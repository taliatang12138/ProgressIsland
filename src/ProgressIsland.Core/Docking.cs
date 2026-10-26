namespace ProgressIsland.Core;

public enum DockSide { Top, Right, Bottom, Left }

public readonly record struct DockArea(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public readonly record struct DockBounds(double X, double Y, double Width, double Height);

public static class Docking
{
    public static bool IsVertical(DockSide side) => side is DockSide.Left or DockSide.Right;

    public static bool Contains(DockBounds bounds, double x, double y) =>
        x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height;

    // A long compact task strip may shrink along its edge when it opens. Keep the
    // hover point inside the panel without moving its dock edge or saved anchor.
    public static DockBounds KeepPointerInside(DockArea area, DockSide side, DockBounds panel, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return panel;
        if (IsVertical(side))
        {
            var inset = Math.Min(12, panel.Height / 2);
            return panel with { Y = Math.Clamp(Math.Clamp(panel.Y, y - panel.Height + inset, y - inset), area.Y, area.Bottom - panel.Height) };
        }
        else
        {
            var inset = Math.Min(12, panel.Width / 2);
            return panel with { X = Math.Clamp(Math.Clamp(panel.X, x - panel.Width + inset, x - inset), area.X, area.Right - panel.Width) };
        }
    }

    // Anchor describes the centre along an edge, not a window corner. Resizing must not move the dock.
    public static DockBounds Place(DockArea area, DockSide side, double anchor, bool expanded, double panelHeight = 594, int compactTasks = 0)
    {
        anchor = double.IsFinite(anchor) ? Math.Clamp(anchor, 0, 1) : 0.5;
        var vertical = IsVertical(side);
        compactTasks = Math.Clamp(compactTasks, 0, 500);
        var width = Math.Min(area.Width, expanded ? 550 : vertical ? compactTasks > 0 ? 142 : 72 : Math.Max(360, 170 + compactTasks * 130));
        var height = Math.Min(area.Height, expanded ? panelHeight : vertical ? Math.Max(214, 100 + compactTasks * 58) : 70);
        var x = side switch
        {
            DockSide.Left => area.X,
            DockSide.Right => area.Right - width,
            _ => Math.Clamp(area.X + area.Width * anchor - width / 2, area.X, area.Right - width)
        };
        var y = side switch
        {
            DockSide.Top => area.Y,
            DockSide.Bottom => area.Bottom - height,
            _ => Math.Clamp(area.Y + area.Height * anchor - height / 2, area.Y, area.Bottom - height)
        };
        return new(x, y, width, height);
    }

    public static DockSide NearestEdge(DockArea area, double x, double y)
    {
        // Stable ordering makes corner drops deterministic; the point may lie just outside a work area.
        return new[]
        {
            (DockSide.Top, Math.Abs(y - area.Y)), (DockSide.Right, Math.Abs(x - area.Right)),
            (DockSide.Bottom, Math.Abs(y - area.Bottom)), (DockSide.Left, Math.Abs(x - area.X))
        }.MinBy(pair => pair.Item2).Item1;
    }

    public static double AnchorAt(DockArea area, DockSide side, double x, double y) =>
        Math.Clamp(IsVertical(side) ? (y - area.Y) / area.Height : (x - area.X) / area.Width, 0, 1);
}
