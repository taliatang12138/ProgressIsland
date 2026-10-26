using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ProgressIsland.Core;

namespace ProgressIsland.App;

public sealed record CommandRequest(string Command, string Directory, string Id);
public sealed record CommandState(string Status, double? Progress, string Detail, int? ExitCode = null, DateTimeOffset? ActivityAt = null);

internal static class CommandRunner
{
    // This independent helper owns stdout/stderr, so closing the island never closes a job's pipes.
    public static async Task<int> RunAsync(string requestPath)
    {
        var statePath = requestPath.Replace(".request.json", ".state.json", StringComparison.Ordinal);
        var gate = new object();
        var state = new CommandState(TaskStates.Running, null, "命令运行中；进度未知", ActivityAt: DateTimeOffset.UtcNow);
        var parser = new ProgressParser();
        void Persist()
        {
            File.WriteAllText(statePath + ".tmp", JsonSerializer.Serialize(state, TaskStore.Json));
            File.Move(statePath + ".tmp", statePath, true);
        }
        try
        {
            var request = JsonSerializer.Deserialize<CommandRequest>(File.ReadAllText(requestPath), TaskStore.Json)!;
            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = request.Directory
            };
            var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $global:LASTEXITCODE=0; try { & { " + request.Command + "\n }; if(-not $?) { exit 1 }; exit $global:LASTEXITCODE } catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }";
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            info.Environment["PYTHONUNBUFFERED"] = "1"; info.Environment["PYTHONIOENCODING"] = "utf-8";
            using var process = Process.Start(info)!;
            // Remove the command request once read; only the latest progress state is retained.
            File.Delete(requestPath);
            Persist();
            var lastSave = Stopwatch.StartNew();
            void Accept(string line)
            {
                lock (gate)
                {
                    var observation = parser.Parse(line);
                    if (string.IsNullOrWhiteSpace(line)) return;
                    state = state with { ActivityAt = DateTimeOffset.UtcNow };
                    if (observation is not null) state = state with { Progress = observation.Percent ?? state.Progress, Detail = observation.Detail };
                    if (lastSave.ElapsedMilliseconds < 200) return;
                    Persist(); lastSave.Restart();
                }
            }
            var stdout = ReadAsync(process.StandardOutput, Accept);
            var stderr = ReadAsync(process.StandardError, Accept);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            lock (gate)
            {
                var success = process.ExitCode == 0;
                state = state with { Status = success ? TaskStates.Completed : TaskStates.Failed,
                    Progress = success ? 100 : state.Progress, ExitCode = process.ExitCode,
                    Detail = success ? "命令执行完成 · 退出码 0" : $"命令执行失败 · 退出码 {process.ExitCode} · {state.Detail}" };
                Persist();
            }
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            state = new(TaskStates.Failed, state.Progress, "启动或监控失败：" + ex.Message, -1);
            try { Persist(); } catch { }
            return 1;
        }
    }

    private static async Task ReadAsync(StreamReader reader, Action<string> accept)
    {
        var chars = new char[1024];
        var pending = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(chars).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (chars[i] is '\r' or '\n') { accept(pending.ToString()); pending.Clear(); }
                else if (pending.Length < 4000) pending.Append(chars[i]);
            }
        }
        if (pending.Length > 0) accept(pending.ToString());
    }
}
