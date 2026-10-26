using System.Diagnostics;
using System.Text.Json;
using ProgressIsland.Core;

namespace ProgressIsland.App;

internal sealed class ProgressProbeSession : IDisposable
{
    private Process? worker;
    private bool disposed;
    private readonly SemaphoreSlim serial = new(1);
    public async Task<ProbeResult?> ReadAsync(int pid, CancellationToken ct)
    {
        await serial.WaitAsync(ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (disposed) return null;
            if (worker is null || worker.HasExited)
            {
                Reset();
                var info = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                info.ArgumentList.Add("--probe-worker");
                worker = Process.Start(info)!;
                try { worker.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                _ = worker.StandardError.ReadToEndAsync();
            }
            await worker.StandardInput.WriteLineAsync(pid.ToString().AsMemory(), timeout.Token).ConfigureAwait(false);
            await worker.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            var output = await worker.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (output is null) { Reset(); return new(null, "软件进度查询已断开，稍后重试", false); }
            return JsonSerializer.Deserialize<ProbeResult>(output, TaskStore.Json);
        }
        catch (OperationCanceledException)
        {
            Reset();
            return new(null, "软件未及时响应进度查询", false);
        }
        catch { Reset(); return null; }
        finally { serial.Release(); }
    }
    private void Reset()
    {
        var process = worker; worker = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(); } catch { }
        process.Dispose();
    }
    public void Dispose() { disposed = true; Reset(); }
}
