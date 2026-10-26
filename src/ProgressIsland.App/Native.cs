using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProgressIsland.App;

internal static class Native
{
    internal delegate bool EnumWindow(IntPtr handle, IntPtr state);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindow callback, IntPtr state);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr handle, int id);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct MonitorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(MonitorPoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    internal static double MonitorScale(System.Drawing.Rectangle bounds)
    {
        try
        {
            var monitor = MonitorFromPoint(new MonitorPoint { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 }, 2);
            if (GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0) return x / 96d;
        }
        catch (DllNotFoundException) { }
        return 1;
    }
    internal static void PlacePhysical(IntPtr handle, Core.DockBounds bounds, double scale) =>
        SetWindowPos(handle, new IntPtr(-1), (int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale), 0x0010); // no activation
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id;
        public IntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    internal static Dictionary<int, int> ProcessParents()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return parents;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = "" };
            if (Process32FirstW(snapshot, ref entry))
                do { parents[(int)entry.Id] = (int)entry.Parent; } while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return parents;
    }
    internal static bool RegisterToggle(IntPtr handle) => RegisterHotKey(handle, 1, 0x4003, 0x50); // Ctrl+Alt+P, no-repeat
    internal static HashSet<int> VisibleConsoleShells(IReadOnlyDictionary<int, int> parents, IReadOnlyDictionary<int, string> names)
    {
        var shells = new HashSet<int>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            GetWindowThreadProcessId(handle, out var process);
            if (names.TryGetValue((int)process, out var name))
            {
                if (Core.TerminalProcessTree.IsShell(name)) shells.Add((int)process);
                else if ((name.Equals("conhost", StringComparison.OrdinalIgnoreCase) || name.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase)) && parents.TryGetValue((int)process, out var parent)) shells.Add(parent);
            }
            return true;
        }, IntPtr.Zero);
        return shells;
    }
    internal static void FocusProcess(int id, long? startTicks = null)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            if (startTicks is not null && process.StartTime.ToUniversalTime().Ticks != startTicks) return;
            var hwnd = process.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, 9);
            SetForegroundWindow(hwnd);
        }
        catch { }
    }
}
