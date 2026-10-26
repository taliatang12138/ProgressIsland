using ProgressIsland.Core;
using Forms = System.Windows.Forms;

namespace ProgressIsland.App;

internal sealed record DockScreen(string Name, DockArea Area, double Scale)
{
    private static DockScreen From(Forms.Screen screen)
    {
        var scale = Native.MonitorScale(screen.Bounds);
        var area = screen.WorkingArea;
        return new(screen.DeviceName, new(area.X / scale, area.Y / scale, area.Width / scale, area.Height / scale), scale);
    }
    public static DockScreen Resolve(string? name) => From(Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == name)
        ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0]);
    public static (DockScreen screen, double x, double y) AtCursor()
    {
        var cursor = Forms.Cursor.Position;
        var screen = From(Forms.Screen.FromPoint(cursor));
        return (screen, cursor.X / screen.Scale, cursor.Y / screen.Scale);
    }
}
