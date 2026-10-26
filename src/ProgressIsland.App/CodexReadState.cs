using System.Text.Json;

namespace ProgressIsland.App;

public sealed record CodexReadState(HashSet<string>? Unread, Dictionary<string, long> Activity)
{
    public static CodexReadState Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        HashSet<string>? unread = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("electron-thread-read-state-v1", out var read) && read.ValueKind == JsonValueKind.Object && read.TryGetProperty("unreadByIdentity", out var identities) && identities.ValueKind == JsonValueKind.Object)
        {
            var identityList = identities.EnumerateObject().ToArray();
            // Never blend identities or remote hosts. Unknown schemas remain unknown.
            if (identityList.Length == 1 && identityList[0].Value.ValueKind == JsonValueKind.Object)
            {
                var local = identityList[0].Value.EnumerateObject().Where(p => p.Name.StartsWith("local:", StringComparison.Ordinal)).ToArray();
                if (local.Length == 1 && local[0].Value.ValueKind == JsonValueKind.Array)
                    unread = local[0].Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToHashSet();
            }
        }
        var activity = new Dictionary<string, long>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("electron-persisted-atom-state", out var atoms) && atoms.ValueKind == JsonValueKind.Object && atoms.TryGetProperty("thread-user-activity-times-v1", out var times) && times.ValueKind == JsonValueKind.Object)
        {
            foreach (var value in times.EnumerateObject())
            {
                try
                {
                    using var key = JsonDocument.Parse(value.Name);
                    if (key.RootElement.ValueKind != JsonValueKind.Array) continue;
                    var parts = key.RootElement.EnumerateArray().ToArray();
                    if (parts.Length == 2 && parts.All(p => p.ValueKind == JsonValueKind.String) && parts[0].GetString() == "local" && value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var timestamp))
                        activity[parts[1].GetString()!] = timestamp;
                }
                catch (JsonException) { }
            }
        }
        return new(unread, activity);
    }
    public static CodexReadState Read(string root)
    {
        try { return Parse(File.ReadAllText(Path.Combine(root, ".codex-global-state.json"))); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(null, []); }
    }
}
