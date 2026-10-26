using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ProgressIsland.Core;
using Forms = System.Windows.Forms;

namespace ProgressIsland.App;

public partial class App : Application
{
    public Settings Settings { get; private set; } = null!;
    public TaskStore Store { get; private set; } = null!;
    public TaskRuntime Runtime { get; private set; } = null!;
    public LocalApi Api { get; private set; } = null!;
    public CodexMonitor Codex { get; private set; } = null!;
    public bool IsPreview { get; private set; }
    public string? StartupError { get; private set; }
    private readonly StartupRegistration startup = new();
    private Forms.NotifyIcon? tray;
    private Mutex? instance;
    private DispatcherTimer? save;
    private MainWindow? island;
    private int saving;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0] == "--verify-codex")
        {
            var exitCode = 0;
            try
            {
                var root = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                var clock = Stopwatch.StartNew();
                var observations = new CodexSessionReader(root).Read();
                var verificationStore = new TaskStore();
                foreach (var observation in observations) verificationStore.ObserveCodex(observation);
                var snapshot = verificationStore.Snapshot();
                var report = new
                {
                    version = typeof(App).Assembly.GetName().Version?.ToString(),
                    sessions = snapshot.Count, running = snapshot.Count(t => TaskGrouping.Classify(t, DateTimeOffset.UtcNow) == TaskGroup.Running),
                    idle = snapshot.Count(t => TaskGrouping.Classify(t, DateTimeOffset.UtcNow) == TaskGroup.Idle),
                    ended = snapshot.Count(t => TaskStates.IsTerminal(t.Status)), unread = snapshot.Count(t => t.IsUnread),
                    hasInventedPercentage = snapshot.Any(t => t.Progress is not null), readMs = clock.Elapsed.TotalMilliseconds
                };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!);
                File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(report, TaskStore.Json));
                if (snapshot.Count == 0) exitCode = 2;
            }
            catch (Exception ex)
            { File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message }, TaskStore.Json)); exitCode = 1; }
            Environment.Exit(exitCode); return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--probe" && int.TryParse(e.Args[1], out var pid))
        { Environment.Exit(ProgressProbe.Run(pid)); return; }
        if (e.Args.Length >= 1 && e.Args[0] == "--probe-worker")
        { Environment.Exit(ProgressProbe.RunWorker()); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--runner")
        { Environment.Exit(CommandRunner.RunAsync(e.Args[1]).GetAwaiter().GetResult()); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--fixture")
        { new ProbeFixture(e.Args[1]).Show(); return; }
        IsPreview = e.Args.Length >= 2 && e.Args[0] is "--preview" or "--verify-ui";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Settings.DataDirectory)))[..16];
        instance = new Mutex(true, "Local\\ProgressIsland-" + key, out var first);
        if (!first && !IsPreview)
        {
            if (!e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
                MessageBox.Show("任务岛已在运行。请从系统托盘打开，或按 Ctrl+Alt+P。", "任务岛");
            Shutdown(); return;
        }
        try
        {
            Settings = IsPreview ? new Settings { AutoDiscover = false, Pinned = true } : Settings.Load();
            if (!IsPreview)
            {
                try
                {
                    Settings.StartWithWindows = startup.IsRegistered;
                    if (e.Args.Contains("--disable-startup", StringComparer.OrdinalIgnoreCase))
                    { startup.SetEnabled(false); Settings.StartWithWindows = false; }
                    else if (Settings.StartWithWindows || e.Args.Contains("--enable-startup", StringComparer.OrdinalIgnoreCase))
                    { startup.SetEnabled(true, ExecutablePath()); Settings.StartWithWindows = true; }
                }
                catch (Exception ex) { StartupError = "开机自启动设置失败：" + ex.Message; }
                SaveSettings();
            }
            Store = new(IsPreview ? null : Path.Combine(Settings.DataDirectory, "tasks.json"));
            Runtime = new(Store, Settings); Api = new(Store, Settings); Codex = new(Store, Settings);
            if (IsPreview) SeedPreview();
            island = new MainWindow(this); MainWindow = island;
            island.Show();
            if (IsPreview)
            {
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(400);
                    if (e.Args[0] == "--verify-ui")
                    {
                        var result = await island.VerifyDockLayoutsAsync();
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!);
                        File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(result, TaskStore.Json));
                    }
                    else if (e.Args.Length > 2 && e.Args[2] is "connect" or "settings")
                    {
                        Window previewWindow = e.Args[2] == "connect" ? new ConnectWindow(this) : new SettingsWindow(this);
                        previewWindow.Show(); await Task.Delay(200); global::ProgressIsland.App.MainWindow.RenderWindow(previewWindow, e.Args[1]); previewWindow.Close();
                    }
                    else if (e.Args.Length > 2 && e.Args[2].StartsWith("dock-", StringComparison.Ordinal))
                    {
                        var parts = e.Args[2].Split('-');
                        Settings.Pinned = false;
                        island.DockToSide(Enum.Parse<DockSide>(parts[1], true), 0.5, false);
                        island.RenderPreview(e.Args[1], parts.Length > 2 && parts[2] == "expanded");
                    }
                    else if (e.Args.Length > 2 && e.Args[2] == "ended")
                    { island.ShowEndedPreview(); island.RenderPreview(e.Args[1]); }
                    else if (e.Args.Length > 2 && e.Args[2] == "idle")
                    { island.ShowIdlePreview(); island.RenderPreview(e.Args[1]); }
                    else if (e.Args.Length > 2 && e.Args[2] == "custom-idle")
                    { island.SetExpanded(true, false); island.ShowCustomIdlePreview(); island.RenderPreview(e.Args[1]); }
                    else island.RenderPreview(e.Args[1], e.Args.Length <= 2 || e.Args[2] != "compact");
                    Shutdown();
                }, DispatcherPriority.ApplicationIdle);
                return;
            }
            ConfigureTray();
            Store.Terminal += item => Dispatcher.BeginInvoke(() => NotifyEnded(item));
            Api.Start(); Runtime.Start(); Codex.Start();
            save = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            save.Tick += (_, _) =>
            {
                if (Interlocked.CompareExchange(ref saving, 1, 0) != 0) return;
                _ = Task.Run(() => { try { Store.Save(); } finally { Volatile.Write(ref saving, 0); } });
            };
            save.Start();
            DispatcherUnhandledException += (_, args) =>
            {
                File.AppendAllText(Path.Combine(Settings.DataDirectory, "errors.log"), DateTimeOffset.Now + " " + args.Exception + Environment.NewLine);
                args.Handled = true;
            };
        }
        catch (Exception ex) { MessageBox.Show("任务岛未能启动：" + ex.Message, "任务岛"); Shutdown(1); }
    }

    private void ConfigureTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏任务岛", null, (_, _) => Dispatcher.BeginInvoke(() => island?.ToggleVisible()));
        menu.Items.Add("接入任务…", null, (_, _) => Dispatcher.BeginInvoke(() =>
        { island!.Show(); new ConnectWindow(this) { Owner = island }.ShowDialog(); island.SetExpanded(true); }));
        menu.Items.Add("设置…", null, (_, _) => Dispatcher.BeginInvoke(() => new SettingsWindow(this) { Owner = island }.ShowDialog()));
        var dockMenu = new Forms.ToolStripMenuItem("停靠位置（也可直接拖动）");
        foreach (var (side, label) in new[] { (DockSide.Top, "上边缘 · 横向"), (DockSide.Bottom, "下边缘 · 横向"), (DockSide.Left, "左边缘 · 竖向"), (DockSide.Right, "右边缘 · 竖向") })
            dockMenu.DropDownItems.Add(label, null, (_, _) => Dispatcher.BeginInvoke(() => { island?.DockToSide(side); island?.Show(); }));
        menu.Items.Add(dockMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出（保留已启动的工作）", null, (_, _) => Dispatcher.BeginInvoke(Quit));
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "任务岛 · Progress Island", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(() => island?.ToggleVisible()); };
        tray.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(() => { island?.Show(); island?.SetExpanded(true); });
    }
    private void NotifyEnded(WorkItem item)
    {
        if (!Settings.Notify) return;
        island?.Announce(item);
        tray?.ShowBalloonTip(4000, new TaskCard(item).State + " · " + item.Title, item.Detail[..Math.Min(220, item.Detail.Length)],
            item.Status == TaskStates.Failed ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        if (Settings.Sound) System.Media.SystemSounds.Asterisk.Play();
    }
    public void SaveSettings()
    {
        if (IsPreview) return;
        try { Settings.Save(); }
        catch (Exception ex) { MessageBox.Show("设置未能保存：" + ex.Message, "任务岛"); }
    }
    private static string ExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (path is null || Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请通过 ProgressIsland.exe 启动后启用开机自启动。");
        return path;
    }
    public bool SetStartWithWindows(bool enabled)
    {
        if (IsPreview) return true;
        try
        {
            startup.SetEnabled(enabled, enabled ? ExecutablePath() : null);
            Settings.StartWithWindows = enabled; StartupError = null;
            return true;
        }
        catch (Exception ex)
        {
            StartupError = "开机自启动设置失败：" + ex.Message;
            MessageBox.Show(StartupError, "任务岛");
            return false;
        }
    }
    public void Quit() => Shutdown();
    public bool OpenCodexSession(WorkItem item)
    {
        if (item.Source != "codex" || !Guid.TryParse(item.SessionId, out var id)) return false;
        try
        {
            Process.Start(new ProcessStartInfo($"codex://threads/{id:D}") { UseShellExecute = true });
            Store.MarkRead(item.Id, true);
            return true;
        }
        catch (Exception ex)
        { MessageBox.Show("会话未能打开，已保留未读标记：" + ex.Message, "任务岛"); return false; }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        save?.Stop(); Codex?.Dispose(); Runtime?.Dispose(); Api?.Dispose(); Store?.Save();
        tray?.Dispose(); instance?.Dispose(); base.OnExit(e);
    }
    private void SeedPreview()
    {
        Store.Update(new() { Id = "preview-python", Title = "Python · batch_process.py", Source = "terminal", Progress = 68, Detail = "正在处理第 68 / 100 个样本" });
        Store.Update(new() { Id = "preview-render", Title = "Blender · 场景渲染", Source = "process", Progress = 42, Detail = "来自软件公开的进度条 · 渲染第 42 帧" });
        Store.Update(new() { Id = "preview-matlab", Title = "MATLAB · 数值计算", Source = "process", Detail = "软件未公开任务进度；持续监控进程状态", CpuPercent = 24.3 });
        Store.ObserveCodex(new("11111111-1111-4111-8111-111111111111", "demo-running", "Codex · 构建任务进度面板", TaskStates.Running, "正在运行 · 本轮尚未结束", DateTimeOffset.UtcNow.AddMinutes(-3), null));
        Store.ObserveCodex(new("22222222-2222-4222-8222-222222222222", "demo-ended", "Codex · 整理导出脚本", TaskStates.Completed, "本轮运行结束 · 尚未查看", DateTimeOffset.UtcNow.AddMinutes(-8), DateTimeOffset.UtcNow.AddMinutes(-1), true));
        Store.ObserveCodex(new("33333333-3333-4333-8333-333333333333", "demo-idle", "Codex · 上个月的实验任务", TaskStates.Running, "原轮次仍未记录结束", DateTimeOffset.UtcNow.AddDays(-42), null, false, LastActivityAt: DateTimeOffset.UtcNow.AddDays(-28)));
    }
}
