using Modbot.Companion.Clips;

namespace Modbot.Companion.Presentation;

/// <summary>
/// Whether Modbot's bubble is on screen in VRChat's HUD row right now, and if so where, how big,
/// what it says and whether it is lit (<c>2026-10-05-desktop-overlay-escape-menu-design.md</c> §3).
/// </summary>
/// <remarks>
/// Plain code, so the decision can be tested without a screen or a VRChat. The window that draws
/// the bubble only carries it out.
/// </remarks>
/// <param name="Metrics">The bubble's sizes, in pixels of VRChat's picture, for this window size.</param>
/// <param name="ScreenX">Where its left edge goes across the whole desktop, in pixels.</param>
/// <param name="ScreenY">Where its top edge goes down the whole desktop, in pixels.</param>
/// <param name="Label">The shortcut as VRChat writes its own labels, such as <c>Ctrl+Alt+M</c>.</param>
/// <param name="Lit">Whether the desktop overlay is open: Modbot purple, rather than grey.</param>
public sealed record EscapeBubblePlan(EscapeBubbleMetrics Metrics, int ScreenX, int ScreenY, string Label, bool Lit)
{
    /// <summary>
    /// The bubble for what is known now, or null when there should be none: the desktop overlay is
    /// off, VRChat's window is not there or is minimised, or its picture is too small to place
    /// anything in. VRChat does not have to be the window in front.
    /// </summary>
    /// <param name="overlayOn">The desktop overlay setting.</param>
    /// <param name="overlayVisible">Whether the overlay's window is up now.</param>
    /// <param name="window">VRChat's window as Windows last described it.</param>
    /// <param name="clientLeft">Where VRChat's picture starts across the desktop.</param>
    /// <param name="clientTop">Where VRChat's picture starts down the desktop.</param>
    /// <param name="shortcut">The overlay's shortcut as saved, such as <c>mod+alt+m</c>.</param>
    public static EscapeBubblePlan? For(
        bool overlayOn,
        bool overlayVisible,
        GameWindow window,
        int clientLeft,
        int clientTop,
        string? shortcut)
    {
        if (!overlayOn || !window.HasPicture)
            return null;

        if (EscapeBubbleLayout.For(window.Width, window.Height) is not { } metrics)
            return null;

        var (x, y) = metrics.Place();
        return new EscapeBubblePlan(
            metrics,
            clientLeft + x,
            clientTop + y,
            EscapeBubbleLayout.Label(shortcut) ?? string.Empty,
            overlayVisible);
    }
}
