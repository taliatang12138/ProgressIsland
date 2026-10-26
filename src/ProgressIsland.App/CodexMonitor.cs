using ProgressIsland.Core;

namespace ProgressIsland.App;

public sealed class CodexMonitor(TaskStore store, Settings settings) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> dismissed = new(settings.DismissedCodexSessions);
    private readonly object gate = new();
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    public string? Error { get; private set; }
    public int ObservedCount { get; private set; }
    public void Dismiss(string id) { lock (gate) { dismissed.Add(id); settings.DismissedCodexSessions = new(dismissed); } }
    public void Start() => _ = Task.Run(async () =>
    {
        var root = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var reader = new CodexSessionReader(root);
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                if (settings.TrackCodex && Directory.Exists(root))
                {
                    var observations = reader.Read();
                    foreach (var observation in observations)
                    {
                        lock (gate) if (dismissed.Contains("codex-" + observation.SessionId)) continue;
                        store.ObserveCodex(observation, observation.FinishedAt >= started);
                    }
                    ObservedCount = observations.Count; Error = null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { Error = "Codex 会话读取暂不可用：" + ex.Message; }
            await Task.Delay(1500, lifetime.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    });
    public void Dispose() { lifetime.Cancel(); }
}
