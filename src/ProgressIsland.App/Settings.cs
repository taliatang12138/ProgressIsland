using System.Security.Cryptography;
using System.Text.Json;

namespace ProgressIsland.App;

public sealed class Settings
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("PROGRESS_ISLAND_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProgressIsland");
    public bool AutoDiscover { get; set; } = true;
    public bool TrackCodex { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public int IdleAfterMinutes { get; set; } = Core.TaskGrouping.DefaultIdleMinutes;
    public HashSet<string> DismissedCodexSessions { get; set; } = [];
    public bool ReadProgressBars { get; set; } = true;
    public bool Notify { get; set; } = true;
    public bool Sound { get; set; }
    public bool Pinned { get; set; }
    public double? Left { get; set; }
    public double Top { get; set; } = 14;
    public string DockSide { get; set; } = "top";
    public string? DockMonitor { get; set; }
    public double DockAnchor { get; set; } = 0.5;
    public int Port { get; set; } = 47832;
    public string Token { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public List<string> IgnoredProcesses { get; set; } = [];

    public static Settings Load()
    {
        try
        {
            var path = Path.Combine(DataDirectory, "settings.json");
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Core.TaskStore.Json);
                if (settings is { Port: > 1024 and <= 65535 } && settings.Token.Length == 64) return settings;
            }
        }
        catch { }
        return new();
    }
    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var path = Path.Combine(DataDirectory, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Core.TaskStore.Json));
        File.Move(path + ".tmp", path, true);
    }
}
