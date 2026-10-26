using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using ProgressIsland.Core;
using ProgressIsland.App;

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); passed++; Console.WriteLine("PASS " + message); }
void Invalid(Action action, string message) { try { action(); } catch (ArgumentException) { Check(true, message); return; } throw new Exception("FAIL: " + message); }
var parser = new ProgressParser();
Check(parser.Parse("processing 42%")?.Percent == 42, "reported percentage");
Check(parser.Parse("\x1b[32m 37%|████ 37/100 [00:01]\x1b[0m")?.Percent == 37, "ANSI and tqdm carriage-return record");
Check(parser.Parse("Epoch 3/10")?.Percent == 30, "epoch counter");
Check(parser.Parse("step 100/100")?.Percent == 100, "last step");
Check(parser.Parse("step 10/0")?.Percent is null, "zero denominator is unknown");
Check(parser.Parse("CPU 93%")?.Percent is null, "CPU is never completion progress");
Check(parser.Parse("accuracy 93%")?.Percent is null, "accuracy is never completion progress");
Check(parser.Parse("5% 60%")?.Percent is null, "ambiguous percentages are unknown");
Check(parser.Parse("file at C:/data/20/100/raw")?.Percent is null, "file paths are not counters");
Check(parser.Parse("loss=0.2")?.Percent is null, "ordinary output is not progress");
Check(parser.Parse("{\"progress\":12.5,\"message\":\"hello\"}") is { Percent: 12.5, Detail: "hello" }, "explicit JSON progress");
Check(parser.Parse("{\"progress\":\"oops\"}")?.Percent is null, "malformed JSON progress does not crash parser");
parser.Parse("Duration: 00:02:00.00");
Check(parser.Parse("frame=50 fps=30 time=00:01:00.00")?.Percent == 50, "FFmpeg duration and time");
Check(new ProgressParser().Parse("time=00:01:00.00")?.Percent is null, "FFmpeg needs known duration");

var store = new TaskStore();
int completed = 0; store.Terminal += _ => completed++;
var task = store.Update(new() { Title = "test", Progress = 100 });
Check(task.Status == TaskStates.Running, "100% stage never implies task completion");
task = store.Update(new() { Id = task.Id, Status = TaskStates.Completed });
store.Update(new() { Id = task.Id, Progress = 30, Status = TaskStates.Running });
Check(store.Find(task.Id)?.Status == TaskStates.Completed && completed == 1, "late events cannot revive completed task or notify twice");
Invalid(() => store.Update(new() { Title = "bad", Progress = -1 }), "negative progress rejected");
Invalid(() => store.Update(new() { Title = "bad", Progress = double.NaN }), "NaN progress rejected");
Invalid(() => store.Update(new() { Title = "bad", Status = "surprise" }), "unknown state rejected");
Invalid(() => store.Update(new() { Id = "" }), "empty task id rejected");
var desktop = new DockArea(0, 0, 1920, 1040);
Check(Docking.Place(desktop, DockSide.Top, 0.5, false) is { Width: 360, Height: 70, Y: 0 }, "top edge uses horizontal strip");
Check(Docking.Place(desktop, DockSide.Left, 0.5, false) is { Width: 72, Height: 214, X: 0 }, "left edge uses genuine vertical layout");
Check(Docking.Place(desktop, DockSide.Right, 0.5, false) is { X: 1848, Width: 72, Height: 214 }, "right rail touches screen edge");
Check(Docking.Place(desktop, DockSide.Bottom, 0.5, false) is { Y: 970, Width: 360 }, "bottom rail touches work-area bottom");
foreach (var side in Enum.GetValues<DockSide>())
{
    var closed = Docking.Place(desktop, side, 0.5, false);
    var open = Docking.Place(desktop, side, 0.5, true, 594);
    Check(side switch
    {
        DockSide.Top => open.Y == closed.Y,
        DockSide.Bottom => open.Y + open.Height == closed.Y + closed.Height,
        DockSide.Left => open.X == closed.X,
        _ => open.X + open.Width == closed.X + closed.Width
    }, side + " hover expands inward from the same edge");
    foreach (var position in new[] { 0d, 0.5, 1d })
    {
        var bounds = Docking.Place(new DockArea(-1920, -100, 1920, 1080), side, position, true);
        Check(bounds.X >= -1920 && bounds.Y >= -100 && bounds.X + bounds.Width <= 0 && bounds.Y + bounds.Height <= 980,
            $"{side} anchor {position} remains visible on a negative-origin monitor");
    }
}
Check(Docking.NearestEdge(desktop, 2, 400) == DockSide.Left && Docking.NearestEdge(desktop, 1900, 400) == DockSide.Right, "drops at side edges choose vertical orientation");
Check(Docking.NearestEdge(desktop, 900, 2) == DockSide.Top && Docking.NearestEdge(desktop, 900, 1038) == DockSide.Bottom, "drops at top and bottom choose horizontal orientation");
Check(Docking.AnchorAt(desktop, DockSide.Left, 0, 260) == 0.25, "vertical dock preserves along-edge drop position");
Check(Docking.Place(new DockArea(0, 0, 320, 240), DockSide.Right, 1, true).Width == 320, "small work area clamps expanded panel");
var wideStrip = Docking.Place(desktop, DockSide.Top, 0.7, false, compactTasks: 8);
Check(!Docking.Contains(Docking.Place(desktop, DockSide.Top, 0.7, true), wideStrip.X + 8, wideStrip.Y + 35),
    "regression fixture reproduces a long compact strip extending beyond the centered expanded panel");
foreach (var area in new[] { desktop, new DockArea(-1920, -100, 1920, 1080), new DockArea(0, 0, 320, 240) })
foreach (var side in Enum.GetValues<DockSide>())
{
    var safe = true;
    foreach (var position in new[] { 0d, 0.5, 1d })
    foreach (var taskCount in new[] { 1, 30 })
    foreach (var fraction in new[] { 0.02, 0.98 })
    {
        var compact = Docking.Place(area, side, position, false, compactTasks: taskCount);
        var panel = Docking.Place(area, side, position, true);
        var x = compact.X + compact.Width * (Docking.IsVertical(side) ? 0.5 : fraction);
        var y = compact.Y + compact.Height * (Docking.IsVertical(side) ? fraction : 0.5);
        var shifted = Docking.KeepPointerInside(area, side, panel, x, y);
        safe &= Docking.Contains(shifted, x, y) && shifted.X >= area.X && shifted.Y >= area.Y
            && shifted.X + shifted.Width <= area.Right && shifted.Y + shifted.Height <= area.Bottom
            && (Docking.IsVertical(side) ? shifted.X == panel.X : shifted.Y == panel.Y);
    }
    Check(safe, $"{side} hover stays inside the expanded panel at both strip ends and corner anchors on {area.Width}x{area.Height}");
}
var card = new TaskCard(new WorkItem { Id = "responsive-card", Title = "test", Progress = 20 });
var changed = new List<string?>(); card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
card.Refresh(card.Item); changed.Clear();
card.Refresh(card.Item with { CpuPercent = 14 });
Check(changed.SequenceEqual(new[] { "Metadata" }), "resource refresh does not invalidate progress, brushes or animation");
changed.Clear(); card.Refresh(card.Item with { UpdatedAt = DateTimeOffset.UtcNow });
Check(changed.Count == 0, "timestamp-only update does not rebuild task visuals");
var unknown = store.Update(new() { Title = "unknown", Progress = 40 });
unknown = store.Update(new() { Id = unknown.Id, ClearProgress = true });
Check(unknown.Progress is null, "lost observations clear stale progress");
var gui = store.Update(new() { Title = "gui", Source = "process", Progress = 42 });
gui = store.Update(new() { Id = gui.Id, Status = TaskStates.Exited, ClearProgress = true });
Check(gui.Status == TaskStates.Exited && gui.Progress is null, "GUI process exit is unverified, never success");
Parallel.For(0, 100, i => store.Update(new() { Id = "thread-" + i, Title = "parallel" }));
Check(store.Snapshot().Count == 103, "concurrent producers preserve independent tasks");

var root = Path.Combine(Path.GetTempPath(), "ProgressIsland-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Check(!new Settings().StartWithWindows, "fresh settings do not enable Windows startup without user choice");
var startupTestKey = @"Software\ProgressIsland\Tests\Startup-" + Guid.NewGuid().ToString("N");
var startupRegistration = new StartupRegistration(startupTestKey);
var startupExecutable = Path.Combine(root, "任务岛 测试.exe");
var movedStartupExecutable = Path.Combine(root, "任务岛 新版本.exe");
File.WriteAllBytes(startupExecutable, []); File.WriteAllBytes(movedStartupExecutable, []);
try
{
    Check(!startupRegistration.IsRegistered, "missing user startup registration reads as disabled");
    Check(StartupRegistration.BuildCommand(startupExecutable) == $"\"{startupExecutable}\" --startup", "startup command quotes paths containing spaces and Unicode and uses silent startup mode");
    Invalid(() => StartupRegistration.BuildCommand("relative.exe"), "relative startup executable is rejected");
    Invalid(() => StartupRegistration.BuildCommand(Path.Combine(root, "startup.ps1")), "startup registration requires an EXE rather than a shell script");
    startupRegistration.SetEnabled(true, startupExecutable);
    Check(startupRegistration.IsRegistered && startupRegistration.ReadCommand() == StartupRegistration.BuildCommand(startupExecutable), "startup enable writes the expected current-user command in isolated test key");
    startupRegistration.SetEnabled(true, startupExecutable);
    using (var startupKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startupTestKey, writable: true)!)
    {
        Check(startupKey.GetValueKind(StartupRegistration.ValueName) == Microsoft.Win32.RegistryValueKind.String && startupKey.GetValueNames().Length == 1,
            "repeated startup enable is idempotent and stores a literal string command");
        startupKey.SetValue("AnotherProgram", "preserve me");
    }
    startupRegistration.SetEnabled(true, movedStartupExecutable);
    Check(startupRegistration.ReadCommand() == StartupRegistration.BuildCommand(movedStartupExecutable), "opening a new executable location updates the existing startup command");
    startupRegistration.SetEnabled(false);
    Check(!startupRegistration.IsRegistered, "startup disable removes its own registration");
    using (var startupKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startupTestKey)!)
        Check(startupKey.GetValue("AnotherProgram") as string == "preserve me", "disabling startup preserves unrelated applications");
    startupRegistration.SetEnabled(false);
    Check(!startupRegistration.IsRegistered, "disabling an already disabled startup registration is safe");
    var missingStartupRejected = false;
    try { startupRegistration.SetEnabled(true, Path.Combine(root, "missing.exe")); }
    catch (FileNotFoundException) { missingStartupRejected = true; }
    Check(missingStartupRejected && !startupRegistration.IsRegistered, "missing executable cannot create a broken startup entry");
}
finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(startupTestKey, throwOnMissingSubKey: false); }
var file = Path.Combine(root, "history.json");
var disk = new TaskStore(file);
disk.Update(new() { Title = "external", Source = "api" });
disk.Update(new() { Title = "command", Source = "command" });
disk.Save();
var restored = new TaskStore(file).Snapshot();
Check(restored.Any(t => t.Source == "api" && t.Status == TaskStates.Interrupted), "stale external task becomes interrupted after restart");
Check(restored.Any(t => t.Source == "command" && t.Status == TaskStates.Running), "independent command helper can reconnect after restart");
File.WriteAllText(file, "not json");
Check(new TaskStore(file).PersistenceError is not null, "corrupt history reports an error instead of crashing");

var groupingNow = DateTimeOffset.UtcNow;
var dormant = new WorkItem { Id = "dormant", Title = "old session", Source = "codex", Status = TaskStates.Running,
    StartedAt = groupingNow.AddDays(-42), LastActivityAt = groupingNow.AddDays(-28), UpdatedAt = groupingNow };
Check(TaskGrouping.Classify(dormant, groupingNow) == TaskGroup.Idle, "stale native in-progress session is idle even when recently polled");
Check(TaskGrouping.Classify(dormant with { LastActivityAt = groupingNow.AddMinutes(-2) }, groupingNow) == TaskGroup.Running, "long-running turn with recent output stays in running group");
Check(TaskGrouping.Classify(dormant with { LastActivityAt = groupingNow.AddDays(-7) }, groupingNow) == TaskGroup.Idle, "one-week idle threshold boundary is inclusive");
Check(TaskGrouping.Classify(dormant with { LastActivityAt = groupingNow.AddDays(-6).AddHours(-23) }, groupingNow) == TaskGroup.Running, "a task remains current until a full week without activity");
Check(new Settings().IdleAfterMinutes == 10080, "fresh settings default to one week rather than ten minutes");
Check(TaskGrouping.DurationLabel(10080) == "1 周" && TaskGrouping.DurationLabel(180) == "3 小时" && TaskGrouping.DurationLabel(30) == "30 分钟", "inline idle selector labels preserve minute, hour and weekly precision");
var previousDataRoot = Environment.GetEnvironmentVariable("PROGRESS_ISLAND_DATA");
try
{
    Environment.SetEnvironmentVariable("PROGRESS_ISLAND_DATA", Path.Combine(root, "idle-settings"));
    new Settings { IdleAfterMinutes = 180, StartWithWindows = true }.Save();
    Check(Settings.Load().IdleAfterMinutes == 180, "custom hourly idle threshold persists and loads without rounding to whole days");
    Check(Settings.Load().StartWithWindows, "Windows startup preference persists alongside existing settings");
}
finally { Environment.SetEnvironmentVariable("PROGRESS_ISLAND_DATA", previousDataRoot); }
Check(TaskGrouping.Classify(dormant with { LastActivityAt = groupingNow.AddMinutes(-20) }, groupingNow, 30) == TaskGroup.Running, "configurable idle threshold permits silent long computations");
Check(TaskGrouping.Classify(dormant with { Status = TaskStates.Completed }, groupingNow) == TaskGroup.Ended && TaskGrouping.Classify(dormant with { Status = TaskStates.Failed }, groupingNow) == TaskGroup.Ended, "old terminal results remain ended rather than being relabeled idle");
Check(TaskGrouping.Classify(dormant with { LastActivityAt = groupingNow, Status = TaskStates.Paused }, groupingNow) == TaskGroup.Running && TaskGrouping.Classify(dormant with { LastActivityAt = groupingNow, Status = TaskStates.Waiting }, groupingNow) == TaskGroup.Running, "recently paused or waiting tasks remain current until inactivity threshold");
Check(TaskGrouping.Classify(dormant with { LastActivityAt = null }, groupingNow) == TaskGroup.Idle, "legacy records use original start time rather than refresh time");
var idleCard = new TaskCard(dormant);
Check(idleCard.IsIdle && !idleCard.Indeterminate && idleCard.PercentLabel == "闲置" && idleCard.Item.Status == TaskStates.Running, "idle presentation stops busy animation without forging completion");
idleCard.Refresh(dormant with { LastActivityAt = groupingNow });
Check(!idleCard.IsIdle && idleCard.Indeterminate && idleCard.PercentLabel == "运行中", "fresh activity restores card state and busy indicator");
var activityStore = new TaskStore();
var processActivity = activityStore.Update(new() { Title = "quiet process", Source = "process", Detail = "same status", ActivityAt = groupingNow.AddDays(-8) });
activityStore.Update(new() { Id = processActivity.Id, CpuPercent = 0, MemoryBytes = 456 });
Check(TaskGrouping.Classify(activityStore.Find(processActivity.Id)!, groupingNow) == TaskGroup.Idle, "routine process liveness and memory sampling cannot keep idle task active");
activityStore.Update(new() { Id = processActivity.Id, CpuPercent = 0.5 });
Check(TaskGrouping.Classify(activityStore.Find(processActivity.Id)!, groupingNow) == TaskGroup.Running, "observed CPU activity updates activity time without inventing percentage");
var quietCommand = activityStore.Update(new() { Title = "quiet command", Source = "command", ActivityAt = groupingNow.AddDays(-8) });
activityStore.Update(new() { Id = quietCommand.Id, Detail = quietCommand.Detail, Status = quietCommand.Status, ActivityAt = quietCommand.LastActivityAt });
Check(TaskGrouping.Classify(activityStore.Find(quietCommand.Id)!, groupingNow) == TaskGroup.Idle, "re-reading unchanged command state does not count as work activity");
var apiActivity = activityStore.Update(new() { Title = "api heartbeat", ActivityAt = groupingNow.AddDays(-8) });
activityStore.Update(new() { Id = apiActivity.Id });
Check(TaskGrouping.Classify(activityStore.Find(apiActivity.Id)!, groupingNow) == TaskGroup.Running, "explicit script heartbeat resumes a quiet API task");

var codexStore = new TaskStore(Path.Combine(root, "codex-tasks.json"));
var sessionId = "11111111-1111-4111-8111-111111111111";
var otherId = "22222222-2222-4222-8222-222222222222";
var observation = new CodexObservation(sessionId, "turn1", "Codex · 数据分析", TaskStates.Running, "运行中", DateTimeOffset.UtcNow.AddMinutes(-2), null, false, 100);
int codexEnded = 0; codexStore.Terminal += _ => codexEnded++;
var session = codexStore.ObserveCodex(observation);
Check(session.Source == "codex" && session.Progress is null && !session.IsUnread, "Codex running uses native turn state without invented percentage");
observation = observation with { Status = TaskStates.Completed, FinishedAt = DateTimeOffset.UtcNow, NativeUnread = true };
session = codexStore.ObserveCodex(observation, true);
Check(TaskStates.IsTerminal(session.Status) && session.Progress is null && session.IsUnread && codexEnded == 1, "unviewed Codex completion is terminal and unread, never fake 100%");
var stableVersion = codexStore.Version;
codexStore.ObserveCodex(observation, true);
Check(codexEnded == 1 && codexStore.Version == stableVersion, "unchanged Codex polling neither repeats notifications nor invalidates UI");
codexStore.MarkRead(session.Id, true);
Check(!codexStore.ObserveCodex(observation).IsUnread, "explicit read survives a stale native unread snapshot");
codexStore.ObserveCodex(observation with { NativeUnread = false });
codexStore.MarkRead(session.Id, false);
Check(codexStore.ObserveCodex(observation with { NativeUnread = false }).IsUnread, "manual unread survives repeated native read snapshots");
codexStore.Save();
var restoredCodex = new TaskStore(Path.Combine(root, "codex-tasks.json"));
Check(restoredCodex.Find(session.Id) is { IsUnread: true, ReadOverride: "unread" }, "manual unread persists across application restart");
Check(!codexStore.ObserveCodex(observation with { NativeUnread = false, NativeActivity = 200 }).IsUnread, "genuine native session activity clears manual unread");
codexStore.ObserveCodex(observation with { NativeUnread = true, NativeActivity = 200 });
codexStore.MarkRead(session.Id, false);
Check(!codexStore.ObserveCodex(observation with { NativeUnread = false, NativeActivity = 200 }).IsUnread, "native unread-to-read transition clears island dot");
observation = observation with { TurnId = "turn2", Status = TaskStates.Running, StartedAt = DateTimeOffset.UtcNow, FinishedAt = null, NativeUnread = false, NativeActivity = 200 };
session = codexStore.ObserveCodex(observation);
Check(session.Status == TaskStates.Running && session.FinishedAt is null && !session.IsUnread && codexStore.Snapshot().Count == 1, "new Codex turn resumes same session without duplicate card or stale unread");
codexStore.MarkRead(session.Id, true);
session = codexStore.ObserveCodex(observation with { Status = TaskStates.Failed, FinishedAt = DateTimeOffset.UtcNow, NativeUnread = true }, true);
Check(session.Status == TaskStates.Failed && session.IsUnread, "viewing a running turn does not consume its later unviewed result");
session = codexStore.ObserveCodex(observation with { TurnId = "turn3", Status = TaskStates.Interrupted, FinishedAt = DateTimeOffset.UtcNow, NativeUnread = true }, true);
Check(session.IsUnread && TaskStates.IsTerminal(session.Status) && session.Progress is null && codexEnded == 3, "later interrupted turn belongs to ended group and resets prior read override");
var fallback = codexStore.ObserveCodex(observation with { SessionId = otherId, NativeUnread = null, Status = TaskStates.Completed, FinishedAt = DateTimeOffset.UtcNow });
Check(fallback.IsUnread, "missing native read metadata conservatively marks new finished session unread");
codexStore.MarkRead(fallback.Id, false);
Check(!codexStore.ObserveCodex(observation with { SessionId = otherId, NativeUnread = null, NativeActivity = 300, Status = TaskStates.Completed, FinishedAt = fallback.FinishedAt }).IsUnread, "activity clears unread even when native unread list is unavailable");
var codexCard = new TaskCard(session);
Check(codexCard.IsUnread && codexCard.OpenLabel == "打开会话" && codexCard.PercentLabel == "已结束", "Codex card exposes unread dot and conversation action");
var dormantObservation = observation with { SessionId = "55555555-5555-4555-8555-555555555555", LastActivityAt = groupingNow.AddDays(-8), StartedAt = groupingNow.AddDays(-9) };
var dormantSession = codexStore.ObserveCodex(dormantObservation);
codexStore.MarkRead(dormantSession.Id, false);
dormantSession = codexStore.ObserveCodex(dormantObservation with { NativeActivity = 900 });
Check(TaskGrouping.Classify(dormantSession, groupingNow) == TaskGroup.Idle && dormantSession.LastActivityAt == dormantObservation.LastActivityAt, "viewing or marking a stale conversation does not fabricate work activity");
dormantSession = codexStore.ObserveCodex(dormantObservation with { LastActivityAt = groupingNow });
Check(TaskGrouping.Classify(dormantSession, groupingNow) == TaskGroup.Running, "new native work timestamp resumes same idle conversation");
Invalid(() => codexStore.ObserveCodex(observation with { SessionId = "bad-id" }), "invalid Codex deep-link identity rejected");

var currentWork = dormant with { Id = "current", LastActivityAt = groupingNow, IsUnread = false };
var pendingResult = dormant with { Id = "pending", Status = TaskStates.Completed, IsUnread = true, FinishedAt = groupingNow.AddDays(-30) };
var acknowledged = pendingResult with { Id = "acknowledged", IsUnread = false };
var attention = TaskGrouping.Attention([dormant, pendingResult, acknowledged, currentWork], groupingNow);
Check(attention.Select(t => t.Id).SequenceEqual(["current", "pending"]), "default island includes every current task and unconfirmed result, excluding idle and confirmed history");
Check(TaskGrouping.Attention(Enumerable.Range(0, 80).Select(i => pendingResult with { Id = "pending-" + i }), groupingNow).Count == 80, "unconfirmed results are not hidden by the former fifty-item list cap");
Check(TaskGrouping.Attention([pendingResult with { Status = TaskStates.Failed }, pendingResult with { Id = "interrupted", Status = TaskStates.Interrupted }], groupingNow).Count == 2, "failed and interrupted unconfirmed results remain visible too");
var cardForConfirmation = new TaskCard(pendingResult);
Check(cardForConfirmation.NeedsConfirmation && cardForConfirmation.CompactLabel.Contains("待确认"), "compact finished task has a distinct confirmation action");
Check(Docking.Place(desktop, DockSide.Top, 0.5, false, compactTasks: 4).Width > Docking.Place(desktop, DockSide.Top, 0.5, false, compactTasks: 1).Width, "horizontal island allocates a slot for each visible task");
Check(Docking.Place(desktop, DockSide.Left, 0.5, false, compactTasks: 4).Height > Docking.Place(desktop, DockSide.Left, 0.5, false, compactTasks: 1).Height, "vertical island allocates a slot for each visible task");
Check(Docking.Place(desktop, DockSide.Right, 0.5, false, compactTasks: 80).Height == desktop.Height && Docking.Place(desktop, DockSide.Top, 0.5, false, compactTasks: 80).Width == desktop.Width, "large task collections stay inside work area and can scroll");

Check(PythonTaskName.FromCommandLine("\"C:\\Program Files\\Python\\python.exe\" -u \"C:\\工作\\训练 模型.py\" --api-key very-secret") == "Python · 训练 模型.py", "Python label uses Unicode script filename without exposing arguments");
Check(PythonTaskName.FromCommandLine("pythonw.exe -W ignore -X utf8 C:\\code\\batch.py") == "Python · batch.py", "Python interpreter options do not become code names");
Check(PythonTaskName.FromCommandLine("python.exe -m package.worker --token very-secret") == "Python · package.worker", "Python module execution uses module name without argument values");
Check(PythonTaskName.FromCommandLine("python.exe -c \"print('private-token')\"") == "Python · 内联代码", "Python inline source code is not copied into titles");
Check(PythonTaskName.FromCommandLine("python.exe") == "Python · 交互会话" && PythonTaskName.FromCommandLine("python.exe -") == "Python · 标准输入", "Python interactive and stdin execution have explicit descriptions");
Check(PythonTaskName.FromCommandLine("node.exe C:\\tools\\npm-cli.js run build --token secret") == "终端 · npm run build", "npm terminal job uses the package command name");
Check(PythonTaskName.FromCommandLine("dotnet.exe build \"C:\\work space\\Example.csproj\" --api-key secret") == "终端 · dotnet build · Example.csproj", "dotnet terminal job uses build verb and project filename");
Check(PythonTaskName.FromCommandLine("pwsh.exe -NoProfile -File \"C:\\work space\\job.ps1\" -Secret value") == "终端 · job.ps1", "terminal PowerShell script label excludes parameters");
Check(PythonTaskName.FromCommandLine("node.exe -e \"console.log('private')\"") == "终端 · Node 内联代码", "Node inline code is not displayed as a task name");
var treeNames = new Dictionary<int, string> { [1] = "WindowsTerminal", [2] = "pwsh", [3] = "node", [4] = "cmd", [5] = "node", [6] = "git", [7] = "powershell", [8] = "ChatGPT", [9] = "codex", [10] = "pwsh", [11] = "dotnet", [12] = "Code", [13] = "node", [14] = "pwsh", [15] = "cargo" };
var treeParents = new Dictionary<int, int> { [1] = 0, [2] = 1, [3] = 2, [4] = 3, [5] = 4, [6] = 2, [7] = 1, [8] = 0, [9] = 8, [10] = 9, [11] = 10, [12] = 0, [13] = 12, [14] = 13, [15] = 14 };
Check(TerminalProcessTree.Roots(treeParents, treeNames).SetEquals([3, 6, 15]), "terminal jobs merge child process trees, include VS Code terminals, and exclude idle shells and agent tools");
Check(TerminalProcessTree.Roots(new Dictionary<int, int> { [101] = 100 }, new Dictionary<int, string> { [100] = "powershell", [101] = "git" }, new HashSet<int> { 100 }).SetEquals([101]), "classic visible console shell identifies its external command");
var terminalStore = new TaskStore();
var terminalTask = terminalStore.Update(new() { Title = "终端 · git fetch", Source = "terminal" });
terminalTask = terminalStore.Update(new() { Id = terminalTask.Id, Status = TaskStates.Exited, ClearProgress = true });
Check(terminalTask.IsUnread && terminalTask.Progress is null && new TaskCard(terminalTask).Metadata.Contains("终端命令"), "external terminal exit remains unverified and awaits confirmation");
var renameTask = terminalStore.Update(new() { Title = "Python · 代码名称未知", Source = "terminal" });
terminalStore.Update(new() { Id = renameTask.Id, Title = "My chosen title" });
Check(!terminalStore.RenameObserved(renameTask.Id, renameTask.Title, "Python · worker.py") && terminalStore.Find(renameTask.Id)?.Title == "My chosen title", "asynchronous code naming preserves a manually chosen task title");
terminalStore.Remove(renameTask.Id);
Check(!terminalStore.RenameObserved(renameTask.Id, "My chosen title", "Python · worker.py") && terminalStore.Find(renameTask.Id) is null, "late metadata lookup cannot resurrect a removed task");

var nativeJson = JsonSerializer.Serialize(new Dictionary<string, object>
{
    ["electron-thread-read-state-v1"] = new { unreadByIdentity = new Dictionary<string, object> { ["identity"] = new Dictionary<string, object> { ["local:host"] = new[] { sessionId }, ["remote:server"] = new[] { otherId } } } },
    ["electron-persisted-atom-state"] = new Dictionary<string, object> { ["thread-user-activity-times-v1"] = new Dictionary<string, object> { [JsonSerializer.Serialize(new[] { "local", sessionId })] = 12345L, ["invalid-key"] = "bad", ["[1,2]"] = false } }
});
var readState = CodexReadState.Parse(nativeJson);
Check(readState.Unread?.SetEquals([sessionId]) == true && readState.Activity.GetValueOrDefault(sessionId) == 12345L, "native read-state parsing isolates local host and actual session activity");
Check(CodexReadState.Parse("{\"electron-thread-read-state-v1\":{\"unreadByIdentity\":{\"one\":{},\"two\":{}}}}").Unread is null, "multiple native identities are not silently blended");
Check(CodexReadState.Parse("{\"electron-thread-read-state-v1\":[],\"electron-persisted-atom-state\":false}").Unread is null && CodexReadState.Parse("[]").Activity.Count == 0, "unsupported native read-state shapes degrade safely");

var actualCodexRoot = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
if (Directory.Exists(actualCodexRoot) && Directory.EnumerateFiles(actualCodexRoot, "thread_history_*.sqlite").Any())
{
    var elapsedRead = Stopwatch.StartNew();
    var actualSessions = new CodexSessionReader(actualCodexRoot).Read();
    Check(actualSessions.Count > 0 && actualSessions.All(s => Guid.TryParse(s.SessionId, out _) && s.LastActivityAt is not null), "real local Codex SQLite WAL metadata opens read-only through Windows runtime");
    Console.WriteLine($"Codex metadata: {actualSessions.Count} sessions; read {elapsedRead.Elapsed.TotalMilliseconds:0.0}ms; no conversation bodies read");
}

if (args.Length > 1 && File.Exists(args[1]))
{
    var fixtureRoot = Path.Combine(root, "codex-fixture");
    Directory.CreateDirectory(fixtureRoot);
    var fixturePython = new ProcessStartInfo(args[1]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    fixturePython.ArgumentList.Add("-c");
    fixturePython.ArgumentList.Add("""
import sqlite3,sys,json,pathlib
p=pathlib.Path(sys.argv[1]); a=sys.argv[2]; b=sys.argv[3]
old=sqlite3.connect(p/'state_2.sqlite'); old.close()
s=sqlite3.connect(p/'state_10.sqlite')
s.execute('CREATE TABLE threads(id TEXT,title TEXT,name TEXT,archived INT,thread_source TEXT,updated_at_ms INT)')
s.executemany('INSERT INTO threads VALUES(?,?,?,?,?,?)',[(a,'old title',None,0,'user',30),(b,'finished',None,0,'user',20),('33333333-3333-4333-8333-333333333333','archived',None,1,'user',50),('44444444-4444-4444-8444-444444444444','subagent',None,0,'subagent',50)])
s.commit(); s.close()
h=sqlite3.connect(p/'thread_history_10.sqlite')
h.execute('CREATE TABLE thread_turns(thread_id TEXT,turn_id TEXT,status TEXT,started_at INT,completed_at INT,rollout_ordinal INT)')
h.executemany('INSERT INTO thread_turns VALUES(?,?,?,?,?,?)',[(a,'old-turn','completed',1700000000,1700000060,1),(a,'new-turn','inProgress',1700000100000,None,2),(b,'done-turn','completed',1700000000,1700000060,1)])
h.execute('CREATE TABLE thread_items(thread_id TEXT,turn_id TEXT,created_at_ms INT)')
h.executemany('INSERT INTO thread_items VALUES(?,?,?)',[(a,'old-turn',1900000000000),(a,'new-turn',1700000200000)])
h.commit(); h.close()
(p/'session_index.jsonl').write_text(json.dumps({'id':a,'thread_name':'中文会话标题'},ensure_ascii=False)+'\n',encoding='utf-8')
(p/'.codex-global-state.json').write_text(json.dumps({'electron-thread-read-state-v1':{'unreadByIdentity':{'identity':{'local:host':[b]}}}}),encoding='utf-8')
""");
    fixturePython.ArgumentList.Add(fixtureRoot); fixturePython.ArgumentList.Add(sessionId); fixturePython.ArgumentList.Add(otherId);
    using var setup = Process.Start(fixturePython)!;
    var fixtureError = setup.StandardError.ReadToEndAsync();
    await setup.WaitForExitAsync();
    if (setup.ExitCode != 0) throw new Exception(await fixtureError);
    var samples = new CodexSessionReader(fixtureRoot).Read();
    Check(samples.Count == 2 && samples[0].Title == "Codex · 中文会话标题", "SQLite numeric schema selection, UTF-8 session title, archived and subagent filtering");
    Check(samples[0] is { TurnId: "new-turn", Status: TaskStates.Running, FinishedAt: null } && samples[0].StartedAt.ToUnixTimeMilliseconds() == 1700000100000, "reader uses latest turn and supports millisecond timestamps");
    Check(samples[1] is { Status: TaskStates.Completed, NativeUnread: true } && samples[1].FinishedAt!.Value.ToUnixTimeSeconds() == 1700000060, "reader preserves native completed timestamp and unread result");
    Check(samples[0].LastActivityAt!.Value.ToUnixTimeMilliseconds() == 1700000200000, "work activity uses current-turn item metadata and excludes unrelated old-turn items");
    var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    try
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", fixtureRoot);
        var monitorStore = new TaskStore(); var monitorSettings = new Settings();
        using var monitor = new CodexMonitor(monitorStore, monitorSettings);
        monitor.Start(); await Task.Delay(1700);
        Check(monitor.ObservedCount == 2 && monitor.Error is null && monitorStore.Snapshot().Count == 2, "background Codex monitor integrates native sessions without RPC or model calls");
        var dismissedId = "codex-" + otherId;
        monitor.Dismiss(dismissedId); monitorStore.Remove(dismissedId); await Task.Delay(1700);
        Check(monitorStore.Find(dismissedId) is null && monitorSettings.DismissedCodexSessions.Contains(dismissedId), "dismissed Codex card does not resurrect on next scan and is persisted in settings");
    }
    finally { Environment.SetEnvironmentVariable("CODEX_HOME", previousHome); }
}

var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
var config = new Settings { Port = port };
using var api = new LocalApi(store, config); api.Start();
using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(4) };
Check((await http.GetAsync("/tasks")).StatusCode == HttpStatusCode.Unauthorized, "API rejects missing token");
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
Check((await http.GetAsync("/health")).IsSuccessStatusCode, "authenticated API health");
var payload = new StringContent("{\"id\":\"http-job\",\"title\":\"数据处理\",\"progress\":35}", Encoding.UTF8, "application/json");
Check((await http.PostAsync("/tasks", payload)).IsSuccessStatusCode && store.Find("http-job")?.Progress == 35, "real HTTP ingest with UTF-8 title");
using var malicious = new HttpRequestMessage(HttpMethod.Post, "/tasks")
{ Content = new StringContent("{\"id\":\"http-job\",\"title\":\"hijack\"}", Encoding.UTF8, "application/json") };
malicious.Headers.Add("Origin", "https://example.com");
Check((await http.SendAsync(malicious)).StatusCode == HttpStatusCode.Unauthorized, "browser-origin requests rejected even with token");
Check((await http.PostAsync("/tasks", new StringContent("{\"title\":\"bad\",\"progress\":101}", Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.BadRequest, "HTTP progress validation");
Check((await http.PostAsync("/tasks", new StringContent("{\"title\":\"bad\",\"processId\":1}", Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.BadRequest, "HTTP cannot attach processes");
Check((await http.PostAsync("/tasks", new StringContent("{\"id\":\"" + gui.Id + "\",\"title\":\"bad\"}", Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.BadRequest, "HTTP cannot alter native tasks");
Check((await http.GetAsync("/unknown")).StatusCode == HttpStatusCode.NotFound, "unknown route rejected");

var tailStore = new TaskStore();
using (var runtime = new TaskRuntime(tailStore, new Settings { AutoDiscover = false, ReadProgressBars = false }))
{
    var logfile = Path.Combine(root, "progress.log");
    await File.WriteAllTextAsync(logfile, "Epoch 1/10\n");
    var log = runtime.WatchLog(logfile, "tail test");
    await Task.Delay(850);
    Check(tailStore.Find(log.Id)?.Progress == 10, "live log initial counter");
    await File.AppendAllTextAsync(logfile, "进度 65%\r");
    await Task.Delay(850);
    Check(tailStore.Find(log.Id) is { Progress: 65, Status: TaskStates.Running }, "UTF-8 log append and carriage return");
    await File.WriteAllTextAsync(logfile, "3%\n");
    await Task.Delay(850);
    Check(tailStore.Find(log.Id)?.Progress == 3, "log truncation resets cursor");
    runtime.Remove(log.Id);
    await File.AppendAllTextAsync(logfile, "99%\n");
    await Task.Delay(850);
    Check(tailStore.Find(log.Id) is null, "removed log never resurrected");
}

if (args.Length > 0)
{
    var appExe = Path.GetFullPath(args[0]);
    Process StartApp(params string[] appArgs)
    {
        var info = new ProcessStartInfo(appExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in appArgs) info.ArgumentList.Add(value);
        return Process.Start(info)!;
    }
    async Task<CommandState> RunHelper(string command, string expected, string message)
    {
        var request = Path.Combine(root, Guid.NewGuid().ToString("N") + ".request.json");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new CommandRequest(command, root, "fixture"), TaskStore.Json));
        using var helper = StartApp("--runner", request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await helper.WaitForExitAsync(timeout.Token);
        var output = request.Replace(".request.json", ".state.json");
        var state = JsonSerializer.Deserialize<CommandState>(await File.ReadAllTextAsync(output), TaskStore.Json)!;
        Check(state.Status == expected, message);
        return state;
    }
    var success = await RunHelper("Write-Output 'progress 25%'; Start-Sleep -Milliseconds 350; Write-Output 'Epoch 2/2'", TaskStates.Completed, "real independent helper runs successful PowerShell command");
    Check(success.ExitCode == 0 && success.Progress == 100, "command success based on exit code");
    var failure = await RunHelper("Write-Output 'progress 35%'; Start-Sleep -Milliseconds 350; exit 7", TaskStates.Failed, "nonzero command exit produces failure");
    Check(failure.ExitCode == 7 && failure.Progress == 35, "failure retains actual partial progress");
    await RunHelper("throw 'expected test error'", TaskStates.Failed, "PowerShell terminating errors produce failure");
    var unicode = await RunHelper("Write-Output '进度 65%'; Start-Sleep -Milliseconds 350; exit 4", TaskStates.Failed, "Unicode command and stdout decoding");
    Check(unicode.Progress == 65, "Unicode percentage preserved");

    using var fixture = StartApp("--fixture", "74");
    try
    {
        for (var attempt = 0; attempt < 30; attempt++) { fixture.Refresh(); if (fixture.MainWindowHandle != IntPtr.Zero) break; await Task.Delay(100); }
        using var probe = StartApp("--probe", fixture.Id.ToString());
        var output = probe.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await probe.WaitForExitAsync(timeout.Token);
        var result = JsonSerializer.Deserialize<ProbeResult>(await output, TaskStore.Json);
        Check(result is { Found: true, Progress: 37 }, "real cross-process UI Automation normalizes 74/200 to 37%");
        using (var worker = StartApp("--probe-worker"))
        {
            for (var requestIndex = 0; requestIndex < 3; requestIndex++)
            {
                await worker.StandardInput.WriteLineAsync(fixture.Id.ToString());
                await worker.StandardInput.FlushAsync();
                var line = await worker.StandardOutput.ReadLineAsync(timeout.Token);
                var reading = JsonSerializer.Deserialize<ProbeResult>(line!, TaskStore.Json);
                Check(reading is { Found: true, Progress: 37 }, "reusable GUI worker request " + (requestIndex + 1));
            }
            worker.StandardInput.Close();
            await worker.WaitForExitAsync(timeout.Token);
            Check(worker.ExitCode == 0, "GUI worker shuts down cleanly when parent closes pipe");
        }

        var processStore = new TaskStore();
        using var observer = new TaskRuntime(processStore, new Settings { AutoDiscover = false, ReadProgressBars = false });
        var watched = observer.Attach(fixture.Id); observer.Start();
        fixture.Kill(); await fixture.WaitForExitAsync();
        await Task.Delay(2400);
        Check(processStore.Find(watched.Id)?.Status == TaskStates.Exited, "real process exit is neutral/unverified");
    }
    finally { if (!fixture.HasExited) fixture.Kill(); }
}

// Exercise the shipped SDKs against the live authenticated server, not mock HTTP.
await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(config, TaskStore.Json));
var sdkRoot = args.Length > 2 ? Path.GetFullPath(args[2]) : null;
if (args.Length > 2 && File.Exists(args[1]))
{
    var namedScriptPath = Path.Combine(root, "命名 验证.py");
    await File.WriteAllTextAsync(namedScriptPath, "import time\ntime.sleep(30)\n", new UTF8Encoding(false));
    var namedInfo = new ProcessStartInfo(args[1]) { UseShellExecute = false, CreateNoWindow = true };
    namedInfo.ArgumentList.Add(namedScriptPath);
    using (var namedPython = Process.Start(namedInfo)!)
    {
        try
        {
            var processLabel = await PythonTaskName.ReadProcessAsync(namedPython.Id, namedPython.StartTime.ToUniversalTime().Ticks);
            if (processLabel != "Python · 命名 验证.py") Console.WriteLine("Naming diagnostic: " + PythonTaskName.LastReadError + "; label=" + (processLabel ?? "<unavailable>"));
            Check(processLabel == "Python · 命名 验证.py", "real Windows WMI lookup resolves actual Python script with spaces and Unicode");
            var namedStore = new TaskStore();
            using var namingRuntime = new TaskRuntime(namedStore, new Settings { AutoDiscover = false, ReadProgressBars = false });
            var namedTask = namingRuntime.Attach(namedPython.Id, true);
            for (var attempt = 0; attempt < 40 && namedStore.Find(namedTask.Id)?.Title != processLabel; attempt++) await Task.Delay(50);
            Check(namedStore.Find(namedTask.Id) is { Source: "terminal", Title: "Python · 命名 验证.py" }, "live terminal task asynchronously replaces generic Python name with actual script");
            namingRuntime.Start(); namedPython.Kill(); await namedPython.WaitForExitAsync(); await Task.Delay(2300);
            Check(namedStore.Find(namedTask.Id) is { Status: TaskStates.Exited, IsUnread: true, Progress: null }, "real externally launched terminal command exits neutrally and awaits confirmation");
        }
        finally { if (!namedPython.HasExited) { namedPython.Kill(); await namedPython.WaitForExitAsync(); } }
    }
    var py = new ProcessStartInfo(args[1]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    py.Environment["PROGRESS_ISLAND_DATA"] = root;
    py.ArgumentList.Add("-c");
    py.ArgumentList.Add("import sys;sys.path.insert(0,sys.argv[1]);from progress_island import Task\nwith Task('Python SDK',task_id='sdk-python') as task:\n task.update(progress=62,message='batch',force=True)\n");
    py.ArgumentList.Add(sdkRoot!);
    using var python = Process.Start(py)!;
    await python.WaitForExitAsync();
    Check(python.ExitCode == 0 && store.Find("sdk-python")?.Status == TaskStates.Completed, "real Python SDK context manager and final completion");
    py.ArgumentList.Clear(); py.ArgumentList.Add("-c");
    py.ArgumentList.Add("import sys;sys.path.insert(0,sys.argv[1]);from progress_island import Task\nwith Task('Python exception',task_id='sdk-failure') as task:\n raise RuntimeError('expected')\n");
    py.ArgumentList.Add(sdkRoot!);
    using var pythonFailure = Process.Start(py)!;
    await pythonFailure.WaitForExitAsync();
    Check(pythonFailure.ExitCode != 0 && store.Find("sdk-failure")?.Status == TaskStates.Failed, "Python SDK reports failure and preserves original exception");
}
if (sdkRoot is not null)
{
    var ps = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    ps.Environment["PROGRESS_ISLAND_DATA"] = root;
    // Test our function definitions as an inline command. Do not change Windows execution policy.
    var script = File.ReadAllText(Path.Combine(sdkRoot, "ProgressIsland.ps1")) + "\nSend-IslandProgress -Id 'sdk-powershell' -Title 'PowerShell SDK' -Progress 50; Send-IslandProgress -Id 'sdk-powershell' -Title 'PowerShell SDK' -Status completed";
    ps.ArgumentList.Add("-NoProfile"); ps.ArgumentList.Add("-EncodedCommand"); ps.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
    using var powershell = Process.Start(ps)!;
    var psOutput = powershell.StandardOutput.ReadToEndAsync();
    var psError = powershell.StandardError.ReadToEndAsync();
    await powershell.WaitForExitAsync();
    if (powershell.ExitCode != 0 || store.Find("sdk-powershell")?.Status != TaskStates.Completed)
        Console.WriteLine($"PowerShell SDK exit {powershell.ExitCode}: {await psOutput}\n{await psError}");
    Check(powershell.ExitCode == 0 && store.Find("sdk-powershell")?.Status == TaskStates.Completed, "real Windows PowerShell SDK completion");
}
Console.WriteLine($"\n{passed} checks passed. Test data: {root}");
