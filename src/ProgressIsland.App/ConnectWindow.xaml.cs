using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProgressIsland.App;

public sealed record ProcessChoice(int Id, string Name, string Title)
{
    public string Label => $"{Name}  ·  PID {Id}" + (Title.Length > 0 ? "\n" + Title : "");
}
public partial class ConnectWindow : Window
{
    private readonly App app;
    private string mode = "command";
    private List<ProcessChoice> processes = [];
    public ConnectWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        ApiExample.Text = ScriptExample();
        ApplyMode(); LoadProcesses();
    }
    internal static string ScriptExample() => "$cfg = Get-Content \"$env:LOCALAPPDATA\\ProgressIsland\\settings.json\" -Raw | ConvertFrom-Json\n$body = @{id='my-job';title='数据处理';progress=35;detail='处理第 35 批';status='running'} | ConvertTo-Json\nInvoke-RestMethod -Method Post -Uri \"http://127.0.0.1:$($cfg.port)/tasks\" -Headers @{Authorization=\"Bearer $($cfg.token)\"} -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))";
    private void ChooseMode(object sender, RoutedEventArgs e) { mode = (string)((Button)sender).Tag; ApplyMode(); }
    private void ApplyMode()
    {
        CommandPane.Visibility = mode == "command" ? Visibility.Visible : Visibility.Collapsed;
        ProcessPane.Visibility = mode == "process" ? Visibility.Visible : Visibility.Collapsed;
        LogPane.Visibility = mode == "log" ? Visibility.Visible : Visibility.Collapsed;
        ApiPane.Visibility = mode == "api" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { CommandTab, ProcessTab, LogTab, ApiTab })
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString((string)button.Tag == mode ? "#4A3A67" : "#262834"));
        ConnectButton.Content = mode switch { "command" => "开始运行", "api" => "完成", _ => "开始监控" };
        ErrorText.Text = "";
    }
    private void LoadProcesses()
    {
        processes = [];
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { if (process.Id != Environment.ProcessId) processes.Add(new(process.Id, process.ProcessName, process.MainWindowTitle)); }
                catch { }
            }
        }
        ApplyFilter();
    }
    private void ApplyFilter()
    {
        if (ProcessList is null || ProcessFilter is null) return;
        var text = ProcessFilter.Text.Trim();
        ProcessList.ItemsSource = processes.Where(p => text.Length == 0 ? p.Title.Length > 0 : p.Label.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Title.Length > 0).ThenBy(p => p.Name).Take(200).ToArray();
    }
    private void FilterProcesses(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void RefreshProcesses(object sender, RoutedEventArgs e) => LoadProcesses();
    private void BrowseLog(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "日志 / 文本|*.log;*.txt;*.jsonl;*.out|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) LogPath.Text = dialog.FileName;
    }
    private void CopyExample(object sender, RoutedEventArgs e) { Clipboard.SetText(ApiExample.Text); ErrorText.Text = "示例已复制"; }
    private void Connect(object sender, RoutedEventArgs e)
    {
        try
        {
            switch (mode)
            {
                case "command": app.Runtime.RunCommand(CommandText.Text, TaskTitle.Text, DirectoryText.Text); break;
                case "process":
                    if (ProcessList.SelectedItem is not ProcessChoice process) throw new ArgumentException("请先选择一个程序");
                    var item = app.Runtime.Attach(process.Id);
                    if (!string.IsNullOrWhiteSpace(TaskTitle.Text)) app.Store.Update(new() { Id = item.Id, Title = TaskTitle.Text });
                    break;
                case "log": app.Runtime.WatchLog(LogPath.Text, TaskTitle.Text); break;
            }
            app.Store.Save(); DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }
    private void Cancel(object sender, RoutedEventArgs e) => Close();
}
