using System.Windows;
using System.Windows.Controls;
using ProgressIsland.Core;

namespace ProgressIsland.App;

public sealed class TaskDetailsWindow : Window
{
    public TaskDetailsWindow(App app, WorkItem item)
    {
        Style = (Style)app.FindResource(typeof(Window));
        Title = "任务详情 · 任务岛"; Width = 560; Height = 430; MinWidth = 500; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(25) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = item.Title, FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        var text = new TextBox
        {
            Text = $"{new TaskCard(item).State}  ·  {new TaskCard(item).PercentLabel}\n\n{item.Detail}\n\n来源：{new TaskCard(item).Metadata}\nPID：{item.ProcessId?.ToString() ?? "—"}\n退出码：{item.ExitCode?.ToString() ?? "—"}\n\n" +
                (item.LogPath is not null ? "日志：" + item.LogPath + "\n\n" : "") +
                "没有可靠的结果信息时，可在确认实际工作结果后手动标记。移除监控不会结束工作。",
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(text, 1); root.Children.Add(text);
        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        void Add(string title, Action action, bool enabled = true)
        {
            var button = new Button { Content = title, Margin = new Thickness(5, 0, 0, 5), IsEnabled = enabled };
            button.Click += (_, _) => action(); buttons.Children.Add(button);
        }
        if (item.Source is "process" or "terminal" && item.ProcessId is { } pid)
            Add("切回软件", () => Native.FocusProcess(pid, item.ProcessStartTicks));
        if (item.Source is "process" or "terminal" or "log" or "api")
        {
            Add("标记失败", () => { app.Store.Update(new() { Id = item.Id, Status = TaskStates.Failed, Detail = "由你确认工作失败" }); app.Store.Save(); Close(); }, !TaskStates.IsTerminal(item.Status));
            Add("标记完成", () => { app.Store.Update(new() { Id = item.Id, Status = TaskStates.Completed, Detail = "由你确认工作已完成" }); app.Store.Save(); Close(); }, !TaskStates.IsTerminal(item.Status));
        }
        Add("关闭", Close); Grid.SetRow(buttons, 2); root.Children.Add(buttons); Content = root;
    }
}
