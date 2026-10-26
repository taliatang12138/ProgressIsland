using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ProgressIsland.Core;

namespace ProgressIsland.App;

public sealed class TaskRuntime(TaskStore store, Settings settings) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, byte> dismissed = new();
    private readonly ConcurrentDictionary<string, (TimeSpan cpu, DateTimeOffset at)> samples = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> pendingNames = new();
    private readonly Dictionary<string, DateTimeOffset> nextGuiRead = new();
    private readonly ProgressProbeSession probeSession = new();
    private static readonly HashSet<string> WorkerNames = new(StringComparer.OrdinalIgnoreCase)
        { "python", "pythonw", "python3", "MATLAB", "Rscript", "ffmpeg", "7z", "7za", "msbuild", "cmake", "ninja", "blender", "make" };
    private Task? loop;
    private volatile bool interacting;
    public bool IsInteracting { get => interacting; set => interacting = value; }
    public void Start() => loop = Task.Run(ObserveAsync);

    public WorkItem Attach(int pid, bool terminal = false)
    {
        using var process = Process.GetProcessById(pid);
        var ticks = process.StartTime.ToUniversalTime().Ticks;
        var id = $"process-{pid}-{ticks}";
        dismissed.TryRemove(id, out _);
        var existing = store.Find(id);
        if (existing is not null && TaskStates.IsTerminal(existing.Status)) store.Remove(id);
        var item = store.Update(new()
        {
            Id = id, Title = PythonTaskName.IsPython(process.ProcessName) ? "Python · 代码名称未知" : terminal ? "终端 · " + process.ProcessName : string.IsNullOrWhiteSpace(process.MainWindowTitle) ? process.ProcessName : process.MainWindowTitle[..Math.Min(150, process.MainWindowTitle.Length)],
            Source = terminal ? "terminal" : "process", ProcessId = pid, ProcessStartTicks = ticks,
            Detail = "监控进程生命周期；未提供可读取的任务进度", Status = TaskStates.Running
        });
        if (terminal || PythonTaskName.IsPython(process.ProcessName))
        { pendingNames[item.Id] = DateTimeOffset.UtcNow.AddSeconds(30); _ = UpdateProcessNameAsync(item); }
        return item;
    }

    private async Task UpdateProcessNameAsync(WorkItem initial)
    {
        var title = await PythonTaskName.ReadProcessAsync(initial.ProcessId!.Value, initial.ProcessStartTicks, lifetime.Token);
        if (title is not null && !lifetime.IsCancellationRequested && !dismissed.ContainsKey(initial.Id) && store.RenameObserved(initial.Id, initial.Title, title))
        { pendingNames.TryRemove(initial.Id, out _); }
        else if (store.Find(initial.Id)?.Title != initial.Title) pendingNames.TryRemove(initial.Id, out _);
    }

    public void Remove(string id) { dismissed[id] = 0; pendingNames.TryRemove(id, out _); store.Remove(id); }

    private async Task ObserveAsync()
    {
        var round = 0;
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                if (round++ % 3 == 0 && settings.AutoDiscover && !IsInteracting) Discover();
                foreach (var command in store.Snapshot().Where(t => t.Source == "command" && !TaskStates.IsTerminal(t.Status)))
                    ReadCommand(command);
                var active = store.Snapshot().Where(t => t.Source is "process" or "terminal" && !TaskStates.IsTerminal(t.Status)).ToArray();
                foreach (var task in active)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (dismissed.ContainsKey(task.Id)) continue;
                    try
                    {
                        using var process = Process.GetProcessById(task.ProcessId!.Value);
                        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != task.ProcessStartTicks) { Exited(task); continue; }
                        var now = DateTimeOffset.UtcNow;
                        if (pendingNames.TryGetValue(task.Id, out var retryAt) && now >= retryAt)
                        { pendingNames[task.Id] = now.AddSeconds(30); _ = UpdateProcessNameAsync(task); }
                        var total = process.TotalProcessorTime;
                        double? cpu = null;
                        if (samples.TryGetValue(task.Id, out var last))
                            cpu = Math.Clamp((total - last.cpu).TotalMilliseconds / Math.Max(1, (now - last.at).TotalMilliseconds) / Environment.ProcessorCount * 100, 0, 100);
                        samples[task.Id] = (total, now);
                        var update = new TaskUpdate { Id = task.Id, CpuPercent = cpu, MemoryBytes = process.WorkingSet64 };
                        if (settings.ReadProgressBars && !IsInteracting && process.MainWindowHandle != IntPtr.Zero &&
                            (!nextGuiRead.TryGetValue(task.Id, out var next) || now >= next))
                        {
                            var probe = await probeSession.ReadAsync(process.Id, lifetime.Token);
                            nextGuiRead[task.Id] = DateTimeOffset.UtcNow.AddSeconds(probe?.Found == true ? 4 : 8);
                            if (probe is not null)
                                update = update with { Progress = probe.Progress, ClearProgress = probe.Progress is null,
                                    Detail = probe.Progress is >= 100 ? "当前阶段达到 100%；等待工作完成确认" : probe.Detail };
                        }
                        else if (!settings.ReadProgressBars)
                            update = update with { ClearProgress = true, Detail = "软件进度读取已关闭；仅监控进程状态" };
                        if (store.Find(task.Id) is not null && !dismissed.ContainsKey(task.Id)) store.Update(update);
                    }
                    catch (ArgumentException) { Exited(task); }
                    catch (InvalidOperationException) { Exited(task); }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        if (store.Find(task.Id) is not null) store.Update(new() { Id = task.Id, Detail = "进程信息不可读，可能需要与目标软件相同权限", ClearProgress = true });
                    }
                }
                await Task.Delay(2000, lifetime.Token);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(2000, lifetime.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
        }
    }

    private void Discover()
    {
        var parents = Native.ProcessParents();
        var processes = Process.GetProcesses();
        var names = new Dictionary<int, string>();
        foreach (var process in processes) { try { names[process.Id] = process.ProcessName; } catch { } }
        var terminalRoots = TerminalProcessTree.Roots(parents, names, Native.VisibleConsoleShells(parents, names));
        var owned = store.Snapshot().Where(t => t.Source == "command" && !TaskStates.IsTerminal(t.Status) && t.ProcessId is not null)
            .Select(t => t.ProcessId!.Value).ToHashSet();
        bool AlreadyOwned(int pid)
        {
            var candidate = pid;
            for (var i = 0; i < 48 && pid != 0; i++)
            {
                if (owned.Contains(pid)) return true;
                if (pid != candidate && terminalRoots.Contains(pid)) return true;
                if (!parents.TryGetValue(pid, out var parent) || parent == pid) break;
                pid = parent;
            }
            return false;
        }
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if ((!WorkerNames.Contains(process.ProcessName) && !PythonTaskName.IsPython(process.ProcessName) && !terminalRoots.Contains(process.Id)) || settings.IgnoredProcesses.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) continue;
                    if (AlreadyOwned(process.Id)) continue;
                    var key = $"process-{process.Id}-{process.StartTime.ToUniversalTime().Ticks}";
                    if (!dismissed.ContainsKey(key) && (store.Find(key) is null || store.Find(key)?.Status == TaskStates.Interrupted)) Attach(process.Id, terminalRoots.Contains(process.Id));
                }
                catch { }
            }
        }
    }

    private void Exited(WorkItem task)
    {
        if (store.Find(task.Id) is not null && !dismissed.ContainsKey(task.Id))
            store.Update(new() { Id = task.Id, Status = TaskStates.Exited, ClearProgress = true, Detail = task.Source == "terminal" ? "终端命令已结束；未取得退出码，结果未验证" : "程序已退出；内部任务是否成功尚未验证" });
        samples.TryRemove(task.Id, out _);
        pendingNames.TryRemove(task.Id, out _);
        nextGuiRead.Remove(task.Id);
    }

    public WorkItem WatchLog(string path, string? title)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("日志文件不存在", path);
        var item = store.Update(new() { Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(path) : title,
            Source = "log", LogPath = path, Detail = "跟随日志；完成状态需由脚本或你确认" });
        _ = Task.Run(() => TailAsync(item));
        return item;
    }

    private async Task TailAsync(WorkItem item)
    {
        var parser = new ProgressParser();
        long position = 0;
        // Incremental decoding handles UTF-8 and a multibyte character split across file writes.
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        var pending = new StringBuilder();
        try
        {
            while (!lifetime.IsCancellationRequested && store.Find(item.Id) is { } current && !TaskStates.IsTerminal(current.Status))
            {
                using (var stream = new FileStream(item.LogPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < position) { position = 0; pending.Clear(); decoder.Reset(); }
                    if (position == 0 && stream.Length > 128 * 1024) position = stream.Length - 128 * 1024;
                    stream.Position = position;
                    var bytes = new byte[8192];
                    var chars = new char[8192];
                    int count, budget = 0;
                    while (budget < 128 * 1024 && (count = await stream.ReadAsync(bytes, lifetime.Token)) > 0)
                    {
                        budget += count;
                        var decoded = decoder.GetChars(bytes, 0, count, chars, 0);
                        for (var i = 0; i < decoded; i++)
                        {
                            if (chars[i] is '\r' or '\n')
                            {
                                ApplyLine(item.Id, parser, pending.ToString());
                                pending.Clear();
                            }
                            else if (pending.Length < 4000) pending.Append(chars[i]);
                        }
                    }
                    position = stream.Position;
                    if (budget > 0) store.Update(new() { Id = item.Id, ActivityAt = DateTimeOffset.UtcNow });
                }
                await Task.Delay(600, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (store.Find(item.Id) is { } active && !TaskStates.IsTerminal(active.Status))
                store.Update(new() { Id = item.Id, Status = TaskStates.Interrupted, Detail = "日志监控中断：" + ex.Message });
        }
    }

    public WorkItem RunCommand(string command, string? title, string? directory)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("请输入命令");
        var workDirectory = string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : Path.GetFullPath(directory);
        if (!Directory.Exists(workDirectory)) throw new DirectoryNotFoundException("工作目录不存在");
        var id = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Settings.DataDirectory, "commands");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, id + ".request.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new CommandRequest(command, workDirectory, id), TaskStore.Json));
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--runner"); info.ArgumentList.Add(path);
        using var helper = Process.Start(info) ?? throw new InvalidOperationException("无法启动命令监控");
        return store.Update(new() { Id = id, Title = string.IsNullOrWhiteSpace(title) ? command[..Math.Min(120, command.Length)] : title,
            Source = "command", ProcessId = helper.Id, ProcessStartTicks = helper.StartTime.ToUniversalTime().Ticks,
            Detail = "已启动；等待标准输出中的进度" });
    }

    private void ReadCommand(WorkItem item)
    {
        try
        {
            var path = Path.Combine(Settings.DataDirectory, "commands", item.Id + ".state.json");
            if (File.Exists(path))
            {
                var state = System.Text.Json.JsonSerializer.Deserialize<CommandState>(File.ReadAllText(path), TaskStore.Json);
                if (state is not null)
                    store.Update(new() { Id = item.Id, Status = state.Status, Progress = state.Progress, Detail = state.Detail, ExitCode = state.ExitCode,
                        ActivityAt = state.ActivityAt ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path)) });
            }
            if (store.Find(item.Id) is { } current && !TaskStates.IsTerminal(current.Status))
            {
                using var process = Process.GetProcessById(item.ProcessId!.Value);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != item.ProcessStartTicks)
                    store.Update(new() { Id = item.Id, Status = TaskStates.Interrupted, Detail = "命令监控已停止，缺少最终退出结果" });
            }
        }
        catch (ArgumentException) { MarkCommandInterrupted(item); }
        catch (InvalidOperationException) { MarkCommandInterrupted(item); }
        catch (IOException) { /* Atomic replacement can briefly race a reader; retry next tick. */ }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void MarkCommandInterrupted(WorkItem item)
    {
        if (store.Find(item.Id) is { } current && !TaskStates.IsTerminal(current.Status))
            store.Update(new() { Id = item.Id, Status = TaskStates.Interrupted, Detail = "命令监控中断；未收到退出结果" });
    }

    private void ApplyLine(string id, ProgressParser parser, string line)
    {
        if (store.Find(id) is not { } current || TaskStates.IsTerminal(current.Status)) return;
        var observation = parser.Parse(line);
        if (observation is not null) store.Update(new() { Id = id, Progress = observation.Percent, Detail = observation.Detail });
    }

    public void Dispose() { lifetime.Cancel(); probeSession.Dispose(); lifetime.Dispose(); }
}
