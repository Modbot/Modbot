namespace Modbot.Companion.Presentation;

/// <summary>A rectangle in screen pixels: a window, or the part of a monitor the taskbar leaves free.</summary>
public readonly record struct ScreenBox(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>
/// Where a window goes when it was left, or dragged, partly or wholly off the screen: wholly back inside
/// the monitor most of it is on. A sliver still showing counts as off screen. The same rule serves the end
/// of a drag and the placing of a window beside VRChat's menu.
/// </summary>
public static class KeepOnScreen
{
    /// <summary>
    /// The window's top-left corner after it is moved back inside the free area of the monitor it overlaps most
    /// (the nearest one when it overlaps none). A window bigger than that area lines up with the area's top left.
    /// </summary>
    public static (int X, int Y) Back(ScreenBox window, IReadOnlyList<ScreenBox> areas)
    {
        if (Pick(window, areas) is not { } area)
            return (window.X, window.Y);

        return Inside(window, area);
    }

    /// <summary>
    /// The window as it is after a resize: no taller than the free area of the monitor it overlaps most, and
    /// moved wholly back inside it. Wider than the area it lines up with its top left, as in <see cref="Back"/>.
    /// </summary>
    public static ScreenBox Fit(ScreenBox window, IReadOnlyList<ScreenBox> areas)
    {
        if (Pick(window, areas) is not { } area)
            return window;

        var fitted = window with { Height = Math.Min(window.Height, area.Height) };
        var (x, y) = Inside(fitted, area);
        return fitted with { X = x, Y = y };
    }

    /// <summary>The window's top-left corner after it is moved inside one area.</summary>
    public static (int X, int Y) Inside(ScreenBox window, ScreenBox area)
    {
        return (
            Math.Clamp(window.X, area.X, Math.Max(area.X, area.Right - window.Width)),
            Math.Clamp(window.Y, area.Y, Math.Max(area.Y, area.Bottom - window.Height)));
    }

    /// <summary>The area the window overlaps most, else the one whose middle is nearest the window's middle; none when there are no areas.</summary>
    public static ScreenBox? Pick(ScreenBox window, IReadOnlyList<ScreenBox> areas)
    {
        ScreenBox? best = null;
        var bestOverlap = 0L;
        var bestDistance = double.MaxValue;

        foreach (var area in areas)
        {
            var overlap = Overlap(window, area);
            var distance = Distance(window, area);

            if (overlap > bestOverlap || (overlap == bestOverlap && distance < bestDistance))
            {
                best = area;
                bestOverlap = overlap;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static long Overlap(ScreenBox a, ScreenBox b)
    {
        var width = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return width > 0 && height > 0 ? (long)width * height : 0;
    }

    private static double Distance(ScreenBox a, ScreenBox b)
    {
        var dx = (a.X + (a.Width / 2.0)) - (b.X + (b.Width / 2.0));
        var dy = (a.Y + (a.Height / 2.0)) - (b.Y + (b.Height / 2.0));
        return (dx * dx) + (dy * dy);
    }
}
