using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProgressIsland.Core;

/// <summary>Only parses reported counters. CPU, elapsed time and output volume are never progress.</summary>
public sealed partial class ProgressParser
{
    private double? durationSeconds;
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();
    [GeneratedRegex(@"(?<![\d.])(?<n>\d{1,3}(?:\.\d+)?)\s*[%％]")]
    private static partial Regex Percent();
    [GeneratedRegex(@"\b(?:epoch|step|iteration|iter|batch|frame|file|item)s?\s*[:=]?\s*(?<n>\d+)\s*/\s*(?<d>\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Counter();
    [GeneratedRegex(@"^\s*(?<n>\d+)\s*/\s*(?<d>\d+)\s*(?:[\[|]|$)")]
    private static partial Regex BareCounter();
    [GeneratedRegex(@"Duration:\s*(?<h>\d+):(?<m>\d{2}):(?<s>\d{2}(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex Duration();
    [GeneratedRegex(@"\btime=(?<h>\d+):(?<m>\d{2}):(?<s>\d{2}(?:\.\d+)?)")]
    private static partial Regex VideoTime();
    [GeneratedRegex(@"\b(?:accuracy|precision|recall|loss|cpu|gpu|memory|utilization)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Metric();

    public ProgressObservation? Parse(string line)
    {
        line = Ansi().Replace(line, "").Trim();
        if (line.Length == 0) return null;
        if (line.Length > 4000) line = line[..4000];
        if (line.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("progress", out var value) && value.TryGetDouble(out var p) && double.IsFinite(p) && p is >= 0 and <= 100)
                    return new(p, json.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString()! : line);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }
        var duration = Duration().Match(line);
        if (duration.Success) durationSeconds = Time(duration);
        var percents = Percent().Matches(line);
        // More than one percentage is ambiguous (e.g. accuracy and loss). Do not choose one.
        if (percents.Count == 1 && !Metric().IsMatch(line) && Number(percents[0].Groups["n"].Value) is var percent && percent <= 100)
            return new(percent, line);
        var counter = Counter().Match(line);
        if (!counter.Success) counter = BareCounter().Match(line);
        if (counter.Success)
        {
            var n = Number(counter.Groups["n"].Value);
            var d = Number(counter.Groups["d"].Value);
            if (d > 0 && n <= d) return new(n / d * 100, line);
        }
        var time = VideoTime().Match(line);
        if (time.Success && durationSeconds is > 0) return new(Math.Clamp(Time(time) / durationSeconds.Value * 100, 0, 100), line);
        return new(null, line);
    }

    private static double Time(Match m) => Number(m.Groups["h"].Value) * 3600 + Number(m.Groups["m"].Value) * 60 + Number(m.Groups["s"].Value);
    private static double Number(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
