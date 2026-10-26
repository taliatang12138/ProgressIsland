using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ProgressIsland.App;

public static class PythonTaskName
{
    public static string? LastReadError { get; private set; }
    private static readonly SemaphoreSlim reader = new(1, 1);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static bool IsPython(string name) => Regex.IsMatch(name, @"^python(?:w|\d+(?:\.\d+)*)?(?:\.exe)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Only the script/module label is returned. Arguments, inline code and secrets
    // never enter task titles, saved history, diagnostics or logs.
    public static string? FromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var memory = CommandLineToArgvW(commandLine.TrimStart(), out var count);
        if (memory == IntPtr.Zero) return null;
        try
        {
            var args = Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size)) ?? "").ToArray();
            if (args.Length == 0) return null;
            if (!IsPython(Leaf(args[0]))) { var terminal = TerminalLabel(args); return terminal[..Math.Min(180, terminal.Length)]; }
            for (var i = 1; i < args.Length; i++)
            {
                var value = args[i];
                if (value == "-c" || value.StartsWith("-c", StringComparison.Ordinal) && value.Length > 2) return "Python · 内联代码";
                if (value == "-m") return i + 1 < args.Length ? Label(args[i + 1], false) : "Python · 模块名称未知";
                if (value.StartsWith("-m", StringComparison.Ordinal) && value.Length > 2) return Label(value[2..], false);
                if (value is "-W" or "-X" or "--check-hash-based-pycs") { i++; continue; }
                if (value == "--") return i + 1 < args.Length ? Label(args[i + 1], true) : "Python · 交互会话";
                if (value == "-") return "Python · 标准输入";
                if (value.StartsWith('-')) continue;
                return Label(value, true);
            }
            return "Python · 交互会话";
        }
        finally { LocalFree(memory); }
    }
    private static string Leaf(string value) => value.Replace('\\', '/').Split('/')[^1];
    private static string Label(string value, bool file)
    {
        value = file ? Leaf(value) : value;
        value = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (value.Length > 140) value = value[..140];
        return "Python · " + (value.Length == 0 ? "代码名称未知" : value);
    }

    public static async Task<string?> ReadProcessAsync(int pid, long? expectedStartTicks = null, CancellationToken cancellation = default)
    {
        var acquired = false;
        try
        {
            acquired = await reader.WaitAsync(TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false);
            if (!acquired) return null;
            var query = Task.Run(() =>
            {
                try { return ReadProcess(pid, expectedStartTicks); }
                finally { reader.Release(); }
            });
            acquired = false; // The query owns the semaphore, even if the caller times out.
            return await query.WaitAsync(TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { LastReadError = ex.GetType().Name; return null; }
        finally { if (acquired) reader.Release(); }
    }
    private static string? ReadProcess(int pid, long? expectedStartTicks)
    {
        object? locator = null, service = null, row = null, properties = null, property = null;
        var stage = "process identity";
        try
        {
            using var process = Process.GetProcessById(pid);
            if (expectedStartTicks is { } ticks && process.StartTime.ToUniversalTime().Ticks != ticks) return null;
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type is null) return null;
            stage = "local WMI activation"; locator = Activator.CreateInstance(type);
            stage = "local WMI connection";
            service = ((dynamic)locator!).ConnectServer(".", "root\\cimv2");
            stage = "process metadata lookup";
            row = ((dynamic)service).Get("Win32_Process.Handle=\"" + pid + "\"");
            stage = "metadata properties"; properties = ((dynamic)row).Properties_;
            stage = "command property"; property = ((dynamic)properties).Item("CommandLine", 0);
            stage = "command value";
            var title = FromCommandLine((string?)((dynamic)property).Value);
            process.Refresh();
            if (!process.HasExited && (expectedStartTicks is null || process.StartTime.ToUniversalTime().Ticks == expectedStartTicks)) { LastReadError = null; return title; }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { LastReadError = stage + ": " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"); }
        finally { Release(property); Release(properties); Release(row); Release(service); Release(locator); }
        return null;
    }
    private static void Release(object? value)
    { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    private static string TerminalLabel(string[] args)
    {
        var executable = Path.GetFileNameWithoutExtension(Leaf(args[0]));
        var next = args.Skip(1).ToArray();
        string Safe(string value) => Regex.IsMatch(value, @"^[\w.@:/-]{1,100}$") && !value.Contains("://", StringComparison.Ordinal) ? value : "";
        if (executable.Equals("node", StringComparison.OrdinalIgnoreCase))
        {
            if (next.Any(a => a is "-e" or "--eval" or "-p" or "--print")) return "终端 · Node 内联代码";
            var index = Array.FindIndex(next, a => !a.StartsWith('-'));
            if (index >= 0)
            {
                var file = Leaf(next[index]);
                if (file is "npm-cli.js" or "npx-cli.js")
                {
                    var command = next.Skip(index + 1).Take(2).Select(Safe).Where(a => a.Length > 0 && !a.StartsWith('-'));
                    return "终端 · " + (file == "npm-cli.js" ? "npm" : "npx") + " " + string.Join(' ', command);
                }
                return "终端 · Node " + file;
            }
        }
        if (executable.Equals("powershell", StringComparison.OrdinalIgnoreCase) || executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            var fileIndex = Array.FindIndex(next, a => a.Equals("-File", StringComparison.OrdinalIgnoreCase) || a.Equals("-f", StringComparison.OrdinalIgnoreCase));
            return fileIndex >= 0 && fileIndex + 1 < next.Length ? "终端 · " + Leaf(next[fileIndex + 1]) : "终端 · PowerShell 命令";
        }
        // Keep only known command verbs and project file names, never arbitrary args.
        var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "build", "run", "test", "watch", "install", "ci", "start", "dev", "serve", "restore", "publish", "check", "clean", "status", "clone", "pull", "push", "fetch", "commit", "checkout", "add", "lint", "format", "exec" };
        var verb = next.FirstOrDefault(a => verbs.Contains(a));
        var project = next.FirstOrDefault(a => new[] { ".csproj", ".sln", ".slnx", ".py", ".ps1", ".bat", ".js", ".ts", ".sh" }.Any(e => a.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
        return "终端 · " + executable + (verb is null ? "" : " " + verb) + (project is null ? "" : " · " + Leaf(project));
    }
}
