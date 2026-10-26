namespace ProgressIsland.Core;

public static class TaskStates
{
    public const string Running = "running", Paused = "paused", Waiting = "waiting",
        Completed = "completed", Failed = "failed", Exited = "exited", Interrupted = "interrupted";
    public static bool IsTerminal(string state) => state is Completed or Failed or Exited or Interrupted;
    public static bool IsValid(string state) => state is Running or Paused or Waiting or Completed or Failed or Exited or Interrupted;
}

public sealed record WorkItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "等待进度信息";
    public string Source { get; init; } = "api";
    public string Status { get; init; } = TaskStates.Running;
    public double? Progress { get; init; }
    public int? ProcessId { get; init; }
    public long? ProcessStartTicks { get; init; }
    public string? LogPath { get; init; }
    public double? CpuPercent { get; init; }
    public long? MemoryBytes { get; init; }
    public int? ExitCode { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastActivityAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? SessionId { get; init; }
    public string? TurnId { get; init; }
    public bool IsUnread { get; init; }
    public string? ReadOverride { get; init; }
    public DateTimeOffset? ReadMarkedAt { get; init; }
    public bool? NativeUnread { get; init; }
    public long NativeActivity { get; init; }
}

public sealed record TaskUpdate
{
    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? Detail { get; init; }
    public string? Source { get; init; }
    public string? Status { get; init; }
    public double? Progress { get; init; }
    public bool ClearProgress { get; init; }
    public int? ProcessId { get; init; }
    public long? ProcessStartTicks { get; init; }
    public string? LogPath { get; init; }
    public double? CpuPercent { get; init; }
    public long? MemoryBytes { get; init; }
    public int? ExitCode { get; init; }
    public DateTimeOffset? ActivityAt { get; init; }
}

public sealed record ProgressObservation(double? Percent, string Detail);

public sealed record CodexObservation(string SessionId, string TurnId, string Title, string Status,
    string Detail, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    bool? NativeUnread = null, long NativeActivity = 0, DateTimeOffset? LastActivityAt = null);
