using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProgressIsland.Core;

namespace ProgressIsland.App;

public partial class MainWindow : Window
{
    private readonly App app;
    private readonly ObservableCollection<TaskCard> cards = [];
    private readonly ObservableCollection<TaskCard> compactCards = [];
    private readonly DispatcherTimer collapse = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer hover = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly DispatcherTimer pointerWatch = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly DispatcherTimer screenRefresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool expanded, dialogOpen, dragging, dockReady, animating, suppressHover;
    private TaskGroup? selectedGroup;
    private int animationGeneration, expansionChanges;
    private DockBounds? hoverTriggerBounds;
    private Point? hoverOrigin, verificationPointer;
    private DockScreen screen = null!;
    private DockSide dockSide;
    private double anchor;
    public DockSide DockedSide => dockSide;
    public bool IsExpanded => expanded;
    public MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        TaskList.ItemsSource = cards;
        CompactTasks.ItemsSource = RailTasks.ItemsSource = compactCards;
        screen = DockScreen.Resolve(app.Settings.DockMonitor);
        dockSide = Enum.TryParse<DockSide>(app.Settings.DockSide, true, out var savedSide) ? savedSide : DockSide.Top;
        anchor = double.IsFinite(app.Settings.DockAnchor) ? Math.Clamp(app.Settings.DockAnchor, 0, 1) : 0.5;
        var initial = Docking.Place(screen.Area, dockSide, anchor, false);
        Left = initial.X; Top = initial.Y; Width = initial.Width; Height = initial.Height;
        UpdateChrome();
        collapse.Tick += (_, _) => { collapse.Stop(); if (!PointerInside() && !dragging && !KeepOpen) SetExpanded(false); };
        hover.Tick += (_, _) => { hover.Stop(); if (!expanded && !dragging && !suppressHover && PointerInside()) SetExpanded(true); };
        pointerWatch.Tick += (_, _) => ReconcilePointer();
        screenRefresh.Tick += (_, _) =>
        {
            screenRefresh.Stop();
            if (dragging) return;
            screen = DockScreen.Resolve(app.Settings.DockMonitor);
            hoverOrigin = null; hoverTriggerBounds = null;
            LayoutDock(false, true);
        };
        var refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        refresh.Tick += (_, _) => { if (!dragging && !animating) Refresh(); }; refresh.Start();
        Loaded += (_, _) => { dockReady = true; Refresh(); LayoutDock(false, true); if (app.Settings.Pinned) SetExpanded(true, false); if (!app.IsPreview) pointerWatch.Start(); };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (!Native.RegisterToggle(handle)) Footer.Text = "快捷键被占用；可从托盘显示";
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x0312) { ToggleVisible(); handled = true; }
                if (msg is 0x02E0 or 0x007E or 0x001A && !dragging) { screenRefresh.Stop(); screenRefresh.Start(); }
                return IntPtr.Zero;
            });
        };
        Closed += (_, _) => { hover.Stop(); collapse.Stop(); pointerWatch.Stop(); screenRefresh.Stop(); refresh.Stop(); Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, 1); };
    }
    private DockBounds WindowBounds => new(Left, Top, Width, Height);
    private Point PointerPosition()
    {
        if (verificationPointer is { } point) return point;
        var physical = System.Windows.Forms.Cursor.Position;
        return new(physical.X / screen.Scale, physical.Y / screen.Scale);
    }
    private bool PointerInside()
    {
        if (!IsVisible) return false;
        var point = PointerPosition();
        return Docking.Contains(WindowBounds, point.X, point.Y)
            || expanded && animating && hoverTriggerBounds is { } trigger && Docking.Contains(trigger, point.X, point.Y);
    }
    private bool KeepOpen => app.Settings.Pinned || dialogOpen || CustomIdlePanel.IsKeyboardFocusWithin;
    private void ReconcilePointer()
    {
        if (app.IsPreview && verificationPointer is null || dragging) return;
        if (!IsVisible) { hover.Stop(); collapse.Stop(); app.Runtime.IsInteracting = false; return; }
        var inside = PointerInside();
        app.Runtime.IsInteracting = inside || dialogOpen;
        if (inside)
        {
            collapse.Stop();
            if (!expanded && !suppressHover && !hover.IsEnabled) hover.Start();
        }
        else
        {
            hover.Stop(); suppressHover = false;
            if (KeepOpen) collapse.Stop();
            else if (expanded && !collapse.IsEnabled)
            { collapse.Interval = TimeSpan.FromMilliseconds(280); collapse.Start(); }
        }
    }
    public void Refresh(bool reflow = true)
    {
        var all = app.Store.Snapshot().Where(t => app.Settings.TrackCodex || t.Source != "codex").ToArray();
        var now = DateTimeOffset.UtcNow;
        TaskGroup Group(WorkItem item) => TaskGrouping.Classify(item, now, app.Settings.IdleAfterMinutes);
        var live = all.Where(t => Group(t) == TaskGroup.Running).ToArray();
        var idle = all.Where(t => Group(t) == TaskGroup.Idle).ToArray();
        var ended = all.Where(t => Group(t) == TaskGroup.Ended).ToArray();
        var unread = all.Count(t => t.IsUnread);
        var attention = TaskGrouping.Attention(all, now, app.Settings.IdleAfterMinutes);
        var shown = selectedGroup is null ? attention.ToArray() : all.Where(t => Group(t) == selectedGroup)
            .OrderByDescending(t => selectedGroup == TaskGroup.Ended ? t.FinishedAt ?? t.StartedAt : TaskGrouping.LastActivity(t)).ToArray();
        SyncCards(cards, shown); SyncCards(compactCards, attention);
        LiveTab.Content = $"当前任务  {attention.Count}";
        IdleTab.Content = $"闲置  {idle.Length}";
        CompactIdle.Content = RailIdle.Content = $"闲置 {idle.Length}";
        CompactEmpty.Visibility = RailEmpty.Visibility = attention.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var unreadEnded = ended.Count(t => t.IsUnread);
        HistoryTab.Content = $"历史记录  {ended.Length}";
        HistoryTab.ToolTip = "包含以往已经结束的会话；只有未确认结果显示在当前任务中";
        HistoryHint.Visibility = selectedGroup == TaskGroup.Ended ? Visibility.Visible : Visibility.Collapsed;
        HistoryHint.Text = $"共 {ended.Length} 条过往结束记录，其中 {unreadEnded} 条待确认；这些不是当前正在运行的任务。";
        IdleThresholdButton.Content = TaskGrouping.DurationLabel(app.Settings.IdleAfterMinutes) + " ▾";
        CompactIdle.ToolTip = RailIdle.ToolTip = $"查看 {TaskGrouping.DurationLabel(app.Settings.IdleAfterMinutes)} 无新活动的任务";
        foreach (var choice in IdleThresholdButton.ContextMenu.Items.OfType<MenuItem>())
            if (int.TryParse(choice.Tag?.ToString(), out var value)) choice.Header = (value == app.Settings.IdleAfterMinutes ? "✓  " : "") + TaskGrouping.DurationLabel(value);
        LiveTab.Background = Brush(selectedGroup is null ? "#3D3357" : "Transparent");
        IdleTab.Background = Brush(selectedGroup == TaskGroup.Idle ? "#3D3357" : "Transparent");
        HistoryTab.Background = Brush(selectedGroup == TaskGroup.Ended ? "#3D3357" : "Transparent");
        PinButton.Content = app.Settings.Pinned ? "●" : "◇";
        PinButton.ToolTip = app.Settings.Pinned ? "已固定 · 点击取消固定" : "保持展开";
        EmptyState.Visibility = shown.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHeading.Text = selectedGroup is null ? "暂无进行中或待确认任务" : selectedGroup == TaskGroup.Idle ? "暂无闲置任务" : "暂无已结束的任务";
        EmptyCaption.Text = selectedGroup == TaskGroup.Ended ? "已确认的结果也会保存在这里。" : selectedGroup == TaskGroup.Idle ? $"{TaskGrouping.DurationLabel(app.Settings.IdleAfterMinutes)} 无新活动的任务会单独放在这里。" : idle.Length > 0 ? $"另有 {idle.Length} 项闲置任务，可从最右侧查看。" : "新的任务和未确认结果会逐项显示在这里。";
        var featured = live.FirstOrDefault(t => t.Progress is not null) ?? live.FirstOrDefault() ?? ended.FirstOrDefault(t => t.IsUnread);
        Headline.Text = $"任务岛   ·   {live.Length} 项进行中";
        CompactDetail.Text = $"{unreadEnded} 项结果待确认" + (idle.Length > 0 ? $" · {idle.Length} 项闲置" : "");
        HeaderUnread.Visibility = RailUnread.Visibility = CompactUnread.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderUnread.ToolTip = RailUnread.ToolTip = CompactUnread.ToolTip = $"{unread} 项未读 · 点击确认或打开对应会话";
        CompactPercent.Text = featured?.Progress is { } p ? $"{p:0}%" : live.Length > 0 ? "•••" : "";
        if (app.Api.Error is not null) Footer.Text = "⚠ 本机接口不可用 · 其他监控仍可用";
        else if (app.Store.PersistenceError is not null) Footer.Text = "⚠ 历史记录保存异常";
        else if (app.Settings.TrackCodex && app.Codex.Error is not null) Footer.Text = "⚠ Codex 会话暂不可读 · 其他监控仍可用";
        else Footer.Text = app.IsPreview ? "●  演示数据 · 界面预览" : "●  本机运行 · 任务不会上传";
        if (reflow && !dragging && !animating) LayoutDock(false);
    }
    private void SyncCards(ObservableCollection<TaskCard> target, IReadOnlyList<WorkItem> shown)
    {
        var ids = shown.Select(t => t.Id).ToHashSet();
        foreach (var stale in target.Where(c => !ids.Contains(c.Id)).ToArray()) target.Remove(stale);
        var existing = target.ToDictionary(c => c.Id);
        for (var i = 0; i < shown.Count; i++)
        {
            if (!existing.TryGetValue(shown[i].Id, out var card)) target.Insert(i, new(shown[i], app.Settings.IdleAfterMinutes));
            else { card.Refresh(shown[i], app.Settings.IdleAfterMinutes); var old = target.IndexOf(card); if (old != i) target.Move(old, i); }
        }
    }
    private double DesiredExpandedHeight(int count) => Math.Min(screen.Area.Height, Math.Clamp(205 + (count == 0 ? 210 : Math.Min(count, 3) * 143)
        + (CustomIdlePanel.Visibility == Visibility.Visible ? 40 : 0) + (IdleValidation.Visibility == Visibility.Visible ? 20 : 0) + (HistoryHint.Visibility == Visibility.Visible ? 28 : 0), 350, 710));
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    public void SetExpanded(bool value, bool animate = true)
    {
        if (dragging) return;
        if (expanded == value) { LayoutDock(false); return; }
        hover.Stop(); collapse.Stop();
        hoverOrigin = null; hoverTriggerBounds = null;
        if (value && (!app.IsPreview || verificationPointer is not null))
        {
            var point = PointerPosition();
            if (Docking.Contains(WindowBounds, point.X, point.Y))
            { hoverOrigin = point; hoverTriggerBounds = WindowBounds; }
        }
        expanded = value;
        expansionChanges++;
        if (!value) selectedGroup = null;
        UpdateChrome(); Refresh(false); LayoutDock(animate);
    }
    private void UpdateChrome()
    {
        var rail = Docking.IsVertical(dockSide) && !expanded;
        PanelContent.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;
        Header.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        CompactHeader.Visibility = !expanded && !rail ? Visibility.Visible : Visibility.Collapsed;
        Rail.Visibility = rail ? Visibility.Visible : Visibility.Collapsed;
        ExpandedContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        OuterFrame.Margin = dockSide switch
        {
            DockSide.Top => new(9, 0, 9, 9), DockSide.Bottom => new(9, 9, 9, 0),
            DockSide.Left => new(0, 9, 9, 9), _ => new(9, 9, 0, 9)
        };
        Shell.CornerRadius = dockSide switch
        {
            DockSide.Top => new(0, 0, 25, 25), DockSide.Bottom => new(25, 25, 0, 0),
            DockSide.Left => new(0, 25, 25, 0), _ => new(25, 0, 0, 25)
        };
    }
    private void FreezeAnimations()
    {
        var current = new DockBounds(Left, Top, Width, Height);
        ++animationGeneration; animating = false;
        BeginAnimation(LeftProperty, null); BeginAnimation(TopProperty, null);
        BeginAnimation(WidthProperty, null); BeginAnimation(HeightProperty, null);
        Left = current.X; Top = current.Y; Width = current.Width; Height = current.Height;
    }
    private void LayoutDock(bool animate, bool force = false)
    {
        if (!dockReady || dragging) return;
        var target = Docking.Place(screen.Area, dockSide, anchor, expanded, DesiredExpandedHeight(cards.Count), compactCards.Count);
        var previous = new DockBounds(Left, Top, Width, Height);
        if (expanded && hoverOrigin is { } origin)
        {
            target = Docking.KeepPointerInside(screen.Area, dockSide, target, origin.X, origin.Y);
            var point = PointerPosition();
            if (Docking.Contains(previous, point.X, point.Y) && !Docking.Contains(target, point.X, point.Y))
            {
                hoverOrigin = point;
                target = Docking.KeepPointerInside(screen.Area, dockSide, target, point.X, point.Y);
            }
        }
        if (!force && Math.Abs(previous.X - target.X) + Math.Abs(previous.Y - target.Y) + Math.Abs(previous.Width - target.Width) + Math.Abs(previous.Height - target.Height) < 0.1 && !animating) return;
        FreezeAnimations();
        Left = target.X; Top = target.Y; Width = target.Width; Height = target.Height;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!animate || !SystemParameters.ClientAreaAnimation)
        { Native.PlacePhysical(hwnd, target, screen.Scale); return; }
        animating = true;
        var generation = ++animationGeneration;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        void Start(DependencyProperty property, double from, double to, bool last = false)
        {
            var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(140)) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
            if (last) animation.Completed += (_, _) =>
            {
                if (generation != animationGeneration || dragging) return;
                animating = false;
                Native.PlacePhysical(hwnd, target, screen.Scale);
            };
            BeginAnimation(property, animation);
        }
        Start(LeftProperty, previous.X, target.X); Start(TopProperty, previous.Y, target.Y);
        Start(WidthProperty, previous.Width, target.Width); Start(HeightProperty, previous.Height, target.Height, true);
    }
    public void DockToSide(DockSide side, double? position = null, bool persist = true)
    {
        hover.Stop(); collapse.Stop(); FreezeAnimations();
        hoverOrigin = null; hoverTriggerBounds = null;
        dockSide = side; anchor = Math.Clamp(position ?? anchor, 0, 1);
        expanded = app.Settings.Pinned;
        suppressHover = true;
        UpdateChrome(); Refresh(false); LayoutDock(false);
        if (persist) PersistDock();
    }
    private void PersistDock()
    {
        app.Settings.DockSide = dockSide.ToString().ToLowerInvariant();
        app.Settings.DockAnchor = anchor; app.Settings.DockMonitor = screen.Name;
        var closed = Docking.Place(screen.Area, dockSide, anchor, false, compactTasks: compactCards.Count);
        app.Settings.Left = closed.X; app.Settings.Top = closed.Y;
        app.SaveSettings();
    }
    public void ToggleVisible() { if (IsVisible) Hide(); else { Show(); SetExpanded(true); } }
    public void Announce(WorkItem item)
    {
        if (!IsVisible) return;
        selectedGroup = null;
        SetExpanded(true); Refresh();
        collapse.Stop(); collapse.Interval = TimeSpan.FromSeconds(7); collapse.Start();
    }
    private void IslandEnter(object sender, MouseEventArgs e)
        => ReconcilePointer();
    private void IslandLeave(object sender, MouseEventArgs e)
        => ReconcilePointer();
    private void IslandKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { app.Settings.Pinned = false; SetExpanded(false); suppressHover = true; } }
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            while (source is not null) { if (source is Button or TextBox or System.Windows.Controls.Primitives.ScrollBar) return; source = VisualTreeHelper.GetParent(source); }
        }
        e.Handled = true;
        if (e.ClickCount == 2) { TogglePin(sender, e); return; }
        var grab = System.Windows.Forms.Cursor.Position;
        hover.Stop(); collapse.Stop(); FreezeAnimations();
        dragging = true; app.Runtime.IsInteracting = true;
        try { DragMove(); } catch (InvalidOperationException) { }
        finally
        {
            dragging = false;
            var release = System.Windows.Forms.Cursor.Position;
            if (Math.Abs(release.X - grab.X) + Math.Abs(release.Y - grab.Y) >= 4 * screen.Scale)
            {
                var drop = DockScreen.AtCursor(); screen = drop.screen;
                var side = Docking.NearestEdge(screen.Area, drop.x, drop.y);
                DockToSide(side, Docking.AnchorAt(screen.Area, side, drop.x, drop.y));
            }
            else { LayoutDock(false); if (PointerInside()) SetExpanded(true); }
            ReconcilePointer();
        }
    }
    private void AddTask(object sender, RoutedEventArgs e)
    {
        dialogOpen = true;
        try { new ConnectWindow(app) { Owner = this }.ShowDialog(); }
        finally { dialogOpen = false; selectedGroup = null; SetExpanded(true); Refresh(); }
    }
    private void ShowLive(object sender, RoutedEventArgs e) { selectedGroup = null; Refresh(); }
    private void ShowIdle(object sender, RoutedEventArgs e) { selectedGroup = TaskGroup.Idle; SetExpanded(true); Refresh(); }
    private void ShowHistory(object sender, RoutedEventArgs e) { selectedGroup = TaskGroup.Ended; Refresh(); }
    private void OpenIdleThreshold(object sender, RoutedEventArgs e)
    { var menu = IdleThresholdButton.ContextMenu; menu.PlacementTarget = IdleThresholdButton; menu.IsOpen = true; }
    private void IdleMenuOpened(object sender, RoutedEventArgs e) { dialogOpen = true; collapse.Stop(); }
    private void IdleMenuClosed(object sender, RoutedEventArgs e)
    { dialogOpen = false; ReconcilePointer(); }
    private void ChooseIdleThreshold(object sender, RoutedEventArgs e)
    { if (int.TryParse(((FrameworkElement)sender).Tag?.ToString(), out var minutes)) SetIdleThreshold(minutes); }
    private void SetIdleThreshold(int minutes)
    {
        if (minutes is < 1 or > TaskGrouping.MaximumIdleMinutes) return;
        app.Settings.IdleAfterMinutes = minutes;
        CustomIdlePanel.Visibility = IdleValidation.Visibility = Visibility.Collapsed;
        app.SaveSettings(); Refresh();
    }
    private void ShowCustomIdle(object sender, RoutedEventArgs e)
    {
        var minutes = app.Settings.IdleAfterMinutes;
        var unit = minutes % 10080 == 0 ? 10080 : minutes % 1440 == 0 ? 1440 : minutes % 60 == 0 ? 60 : 1;
        IdleWeeksUnit.IsChecked = unit == 10080; IdleDaysUnit.IsChecked = unit == 1440;
        IdleHoursUnit.IsChecked = unit == 60; IdleMinutesUnit.IsChecked = unit == 1;
        IdleAmount.Text = (minutes / unit).ToString();
        CustomIdlePanel.Visibility = Visibility.Visible; IdleValidation.Visibility = Visibility.Collapsed;
        Refresh(); IdleAmount.Focus(); IdleAmount.SelectAll();
    }
    private void ApplyCustomIdle(object sender, RoutedEventArgs e)
    {
        var unit = IdleWeeksUnit.IsChecked == true ? 10080 : IdleDaysUnit.IsChecked == true ? 1440 : IdleHoursUnit.IsChecked == true ? 60 : 1;
        if (!long.TryParse(IdleAmount.Text, out var amount) || amount < 1 || amount > TaskGrouping.MaximumIdleMinutes / unit)
        { IdleValidation.Text = "请输入有效时长，范围为 1 分钟到 365 天。"; IdleValidation.Visibility = Visibility.Visible; Refresh(); return; }
        SetIdleThreshold((int)(amount * unit));
    }
    private void TogglePin(object sender, RoutedEventArgs e) { app.Settings.Pinned = !app.Settings.Pinned; app.SaveSettings(); Refresh(); }
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        dialogOpen = true;
        try { new SettingsWindow(app) { Owner = this }.ShowDialog(); }
        finally { dialogOpen = false; Refresh(); }
    }
    private void RemoveTask(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string id) return;
        if (app.Store.Find(id)?.Source == "codex") { app.Codex.Dismiss(id); app.SaveSettings(); }
        app.Runtime.Remove(id); _ = Task.Run(app.Store.Save); Refresh();
    }
    private void TaskMenuOpening(object sender, ContextMenuEventArgs e) { dialogOpen = true; collapse.Stop(); }
    private void TaskMenuClosing(object sender, ContextMenuEventArgs e)
    { dialogOpen = false; ReconcilePointer(); }
    private void MarkUnread(object sender, RoutedEventArgs e) => SetTaskRead(sender, false);
    private void MarkRead(object sender, RoutedEventArgs e) => SetTaskRead(sender, true);
    private void CompactTaskClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string id || app.Store.Find(id) is not { } item) return;
        if (TaskStates.IsTerminal(item.Status)) SetTaskRead(sender, true);
        else InspectTask(sender, e);
    }
    private void CompactScrollWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer scroll && scroll.ScrollableWidth > 0)
        { scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta); e.Handled = true; }
    }
    private void SetTaskRead(object sender, bool read)
    {
        if (((FrameworkElement)sender).Tag is not string id) return;
        app.Store.MarkRead(id, read); _ = Task.Run(app.Store.Save); Refresh();
    }
    private void InspectTask(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string id || app.Store.Find(id) is not { } item) return;
        if (item.Source == "codex")
        { app.OpenCodexSession(item); _ = Task.Run(app.Store.Save); Refresh(); return; }
        dialogOpen = true;
        try
        {
            var details = new TaskDetailsWindow(app, item) { Owner = this };
            details.Loaded += (_, _) => { app.Store.MarkRead(id, true); _ = Task.Run(app.Store.Save); Refresh(); };
            details.ShowDialog();
        }
        finally { dialogOpen = false; }
    }
    public void RenderPreview(string path, bool expand = true)
    {
        SetExpanded(expand, false); Refresh(); UpdateLayout();
        RenderWindow(this, path);
    }
    public void ShowEndedPreview() { selectedGroup = TaskGroup.Ended; Refresh(); }
    public void ShowIdlePreview() { selectedGroup = TaskGroup.Idle; Refresh(); }
    public void ShowCustomIdlePreview() => ShowCustomIdle(IdleThresholdButton, new RoutedEventArgs());
    public static void RenderWindow(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * 2), (int)(window.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = File.Create(path); encoder.Save(output);
    }

    public async Task<object> VerifyDockLayoutsAsync()
    {
        var checks = new List<string>();
        var calls = new List<double>();
        var gaps = new List<double>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double lastFrame = 0;
        void Frame(object? sender, EventArgs e)
        {
            var now = clock.Elapsed.TotalMilliseconds;
            if (lastFrame > 0) gaps.Add(now - lastFrame);
            lastFrame = now;
        }
        System.Windows.Media.CompositionTarget.Rendering += Frame;
        try
        {
            foreach (var side in Enum.GetValues<DockSide>())
            {
                app.Settings.Pinned = false;
                DockToSide(side, 0.5, false);
                UpdateLayout();
                var vertical = Docking.IsVertical(side);
                if ((ActualHeight > ActualWidth) != vertical || (Rail.Visibility == Visibility.Visible) != vertical)
                    throw new InvalidOperationException(side + " collapsed orientation is incorrect");
                checks.Add(side + ": collapsed orientation and rail visibility");
                var before = new DockBounds(Left, Top, ActualWidth, ActualHeight);
                var call = System.Diagnostics.Stopwatch.StartNew();
                SetExpanded(true); calls.Add(call.Elapsed.TotalMilliseconds);
                await Task.Delay(210);
                UpdateLayout();
                var after = new DockBounds(Left, Top, ActualWidth, ActualHeight);
                var edgeShift = side switch
                {
                    DockSide.Top => Math.Abs(after.Y - before.Y),
                    DockSide.Bottom => Math.Abs(after.Y + after.Height - before.Y - before.Height),
                    DockSide.Left => Math.Abs(after.X - before.X),
                    _ => Math.Abs(after.X + after.Width - before.X - before.Width)
                };
                if (edgeShift > 2 || ExpandedContent.Visibility != Visibility.Visible || !IsExpanded)
                    throw new InvalidOperationException(side + " expansion lost its edge anchor");
                checks.Add(side + ": real animated expansion stays attached to edge");
                SetExpanded(false); await Task.Delay(200); UpdateLayout();
                if (Math.Abs(ActualWidth - before.Width) > 2 || Math.Abs(ActualHeight - before.Height) > 2)
                    throw new InvalidOperationException(side + " did not return to collapsed dimensions");
                checks.Add(side + ": animated collapse restores original dimensions");
            }
            var unreadBefore = app.Store.Snapshot().Count(t => t.IsUnread);
            if (unreadBefore < 1 || HeaderUnread.Visibility != Visibility.Visible || RailUnread.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Unread indicator missing from compact layouts");
            checks.Add("hover and docking preserve unread indicators in both orientations");
            selectedGroup = TaskGroup.Ended; SetExpanded(true, false); Refresh(); UpdateLayout();
            if (cards.Count != 1 || cards[0].Item.Source != "codex" || !cards[0].NeedsConfirmation)
                throw new InvalidOperationException("Ended grouping or unread count is incorrect");
            checks.Add("completed Codex conversation appears in ended group with unread count");
            static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i); yield return child;
                    foreach (var descendant in Descendants(child)) yield return descendant;
                }
            }
            var taskBorder = Descendants(TaskList).OfType<Border>().First(b => b.ContextMenu is not null);
            var menu = taskBorder.ContextMenu;
            menu.PlacementTarget = taskBorder; menu.IsOpen = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var unreadAction = (MenuItem)menu.Items[2];
            var readAction = (MenuItem)menu.Items[3];
            if (unreadAction.Tag as string != cards[0].Id || readAction.Tag as string != cards[0].Id)
                throw new InvalidOperationException("Context menu lost task identity binding");
            checks.Add("actual WPF context menu resolves conversation identity and both read actions");
            readAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (cards[0].IsUnread || HeaderUnread.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Mark read did not clear purple dot");
            unreadAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (!cards[0].IsUnread || HeaderUnread.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Mark unread did not restore purple dot");
            menu.IsOpen = false;
            checks.Add("right-click read and unread actions update real card and compact purple dot");
            selectedGroup = null; Refresh(); UpdateLayout();
            if (cards.Count != 5 || !cards.Any(c => c.NeedsConfirmation) || compactCards.Count != cards.Count)
                throw new InvalidOperationException("Current view omitted running work or unconfirmed result");
            checks.Add("default view combines all current work and unconfirmed ended results");
            if (cards.Any(c => c.IsIdle) || cards.Any(c => c.Item.TurnId == "demo-idle") || !LiveTab.Content.ToString()!.Contains("5"))
                throw new InvalidOperationException("Dormant session leaked into running cards or count");
            checks.Add("running cards and compact count exclude stale in-progress session");
            if (!cards.Select(c => c.Id).SequenceEqual(compactCards.Select(c => c.Id)) || CompactTasks.Items.Count != 5 || RailTasks.Items.Count != 5)
                throw new InvalidOperationException("Compact layouts lost individual task slots");
            checks.Add("both collapsed orientations retain one individual slot for every current task and pending result");
            var idleStack = (StackPanel)IdleTab.Parent;
            if (idleStack.Children[^1] != IdleTab || ((Grid)CompactIdle.Parent).Children[^1] != CompactIdle)
                throw new InvalidOperationException("Idle entry is not the rightmost control");
            checks.Add("idle entries are placed last and at the far right");
            var pending = cards.First(c => c.NeedsConfirmation);
            CompactTaskClick(new Button { Tag = pending.Id }, new RoutedEventArgs());
            if (cards.Any(c => c.Id == pending.Id) || compactCards.Any(c => c.Id == pending.Id) || app.Store.Find(pending.Id) is not { IsUnread: false, Status: TaskStates.Completed })
                throw new InvalidOperationException("Confirming a result did not clear slots while preserving history");
            checks.Add("clicking a pending slot confirms and removes it from current view while retaining history");
            SetTaskRead(new Button { Tag = pending.Id }, false);
            if (!cards.Any(c => c.Id == pending.Id) || !compactCards.Any(c => c.Id == pending.Id))
                throw new InvalidOperationException("Mark unread did not restore pending result slots");
            checks.Add("mark unread restores an acknowledged result to both compact and full current views");
            selectedGroup = TaskGroup.Idle; Refresh(); UpdateLayout();
            if (cards.Count != 1 || !cards[0].IsIdle || cards[0].Indeterminate || cards[0].Item.Status != TaskStates.Running)
                throw new InvalidOperationException("Idle grouping failed to preserve original turn status");
            checks.Add("idle group preserves native state and stops busy animation");
            var idleItem = cards[0].Item;
            app.Store.ObserveCodex(new(idleItem.SessionId!, idleItem.TurnId!, idleItem.Title, idleItem.Status, idleItem.Detail, idleItem.StartedAt, null, false, LastActivityAt: DateTimeOffset.UtcNow));
            Refresh();
            if (cards.Count != 0) throw new InvalidOperationException("Fresh activity did not leave idle group");
            selectedGroup = null; Refresh();
            if (!cards.Any(c => c.Id == idleItem.Id && !c.IsIdle)) throw new InvalidOperationException("Resumed session missing from running group");
            checks.Add("new work activity automatically restores idle session to running group");
            selectedGroup = TaskGroup.Ended; Refresh(); UpdateLayout();
            if (HistoryHint.Visibility != Visibility.Visible || !HistoryHint.Text.Contains("过往结束记录"))
                throw new InvalidOperationException("Historical results are not explained in expanded view");
            checks.Add("history view explains old ended records separately from current work");
            selectedGroup = null; Refresh();
            var recentId = "44444444-4444-4444-8444-444444444444";
            app.Store.ObserveCodex(new(recentId, "threshold-demo", "Codex · 三天前的任务", TaskStates.Running, "无新活动", DateTimeOffset.UtcNow.AddDays(-4), null, false, LastActivityAt: DateTimeOffset.UtcNow.AddDays(-3)));
            OpenIdleThreshold(IdleThresholdButton, new RoutedEventArgs());
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var oneDay = IdleThresholdButton.ContextMenu.Items.OfType<MenuItem>().First(i => i.Tag?.ToString() == "1440");
            oneDay.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); IdleThresholdButton.ContextMenu.IsOpen = false;
            if (app.Settings.IdleAfterMinutes != 1440 || cards.Any(c => c.Id == "codex-" + recentId) || !IdleThresholdButton.Content.ToString()!.Contains("1 天"))
                throw new InvalidOperationException("Inline idle preset did not immediately regroup tasks");
            checks.Add("expanded-view idle preset immediately changes grouping and visible threshold");
            ShowCustomIdle(IdleThresholdButton, new RoutedEventArgs());
            IdleAmount.Text = "3"; IdleHoursUnit.IsChecked = true; ApplyCustomIdle(IdleThresholdButton, new RoutedEventArgs());
            if (app.Settings.IdleAfterMinutes != 180 || CustomIdlePanel.Visibility != Visibility.Collapsed || !IdleThresholdButton.Content.ToString()!.Contains("3 小时"))
                throw new InvalidOperationException("Custom idle hours were not applied");
            checks.Add("inline custom duration supports hours without opening settings");
            ShowCustomIdle(IdleThresholdButton, new RoutedEventArgs());
            IdleAmount.Text = "0"; ApplyCustomIdle(IdleThresholdButton, new RoutedEventArgs());
            if (app.Settings.IdleAfterMinutes != 180 || IdleValidation.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Invalid custom duration was accepted");
            checks.Add("invalid inline custom duration keeps previous threshold and shows validation");
            SetIdleThreshold(TaskGrouping.DefaultIdleMinutes);
            if (!cards.Any(c => c.Id == "codex-" + recentId) || app.Settings.IdleAfterMinutes != 10080)
                throw new InvalidOperationException("Restoring weekly threshold failed to return recent work");
            checks.Add("returning to weekly threshold immediately restores tasks to current view");

            var startupBeforePreview = new StartupRegistration().ReadCommand();
            var settingsPreview = new SettingsWindow(app);
            if (settingsPreview.FindName("StartWithWindows") is not CheckBox { IsEnabled: false, IsChecked: false })
                throw new InvalidOperationException("Startup preference is missing or writable in preview");
            settingsPreview.Close();
            checks.Add("settings display the Windows startup toggle without enabling it in preview");
            app.SetStartWithWindows(true);
            if (new StartupRegistration().ReadCommand() != startupBeforePreview)
                throw new InvalidOperationException("Preview changed the real Windows startup registration");
            checks.Add("preview startup actions leave the real user startup registration unchanged");

            // Exercise the real dispatcher timers and animations using a stationary
            // pointer sample; never move the user's system cursor during self-tests.
            CustomIdlePanel.Visibility = IdleValidation.Visibility = Visibility.Collapsed;
            Keyboard.ClearFocus();
            for (var i = 0; i < 20; i++)
                app.Store.Update(new() { Id = "hover-fixture-" + i, Title = "Hover fixture " + i, Progress = 20 });
            selectedGroup = null; Refresh(); pointerWatch.Start();
            foreach (var side in Enum.GetValues<DockSide>())
            foreach (var fraction in new[] { 0.02, 0.98 })
            {
                verificationPointer = new Point(screen.Area.X - 10000, screen.Area.Y - 10000);
                DockToSide(side, 0.5, false); ReconcilePointer(); UpdateLayout();
                var compact = WindowBounds;
                verificationPointer = new Point(compact.X + compact.Width * (Docking.IsVertical(side) ? 0.5 : fraction),
                    compact.Y + compact.Height * (Docking.IsVertical(side) ? fraction : 0.5));
                var savedAnchor = anchor;
                ReconcilePointer(); await Task.Delay(240); UpdateLayout();
                if (!expanded || !Docking.Contains(WindowBounds, verificationPointer.Value.X, verificationPointer.Value.Y) || anchor != savedAnchor)
                    throw new InvalidOperationException($"{side} hover at {fraction} lost the pointer or changed the saved anchor");
                checks.Add($"{side} strip end {fraction}: animated expansion keeps pointer inside and preserves saved anchor");
                var transitions = expansionChanges;
                for (var frame = 0; frame < 6; frame++)
                {
                    RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                    Refresh(); await Task.Delay(85);
                    if (!expanded || expansionChanges != transitions)
                        throw new InvalidOperationException($"{side} stationary hover oscillated after a layout MouseLeave");
                }
                checks.Add($"{side} strip end {fraction}: stationary hover survives repeated leave events and data refresh without oscillation");
                var stationary = verificationPointer;
                verificationPointer = new Point(compact.X + compact.Width * (Docking.IsVertical(side) ? 0.5 : 1 - fraction),
                    compact.Y + compact.Height * (Docking.IsVertical(side) ? 1 - fraction : 0.5));
                if (!Docking.Contains(WindowBounds, verificationPointer.Value.X, verificationPointer.Value.Y) && PointerInside())
                    throw new InvalidOperationException("Old compact footprint retained an invisible hover region after animation");
                checks.Add($"{side} strip end {fraction}: settled hover region follows visible panel, not old strip footprint");
                verificationPointer = stationary;
                verificationPointer = new Point(screen.Area.X - 10000, screen.Area.Y - 10000);
                ReconcilePointer(); await Task.Delay(470);
                if (expanded || expansionChanges != transitions + 1)
                    throw new InvalidOperationException($"{side} real pointer departure did not collapse exactly once");
                checks.Add($"{side} strip end {fraction}: real departure collapses exactly once");
            }
            var finalCompact = WindowBounds;
            verificationPointer = new Point(finalCompact.X + finalCompact.Width / 2, finalCompact.Y + finalCompact.Height / 2);
            ReconcilePointer(); await Task.Delay(240);
            app.Settings.Pinned = true;
            verificationPointer = new Point(screen.Area.X - 10000, screen.Area.Y - 10000);
            ReconcilePointer(); await Task.Delay(350);
            if (!expanded) throw new InvalidOperationException("Pointer watcher ignored pin state");
            checks.Add("pointer watcher preserves pinned expansion while cursor is outside");
            app.Settings.Pinned = false; dialogOpen = true;
            ReconcilePointer(); await Task.Delay(350);
            if (!expanded) throw new InvalidOperationException("Pointer watcher collapsed an open menu or dialog");
            checks.Add("pointer watcher keeps menus and dialogs open while cursor is outside");
            dialogOpen = false; ReconcilePointer(); await Task.Delay(470);
            if (expanded) throw new InvalidOperationException("Closing a dialog did not resume normal pointer departure");
            checks.Add("closing menus or dialogs resumes normal automatic collapse");
            var escapeCompact = WindowBounds;
            var escapePoint = new Point(escapeCompact.X + escapeCompact.Width / 2, escapeCompact.Y + escapeCompact.Height / 2);
            verificationPointer = escapePoint; ReconcilePointer(); await Task.Delay(240);
            IslandKeyDown(this, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, Key.Escape));
            await Task.Delay(350);
            if (expanded) throw new InvalidOperationException("Escape immediately reopened under stationary cursor");
            checks.Add("Escape keeps the panel collapsed until pointer leaves and returns");
            verificationPointer = new Point(screen.Area.X - 10000, screen.Area.Y - 10000); ReconcilePointer();
            verificationPointer = escapePoint; ReconcilePointer(); await Task.Delay(240);
            if (!expanded) throw new InvalidOperationException("Hover did not recover after Escape and real re-entry");
            checks.Add("real pointer re-entry restores hover after Escape");
        }
        finally { CompositionTarget.Rendering -= Frame; pointerWatch.Stop(); hover.Stop(); collapse.Stop(); verificationPointer = null; }
        return new
        {
            checks, hoverDelayMs = hover.Interval.TotalMilliseconds, animationMs = 140,
            maxExpandHandlerMs = calls.Max(), observedFrames = gaps.Count,
            renderingTier = System.Windows.Media.RenderCapability.Tier >> 16,
            p95FrameGapMs = gaps.Count == 0 ? 0 : gaps.Order().ElementAt((int)((gaps.Count - 1) * 0.95))
        };
    }
}
