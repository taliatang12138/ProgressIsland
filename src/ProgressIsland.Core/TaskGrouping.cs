namespace ProgressIsland.Core;

public enum TaskGroup { Running, Idle, Ended }

public static class TaskGrouping
{
    public const int DefaultIdleMinutes = 7 * 24 * 60;
    public const int MaximumIdleMinutes = 365 * 24 * 60;
    public static string DurationLabel(int minutes) => minutes % (7 * 1440) == 0 ? $"{minutes / (7 * 1440)} 周" : minutes % 1440 == 0 ? $"{minutes / 1440} 天" : minutes % 60 == 0 ? $"{minutes / 60} 小时" : $"{minutes} 分钟";
    public static DateTimeOffset LastActivity(WorkItem item) => item.LastActivityAt ?? item.StartedAt;
    public static TaskGroup Classify(WorkItem item, DateTimeOffset now, int idleMinutes = DefaultIdleMinutes)
    {
        if (TaskStates.IsTerminal(item.Status)) return TaskGroup.Ended;
        return now - LastActivity(item) >= TimeSpan.FromMinutes(Math.Clamp(idleMinutes, 1, MaximumIdleMinutes))
            ? TaskGroup.Idle : TaskGroup.Running;
    }
    public static IReadOnlyList<WorkItem> Attention(IEnumerable<WorkItem> items, DateTimeOffset now, int idleMinutes = DefaultIdleMinutes) =>
        items.Where(item => Classify(item, now, idleMinutes) == TaskGroup.Running || TaskStates.IsTerminal(item.Status) && item.IsUnread)
            .OrderBy(item => TaskStates.IsTerminal(item.Status))
            .ThenByDescending(item => TaskStates.IsTerminal(item.Status) ? item.FinishedAt ?? item.StartedAt : LastActivity(item)).ToArray();
    public static string ActivityAge(WorkItem item, DateTimeOffset now)
    {
        var age = now - LastActivity(item);
        return age.TotalDays >= 1 ? $"{(int)age.TotalDays} 天" : age.TotalHours >= 1 ? $"{(int)age.TotalHours} 小时" : age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes} 分钟" : "不到 1 分钟";
    }
}
