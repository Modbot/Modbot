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

    /// <summary>
    /// For a window that has just been made bigger: no taller than the free area of the monitor most of it is on,
    /// and moved wholly inside that area. The size is the window's own (<see cref="Layoutable.Width"/> and
    /// <see cref="Layoutable.Height"/>), which is up to date straight after it is set.
    /// </summary>
    public static void FitOnScreen(this Window window)
    {
        var areas = window.Screens.All.Select(screen => Box(screen.WorkingArea)).ToList();
        var scaling = window.RenderScaling <= 0 ? 1 : window.RenderScaling;
        var here = new ScreenBox(
            window.Position.X,
            window.Position.Y,
            (int)Math.Round(window.Width * scaling),
            (int)Math.Round(window.Height * scaling));

        var fitted = KeepOnScreen.Fit(here, areas);

        if (fitted.Height != here.Height)
            window.Height = fitted.Height / scaling;

        if (fitted.X != here.X || fitted.Y != here.Y)
            window.Position = new PixelPoint(fitted.X, fitted.Y);
    }

    /// <summary>The work area as a box.</summary>
    public static ScreenBox Box(PixelRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
