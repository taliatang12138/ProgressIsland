using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using ProgressIsland.Core;

namespace ProgressIsland.App;

public sealed record ProbeResult(double? Progress, string Detail, bool Found);

internal static class ProgressProbe
{
    // A third-party accessibility provider may hang forever. It runs ONLY in a disposable process.
    public static int Run(int pid)
    {
        Console.Write(JsonSerializer.Serialize(Read(pid), TaskStore.Json));
        return 0;
    }
    public static int RunWorker()
    {
        // One reusable, lower-priority worker avoids repeatedly launching the WPF runtime.
        // A hung third-party provider is still killed by the parent after the request timeout.
        var thread = new Thread(() =>
        {
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            while (Console.ReadLine() is { } line)
            {
                if (!int.TryParse(line, out var pid)) continue;
                Console.WriteLine(JsonSerializer.Serialize(Read(pid), json));
                Console.Out.Flush();
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start(); thread.Join();
        return 0;
    }
    private static ProbeResult Read(int pid)
    {
        var result = new ProbeResult(null, "未发现可读取的进度条", false);
        try
        {
            var windows = new List<IntPtr>();
            Native.EnumWindows((hwnd, _) =>
            {
                Native.GetWindowThreadProcessId(hwnd, out var owner);
                if (owner == pid && Native.IsWindowVisible(hwnd)) windows.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            var readings = new List<(double value, string name)>();
            foreach (var hwnd in windows.Take(12))
            {
                var root = AutomationElement.FromHandle(hwnd);
                var bars = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar));
                foreach (AutomationElement bar in bars)
                {
                    if (bar.Current.IsOffscreen) continue;
                    if (bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out var pattern))
                    {
                        var range = ((RangeValuePattern)pattern).Current;
                        if (range.Maximum > range.Minimum && double.IsFinite(range.Value))
                            readings.Add((Math.Clamp((range.Value - range.Minimum) / (range.Maximum - range.Minimum) * 100, 0, 100), bar.Current.Name));
                    }
                    else if (bar.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
                    {
                        var observation = new ProgressParser().Parse(((ValuePattern)valuePattern).Current.Value);
                        if (observation?.Percent is { } value) readings.Add((value, bar.Current.Name));
                    }
                    if (readings.Count >= 8) break;
                }
            }
            if (readings.Count == 1)
                result = new(readings[0].value, string.IsNullOrWhiteSpace(readings[0].name) ? "来自软件公开的进度条" : readings[0].name, true);
            else if (readings.Count > 1)
                result = new(null, $"检测到 {readings.Count} 个进度条，无法确定总进度", true);
        }
        catch (Exception ex) { result = new(null, "进度条不可读：" + ex.GetType().Name, false); }
        return result;
    }
}
