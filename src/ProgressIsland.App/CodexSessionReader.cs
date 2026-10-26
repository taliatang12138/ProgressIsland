using System.Text.Json;
using ProgressIsland.Core;

namespace ProgressIsland.App;

public sealed class CodexSessionReader(string root)
{
    public IReadOnlyList<CodexObservation> Read()
    {
        static int Version(string path) => int.TryParse(Path.GetFileNameWithoutExtension(path).Split('_')[^1], out var version) ? version : -1;
        var statePath = Directory.EnumerateFiles(root, "state_*.sqlite").OrderByDescending(Version).FirstOrDefault();
        var historyPath = Directory.EnumerateFiles(root, "thread_history_*.sqlite").OrderByDescending(Version).FirstOrDefault();
        if (statePath is null || historyPath is null) return [];
        using var state = new ReadOnlySqlite(statePath);
        using var history = new ReadOnlySqlite(historyPath);
        var name = state.HasColumn("threads", "name") ? "COALESCE(NULLIF(name,''),title)" : "title";
        var users = state.HasColumn("threads", "thread_source") ? " AND (thread_source IS NULL OR thread_source='user')" : "";
        var order = state.HasColumn("threads", "updated_at_ms") ? "updated_at_ms" : "updated_at";
        var rollout = state.HasColumn("threads", "rollout_path") ? ",rollout_path" : "";
        var threads = state.Query($"SELECT id,{name} AS title,{order} AS activity{rollout} FROM threads WHERE archived=0{users} ORDER BY {order} DESC");
        var hasItems = history.Query("SELECT name FROM sqlite_master WHERE type='table' AND name='thread_items'").Count > 0;
        var read = CodexReadState.Read(root);
        var titles = Titles();
        var observations = new List<CodexObservation>();
        foreach (var row in threads)
        {
            var id = row.GetValueOrDefault("id");
            if (!Guid.TryParse(id, out _)) continue;
            var turn = history.Query($"SELECT turn_id,status,started_at,completed_at FROM thread_turns WHERE thread_id='{id}' ORDER BY rollout_ordinal DESC LIMIT 1").FirstOrDefault();
            if (turn is null || turn.GetValueOrDefault("turn_id") is not { } turnId) continue;
            var status = turn.GetValueOrDefault("status") switch
            {
                "inProgress" => TaskStates.Running, "completed" => TaskStates.Completed,
                "failed" => TaskStates.Failed, "interrupted" => TaskStates.Interrupted, _ => TaskStates.Waiting
            };
            var detail = status switch
            {
                TaskStates.Completed => "本轮运行结束", TaskStates.Failed => "本轮运行失败",
                TaskStates.Interrupted => "本轮运行中断", TaskStates.Waiting => "等待会话状态", _ => "正在运行 · 本轮尚未结束"
            };
            var title = titles.GetValueOrDefault(id!) ?? row.GetValueOrDefault("title") ?? "Codex 会话";
            title = title.Split('\n')[0].Trim();
            if (title.Length > 140) title = title[..140];
            var started = Timestamp(turn.GetValueOrDefault("started_at")) ?? DateTimeOffset.UtcNow;
            var ended = Timestamp(turn.GetValueOrDefault("completed_at"));
            var activity = ended ?? started;
            var foundWork = false;
            if (hasItems && !TaskStates.IsTerminal(status))
            {
                var latest = history.Query($"SELECT MAX(created_at_ms) AS activity FROM thread_items WHERE thread_id='{id}' AND turn_id='{turnId.Replace("'", "''")}'").FirstOrDefault();
                if (Timestamp(latest?.GetValueOrDefault("activity")) is { } itemTime)
                { foundWork = true; if (itemTime > activity) activity = itemTime; }
            }
            // Read only the file timestamp; never parse private conversation bodies.
            if (!TaskStates.IsTerminal(status) && row.GetValueOrDefault("rollout_path") is { } path && Path.IsPathFullyQualified(path))
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var writeTime = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                        foundWork = true;
                        if (writeTime > activity) activity = writeTime;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            if (!foundWork && !TaskStates.IsTerminal(status) && Timestamp(row.GetValueOrDefault("activity")) is { } metadataTime && metadataTime > activity)
                activity = metadataTime;
            observations.Add(new(id!, turnId, "Codex · " + title, status, detail, started, ended,
                read.Unread is null ? null : read.Unread.Contains(id!), read.Activity.GetValueOrDefault(id!), activity));
        }
        return observations;
    }
    private Dictionary<string, string> Titles()
    {
        var titles = new Dictionary<string, string>();
        var path = Path.Combine(root, "session_index.jsonl");
        if (!File.Exists(path)) return titles;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var value = json.RootElement;
                    if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && value.TryGetProperty("thread_name", out var title) && title.ValueKind == JsonValueKind.String)
                        titles[id.GetString()!] = title.GetString() ?? "Codex 会话";
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
        return titles;
    }
    private static DateTimeOffset? Timestamp(string? value)
    {
        if (!long.TryParse(value, out var number) || number <= 0) return null;
        try { return number > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(number) : DateTimeOffset.FromUnixTimeSeconds(number); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
