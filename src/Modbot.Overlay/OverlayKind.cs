namespace Modbot.Overlay;

/// <summary>
/// Which of the two headset panels this is.
/// </summary>
/// <remarks>
/// They are separate overlays, not two states of one: separate settings, separate on/off
/// switches, separate handles in the VR runtime, and one of them takes no input at all (two
/// overlay modes design §1).
/// </remarks>
public enum OverlayKind
{
    /// <summary>
    /// The panel a moderator reads and moves around: the roster, recent events, a person's card.
    /// Fixed to the head, a hand or the room, and pointed at with a controller.
    /// </summary>
    Main,

    /// <summary>
    /// The pop-ups, fixed to a corner of the view. Never pointed at, and shown even when the main
    /// panel is switched off.
    /// </summary>
    Notification,
}
