using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Views;

/// <summary>How far a stick hold has got, as the button shows it.</summary>
/// <param name="SecondsLeft">Whole seconds still to go: 5, 4, 3, 2, 1.</param>
/// <param name="Progress">How far through, 0 to 1, in the steps <see cref="StickHold.ProgressSteps"/> sets.</param>
public sealed record ButtonCountdown(int SecondsLeft, float Progress);

/// <summary>
/// Everything the show and hide button draws: whether the headset panel is up, the shortcut written
/// under the label, and the count while the shortcut stick is being held.
/// </summary>
/// <param name="PanelShown">
/// Whether the headset panel is showing. The button reads "Hide" while it is and "Show" while it is
/// not.
/// </param>
/// <param name="Shortcut">The words under the label. Empty while the shortcut is off. One place holds the default (<see cref="OverlayButton.DefaultShortcut"/>).</param>
/// <param name="Countdown">Set while the stick is held, which puts the seconds left and a filling bar on the face.</param>
public sealed record ButtonScreen(bool PanelShown, string Shortcut, ButtonCountdown? Countdown = null)
{
    /// <summary>The panel is up: the button offers to hide it.</summary>
    public static ButtonScreen Shown { get; } = new(true, OverlayButton.DefaultShortcut);

    /// <summary>The panel is down: the button offers to bring it back.</summary>
    public static ButtonScreen Hidden { get; } = new(false, OverlayButton.DefaultShortcut);

    /// <summary>What the button says: what a click will do.</summary>
    public string Label => PanelShown ? "Hide" : "Show";

    /// <summary>
    /// The line under the label: the shortcut, or while the stick is held what it will do and when
    /// ("Hide in 3"), so the hold can be seen counting down.
    /// </summary>
    public string Line => Countdown is { } count ? $"{Label} in {count.SecondsLeft}" : Shortcut;
}
