using System.Text.Json;

namespace ProgressIsland.Core;

public sealed class TaskStore
{
    private readonly object gate = new();
    private readonly object saveGate = new();
    private readonly Dictionary<string, WorkItem> tasks = new(StringComparer.Ordinal);
    private readonly string? file;
    public event Action<WorkItem>? Terminal;
    public long Version { get; private set; }
    public string? PersistenceError { get; private set; }
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TaskStore(string? file = null)
    {
        this.file = file;
        if (file is null || !File.Exists(file)) return;
        try
        {
            foreach (var item in JsonSerializer.Deserialize<List<WorkItem>>(File.ReadAllText(file), Json) ?? [])
                tasks[item.Id] = TaskStates.IsTerminal(item.Status) || item.Source is "command" or "codex" ? item : item with
                {
                    Status = TaskStates.Interrupted, FinishedAt = DateTimeOffset.UtcNow,
                    Detail = "监控曾中断；请重新接入，未推断任务结果"
                };
        }
        catch (Exception ex) { PersistenceError = "历史记录读取失败：" + ex.Message; }
    }

    public IReadOnlyList<WorkItem> Snapshot()
    {
        lock (gate) return tasks.Values.OrderBy(t => TaskStates.IsTerminal(t.Status))
            .ThenByDescending(t => t.StartedAt).ToArray();
    }

    public WorkItem? Find(string id) { lock (gate) return tasks.GetValueOrDefault(id); }

    public WorkItem Update(TaskUpdate update)
    {
        if (update.Progress is { } p && (!double.IsFinite(p) || p < 0 || p > 100))
            throw new ArgumentException("progress 必须是 0 到 100 的有限数值");
        if (update.Status is { } s && !TaskStates.IsValid(s)) throw new ArgumentException("无效的任务状态");
        if (update.Source is { } source && source is not ("api" or "command" or "process" or "terminal" or "log" or "codex"))
            throw new ArgumentException("无效的数据来源");
        if (update.Id?.Length > 160 || update.Title?.Length > 200 || update.Detail?.Length > 4000)
            throw new ArgumentException("任务字段过长");
        if (update.Id is not null && string.IsNullOrWhiteSpace(update.Id)) throw new ArgumentException("id 不能为空");
        WorkItem item;
        bool finished;
        lock (gate)
        {
            var id = update.Id ?? Guid.NewGuid().ToString("N");
            var prior = tasks.GetValueOrDefault(id);
            if (prior is null && string.IsNullOrWhiteSpace(update.Title)) throw new ArgumentException("新任务必须提供 title");
            // Late log or process callbacks must never revive a finished task.
            if (prior is not null && TaskStates.IsTerminal(prior.Status)) return prior;
            if (prior is null && tasks.Count >= 500) PruneLocked(400);
            if (prior is null && tasks.Count >= 500) throw new ArgumentException("任务上限为 500，请先移除旧任务");
            var status = update.Status ?? prior?.Status ?? TaskStates.Running;
            var now = DateTimeOffset.UtcNow;
            var sourceName = update.Source ?? prior?.Source ?? "api";
            var changed = prior is null || prior.Status != status ||
                update.Progress is { } progress && progress != prior.Progress ||
                update.Detail is { } detail && detail != prior.Detail;
            var activity = update.ActivityAt ?? (sourceName == "api" || changed || update.CpuPercent > 0.05 ? now : prior?.LastActivityAt ?? prior?.StartedAt ?? now);
            item = (prior ?? new WorkItem { Id = id, Title = update.Title! }) with
            {
                Title = string.IsNullOrWhiteSpace(update.Title) ? prior!.Title : update.Title.Trim(),
                Detail = update.Detail ?? prior?.Detail ?? "等待进度信息",
                Source = sourceName,
                Status = status,
                Progress = status == TaskStates.Completed ? 100 : update.ClearProgress ? null : update.Progress ?? prior?.Progress,
                ProcessId = update.ProcessId ?? prior?.ProcessId,
                ProcessStartTicks = update.ProcessStartTicks ?? prior?.ProcessStartTicks,
                LogPath = update.LogPath ?? prior?.LogPath,
                CpuPercent = update.CpuPercent ?? prior?.CpuPercent,
                MemoryBytes = update.MemoryBytes ?? prior?.MemoryBytes,
                ExitCode = update.ExitCode ?? prior?.ExitCode,
                UpdatedAt = now,
                LastActivityAt = prior?.LastActivityAt > activity ? prior.LastActivityAt : activity,
                FinishedAt = TaskStates.IsTerminal(status) ? now : null,
                IsUnread = TaskStates.IsTerminal(status) || prior?.IsUnread == true
            };
            tasks[id] = item;
            Version++;
            finished = TaskStates.IsTerminal(status);
        }
        if (finished) Terminal?.Invoke(item);
        return item;
    }

    public void Remove(string id) { lock (gate) { if (tasks.Remove(id)) Version++; } }

    public bool RenameObserved(string id, string expectedTitle, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200) return false;
        lock (gate)
        {
            if (!tasks.TryGetValue(id, out var item) || item.Title != expectedTitle || TaskStates.IsTerminal(item.Status)) return false;
            tasks[id] = item with { Title = title, UpdatedAt = DateTimeOffset.UtcNow }; Version++; return true;
        }
    }

    public void MarkRead(string id, bool read)
    {
        lock (gate)
        {
            if (!tasks.TryGetValue(id, out var item)) return;
            tasks[id] = item with { IsUnread = !read, ReadOverride = read ? "read" : "unread", ReadMarkedAt = DateTimeOffset.UtcNow };
            Version++;
        }
    }

    // A Codex conversation can run many turns. A new turn may resume the same session;
    // this deliberately does not weaken monotonic completion for ordinary jobs.
    public WorkItem ObserveCodex(CodexObservation observation, bool announce = false)
    {
        if (!Guid.TryParse(observation.SessionId, out _) || string.IsNullOrWhiteSpace(observation.TurnId) || !TaskStates.IsValid(observation.Status))
            throw new ArgumentException("无效的 Codex 会话状态");
        WorkItem item;
        bool notify;
        lock (gate)
        {
            var id = "codex-" + observation.SessionId;
            var prior = tasks.GetValueOrDefault(id);
            var sameTurn = prior?.TurnId == observation.TurnId;
            var ended = TaskStates.IsTerminal(observation.Status);
            var readOverride = sameTurn ? prior?.ReadOverride : null;
            // Viewing a running turn does not mean its later result has been viewed.
            if (ended && prior is not null && !TaskStates.IsTerminal(prior.Status) && readOverride == "read")
                readOverride = null;
            // Honor a genuinely new native read action, but never erase a manual unread
            // marker merely because a stale native snapshot still says "read".
            var viewed = sameTurn && prior is not null &&
                (observation.NativeActivity > prior.NativeActivity || prior.NativeUnread == true && observation.NativeUnread == false);
            if (viewed)
                readOverride = ended && observation.NativeUnread != false ? "read" : null;
            var unread = readOverride switch
            {
                "read" => false, "unread" => true,
                _ => ended && (viewed ? false : observation.NativeUnread ?? (sameTurn ? prior?.IsUnread ?? true : true))
            };
            item = new()
            {
                Id = id, Title = observation.Title, Source = "codex", SessionId = observation.SessionId,
                TurnId = observation.TurnId, Status = observation.Status, Detail = observation.Detail,
                StartedAt = observation.StartedAt, UpdatedAt = prior?.UpdatedAt ?? DateTimeOffset.UtcNow,
                LastActivityAt = sameTurn && prior?.LastActivityAt > (observation.LastActivityAt ?? observation.StartedAt)
                    ? prior.LastActivityAt : observation.LastActivityAt ?? observation.StartedAt,
                FinishedAt = ended ? observation.FinishedAt ?? (sameTurn ? prior?.FinishedAt : null) ?? DateTimeOffset.UtcNow : null,
                Progress = null, IsUnread = unread, ReadOverride = readOverride,
                ReadMarkedAt = sameTurn ? prior?.ReadMarkedAt : null,
                NativeUnread = observation.NativeUnread, NativeActivity = observation.NativeActivity
            };
            notify = announce && ended && (prior is null || !sameTurn || !TaskStates.IsTerminal(prior.Status));
            if (prior != item) { tasks[id] = item with { UpdatedAt = DateTimeOffset.UtcNow }; item = tasks[id]; Version++; }
        }
        if (notify) Terminal?.Invoke(item);
        return item;
    }
    public void ClearHistory() { lock (gate) { PruneLocked(0); Version++; } }
    private void PruneLocked(int retain)
    {
        foreach (var key in tasks.Values.Where(t => TaskStates.IsTerminal(t.Status))
                     .OrderByDescending(t => t.FinishedAt).Skip(retain).Select(t => t.Id).ToArray()) tasks.Remove(key);
    }

    public void Save()
    {
        if (file is null) return;
        lock (saveGate)
        {
            try
            {
                WorkItem[] snapshot;
                lock (gate) snapshot = tasks.Values.ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temp = file + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Json));
                File.Move(temp, file, true);
                PersistenceError = null;
            }
            catch (Exception ex) { PersistenceError = "历史记录保存失败：" + ex.Message; }
        }
    }
}
