using Modbot.Overlay.OpenXr;

namespace Modbot.Overlay.Views;

/// <summary>One hint: a control and what it does, such as "Grip" and "move".</summary>
public sealed record ControlHint(string Control, string Action);

/// <summary>
/// The short strip of controller hints beside the bar while a hand is on the panel. A null hint is
/// one that is left out.
/// </summary>
/// <remarks>
/// <para><strong>Each hint is what the controls really do.</strong> A grip takes the panel; with
/// it carried, the other hand's grip sizes it and a stick pushes or pulls it
/// (<c>OverlayInteraction.Hold</c> and <c>Stretch</c>). The stick only does that while the panel
/// is held: pointing without holding, the same stick scrolls the list. So "closer / farther" is
/// shown only while a hand holds the panel, and the stick is not mentioned while pointing.</para>
/// <para><strong>Named for the controller.</strong> The simple controller's grab is its menu
/// button and it has no stick; the Vive has a trackpad. With no controller known (SteamVR does
/// not say), plain "Grip" and "Stick".</para>
/// </remarks>
/// <param name="Move">Taking hold of the panel; left out once it is held.</param>
/// <param name="Resize">Both hands gripping.</param>
/// <param name="Distance">The stick pushing and pulling the held panel; left out unless it is held.</param>
public sealed record ControlHints(ControlHint? Move, ControlHint? Resize, ControlHint? Distance)
{
    /// <param name="controller">The controller in the hand on the panel, or null when not known.</param>
    /// <param name="holding">Whether a hand is holding the panel, rather than only pointing at it.</param>
    public static ControlHints For(ControllerProfile? controller, bool holding)
    {
        var simple = controller?.Path == ControllerBindings.Simple.Path;
        var one = simple ? "Menu button" : "Grip";
        var both = simple ? "Both menu buttons" : "Both grips";

        var stick = controller is null
            ? "Stick"
            : controller.Scroll is null
                ? null
                : controller.Path == ControllerBindings.HtcVive.Path ? "Trackpad" : "Stick";

        return holding
            ? new ControlHints(null, new ControlHint(both, "larger / smaller"), stick is null ? null : new ControlHint(stick, "closer / farther"))
            : new ControlHints(new ControlHint(one, "move"), new ControlHint(both, "larger / smaller"), null);
    }
}
