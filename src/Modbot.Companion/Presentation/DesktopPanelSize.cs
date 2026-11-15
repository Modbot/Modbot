using Modbot.Companion.Overlay;

namespace Modbot.Companion.Presentation;

/// <summary>
/// How much bigger than ordinary a desktop window has been dragged to be, as a number of times:
/// 1 is the size it opens at and 2 is twice that, in each direction.
/// </summary>
/// <remarks>
/// The same bounds the headset's panels keep for their resize corner
/// (<see cref="OverlayPlacement.MinShape"/> and <see cref="OverlayPlacement.MaxShape"/>), so a
/// window is never smaller than ordinary and never so big that its corner cannot be reached. The
/// number is a ratio rather than a size in pixels because the ordinary size is not fixed: the main
/// panel's follows VRChat's window.
/// </remarks>
public static class DesktopPanelSize
{
    /// <summary>The ordinary size, which is also the least.</summary>
    public const double Least = OverlayPlacement.MinShape;

    /// <summary>Twice the ordinary size.</summary>
    public const double Most = OverlayPlacement.MaxShape;

    /// <summary>The number brought inside the bounds; one that is not a number is the ordinary size.</summary>
    public static double Clamp(double times)
        => double.IsFinite(times) ? Math.Clamp(times, Least, Most) : Least;

    /// <summary>
    /// How many times the ordinary size a window of this size is, inside the bounds. The ordinary
    /// size not being a size at all gives 1.
    /// </summary>
    public static double TimesOrdinary(double size, double ordinary)
        => double.IsFinite(ordinary) && ordinary > 0 ? Clamp(size / ordinary) : Least;
}
