using Microsoft.Win32;

namespace ProgressIsland.App;

public sealed class StartupRegistration(string keyPath = StartupRegistration.RunKeyPath)
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ProgressIsland";

    public string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public bool IsRegistered => !string.IsNullOrWhiteSpace(ReadCommand());

    public static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.Contains('"') || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择任务岛程序的完整 EXE 路径。", nameof(executablePath));
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("任务岛程序不存在，请重新打开程序后启用自启动。", fullPath);
        return $"\"{fullPath}\" --startup";
    }

    public void SetEnabled(bool enabled, string? executablePath = null)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        var command = BuildCommand(executablePath ?? throw new ArgumentNullException(nameof(executablePath)));
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new IOException("无法打开当前用户的自启动设置。");
        if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.Ordinal))
            key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
