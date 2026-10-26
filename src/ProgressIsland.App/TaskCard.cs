using System.ComponentModel;
using System.Windows.Media;
using ProgressIsland.Core;

namespace ProgressIsland.App;

public sealed class TaskCard(WorkItem item, int idleMinutes = TaskGrouping.DefaultIdleMinutes) : INotifyPropertyChanged
{
    private string? previousMetadata;
    private string? previousDetail;
    private bool idle = TaskGrouping.Classify(item, DateTimeOffset.UtcNow, idleMinutes) == TaskGroup.Idle;
    private static Brush Ink(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private static readonly Brush CompletedInk = Ink("#6DE2B3"), FailedInk = Ink("#FF8B9E"), QuietInk = Ink("#9A9FB5"), WaitingInk = Ink("#F1C77E"), RunningInk = Ink("#BBA7FF");
    public WorkItem Item { get; private set; } = item;
    public string Id => Item.Id;
    public string Title => Item.Title;
    public string CompactTitle => Item.Source == "codex" && Item.Title.StartsWith("Codex · ", StringComparison.Ordinal) ? Item.Title[8..] : Item.Title;
    public string Detail => idle ? Item.Status is TaskStates.Paused or TaskStates.Waiting ? "暂停或等待中的任务单独存放 · 恢复运行后自动移回" : $"{TaskGrouping.ActivityAge(Item, DateTimeOffset.UtcNow)}无新活动 · 有新活动后自动回到运行中" : Item.Detail;
    public bool IsIdle => idle;
    public bool IsUnread => Item.IsUnread;
    public bool NeedsConfirmation => Item.IsUnread && TaskStates.IsTerminal(Item.Status);
    public string CompactLabel => NeedsConfirmation ? "已结束 · 待确认" : PercentLabel;
    public string CompactTip => Item.Title + "\n" + (NeedsConfirmation ? "点击确认结果；右键可打开会话或详情" : State + " · 点击打开") ;
    public string OpenLabel => Item.Source == "codex" ? "打开会话" : "查看";
    public double Percent => Item.Progress ?? 0;
    public bool Indeterminate => !idle && Item.Progress is null && Item.Status == TaskStates.Running;
    public string PercentLabel => idle ? "闲置" : Item.Source == "codex" ? TaskStates.IsTerminal(Item.Status) ? "已结束" : Item.Status == TaskStates.Paused ? "已暂停" : Item.Status == TaskStates.Waiting ? "等待中" : "运行中" : Item.Progress is { } n ? $"{n:0.#}%" : TaskStates.IsTerminal(Item.Status) ? "—" : "进度未知";
    public string State => idle ? "闲置 · " + (Item.Status == TaskStates.Paused ? "已暂停" : Item.Status == TaskStates.Waiting ? "等待中" : "原记录仍为运行中") : Item.Source == "codex" && Item.Status == TaskStates.Completed ? "运行结束 · 本轮已完成" : Item.Source == "codex" && Item.Status == TaskStates.Interrupted ? "运行结束 · 本轮中断" : Item.Status switch
    {
        TaskStates.Completed => "已完成", TaskStates.Failed => "失败", TaskStates.Exited => "已退出 · 未验证",
        TaskStates.Interrupted => "监控中断", TaskStates.Paused => "已暂停", TaskStates.Waiting => "等待中", _ => "运行中"
    };
    public string Glyph => Item.Source switch { "codex" => "C", "command" or "terminal" => "›_", "process" => "▦", "log" => "≡", _ => "↗" };
    public Brush Color => idle ? QuietInk : Item.Status switch
    {
        TaskStates.Completed => CompletedInk, TaskStates.Failed => FailedInk, TaskStates.Exited or TaskStates.Interrupted => QuietInk,
        TaskStates.Paused or TaskStates.Waiting => WaitingInk, _ => RunningInk
    };
    public string Metadata
    {
        get
        {
            var source = Item.Source switch { "codex" => "Codex 会话", "command" => "命令输出", "terminal" => "终端命令", "process" => "软件 / 进程", "log" => "日志跟随", _ => "脚本上报" };
            var elapsed = (Item.FinishedAt ?? DateTimeOffset.UtcNow) - Item.StartedAt;
            var time = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m" : elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s" : $"{elapsed.Seconds}s";
            var stats = Item.CpuPercent is { } cpu ? $" · CPU {cpu:0.#}%" : "";
            return $"{source} · {time}{stats}";
        }
    }
    public void Refresh(WorkItem item, int? idleMinutesOverride = null)
    {
        var previous = Item; Item = item;
        if (idleMinutesOverride is { } minutes) idleMinutes = minutes;
        var previousIdle = idle;
        idle = TaskGrouping.Classify(item, DateTimeOffset.UtcNow, idleMinutes) == TaskGroup.Idle;
        var groupingChanged = previousIdle != idle;
        void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
        if (previous.Title != item.Title) { Changed(nameof(Title)); Changed(nameof(CompactTitle)); Changed(nameof(CompactTip)); }
        var detail = Detail;
        if (detail != previousDetail) { previousDetail = detail; Changed(nameof(Detail)); }
        if (groupingChanged) Changed(nameof(IsIdle));
        if (previous.IsUnread != item.IsUnread || previous.Status != item.Status)
        { Changed(nameof(IsUnread)); Changed(nameof(NeedsConfirmation)); Changed(nameof(CompactLabel)); Changed(nameof(CompactTip)); }
        if (previous.Source != item.Source) { Changed(nameof(Glyph)); Changed(nameof(OpenLabel)); }
        if (groupingChanged || previous.Progress != item.Progress || previous.Status != item.Status)
        { Changed(nameof(Percent)); Changed(nameof(PercentLabel)); Changed(nameof(Indeterminate)); Changed(nameof(CompactLabel)); Changed(nameof(CompactTip)); }
        if (groupingChanged || previous.Status != item.Status) { Changed(nameof(State)); Changed(nameof(Color)); }
        var metadata = Metadata;
        if (metadata != previousMetadata) { previousMetadata = metadata; Changed(nameof(Metadata)); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
