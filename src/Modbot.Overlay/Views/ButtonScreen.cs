using Modbot.Companion.Overlay;

namespace Modbot.Overlay.Views;

/// <summary>
/// Everything the show and hide button draws: whether the headset panel is up, and the shortcut
/// written under the label.
/// </summary>
/// <param name="PanelShown">
/// Whether the headset panel is showing. The button reads "Hide" while it is and "Show" while it is
/// not.
/// </param>
/// <param name="Shortcut">The words under the label. One place holds the default (<see cref="OverlayButton.DefaultShortcut"/>).</param>
public sealed record ButtonScreen(bool PanelShown, string Shortcut)
{
    /// <summary>The panel is up: the button offers to hide it.</summary>
    public static ButtonScreen Shown { get; } = new(true, OverlayButton.DefaultShortcut);

    /// <summary>The panel is down: the button offers to bring it back.</summary>
    public static ButtonScreen Hidden { get; } = new(false, OverlayButton.DefaultShortcut);

    /// <summary>What the button says: what a click will do.</summary>
    public string Label => PanelShown ? "Hide" : "Show";
}
