using System.Diagnostics;
using System.Windows;

namespace ProgressIsland.App;

public partial class SettingsWindow : Window
{
    private readonly App app;
    public SettingsWindow(App app)
    {
        this.app = app; InitializeComponent();
        StartWithWindows.IsChecked = app.Settings.StartWithWindows;
        StartWithWindows.IsEnabled = !app.IsPreview;
        StartupStatus.Text = app.IsPreview ? "界面预览不会修改开机自启动。" : app.StartupError ?? "仅对当前 Windows 账号生效，无需管理员权限。";
        TrackCodex.IsChecked = app.Settings.TrackCodex;
        IdleSummary.Text = "当前闲置时间：" + Core.TaskGrouping.DurationLabel(app.Settings.IdleAfterMinutes);
        Auto.IsChecked = app.Settings.AutoDiscover; ReadBars.IsChecked = app.Settings.ReadProgressBars;
        Notify.IsChecked = app.Settings.Notify; Sound.IsChecked = app.Settings.Sound;
        Endpoint.Text = $"http://127.0.0.1:{app.Settings.Port}";
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        if (!app.SetStartWithWindows(StartWithWindows.IsChecked == true)) return;
        app.Settings.TrackCodex = TrackCodex.IsChecked == true;
        app.Settings.AutoDiscover = Auto.IsChecked == true; app.Settings.ReadProgressBars = ReadBars.IsChecked == true;
        app.Settings.Notify = Notify.IsChecked == true; app.Settings.Sound = Sound.IsChecked == true;
        app.SaveSettings(); Close();
    }
    private void OpenData(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", Settings.DataDirectory) { UseShellExecute = true });
    private void ClearHistory(object sender, RoutedEventArgs e)
    {
        foreach (var item in app.Store.Snapshot().Where(t => t.Source == "codex" && ProgressIsland.Core.TaskStates.IsTerminal(t.Status)))
            app.Codex.Dismiss(item.Id);
        app.Store.ClearHistory(); app.Store.Save(); app.SaveSettings();
    }
    private void Quit(object sender, RoutedEventArgs e) { Close(); app.Quit(); }
}
