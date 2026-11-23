using Avalonia;
using Avalonia.Controls;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.App;

/// <summary>Puts a dragged desktop window back on the screen when the drag ends.</summary>
internal static class WindowSnap
{
    /// <summary>
    /// Moves the window wholly inside the free area of the monitor most of it is on, if any of it is outside.
    /// True when it moved.
    /// </summary>
    public static bool BackOnScreen(this Window window)
    {
        var areas = window.Screens.All.Select(screen => Box(screen.WorkingArea)).ToList();
        var scaling = window.RenderScaling <= 0 ? 1 : window.RenderScaling;
        var here = new ScreenBox(
            window.Position.X,
            window.Position.Y,
            (int)Math.Round(window.Bounds.Width * scaling),
            (int)Math.Round(window.Bounds.Height * scaling));

        var (x, y) = KeepOnScreen.Back(here, areas);

        if (x == here.X && y == here.Y)
            return false;

        window.Position = new PixelPoint(x, y);
        return true;
    }

    /// <summary>The work area as a box.</summary>
    public static ScreenBox Box(PixelRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
