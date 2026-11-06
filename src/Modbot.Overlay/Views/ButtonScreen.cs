using Modbot.Companion.Dictation;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Views;

/// <summary>How far a stick hold has got, as the button shows it.</summary>
/// <param name="SecondsLeft">Whole seconds still to go: 5, 4, 3, 2, 1.</param>
/// <param name="Progress">How far through, 0 to 1, in the steps <see cref="StickHold.ProgressSteps"/> sets.</param>
public sealed record ButtonCountdown(int SecondsLeft, float Progress);

/// <summary>What the Dictate button on the button's second row is doing.</summary>
public enum DictateState
{
    /// <summary>No Dictate button: the card is the show and hide button alone.</summary>
    Off,

    /// <summary>Ready to listen: a microphone.</summary>
    Idle,

    /// <summary>Listening: the microphone with sound arcs that move.</summary>
    Listening,

    /// <summary>Writing out what was said: a ring of dots that turns.</summary>
    Writing,

    /// <summary>Fetching the speech model the first time: the ring and how far it has got.</summary>
    GettingReady,
}

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
/// <param name="Dictate">The Dictate button under the show and hide button. <see cref="DictateState.Off"/> leaves the card as it was.</param>
/// <param name="Percent">How much of the speech model has come, 0 to 100, while <paramref name="Dictate"/> is getting ready.</param>
/// <param name="Frame">Which of 8 steps the moving picture is at. Set by the button's host while something moves, never by the caller.</param>
public sealed record ButtonScreen(
    bool PanelShown,
    string Shortcut,
    ButtonCountdown? Countdown = null,
    DictateState Dictate = DictateState.Off,
    int Percent = 0,
    int Frame = 0)
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

    /// <summary>
    /// The screen with the Dictate row set from where the controller is. A <paramref name="state"/> of
    /// null leaves the row off.
    /// </summary>
    public ButtonScreen WithDictate(DictationState? state, int percent) => state switch
    {
        null => this with { Dictate = DictateState.Off, Percent = 0 },
        DictationState.Listening => this with { Dictate = DictateState.Listening, Percent = 0 },
        DictationState.Writing => this with { Dictate = DictateState.Writing, Percent = 0 },
        DictationState.Preparing => this with { Dictate = DictateState.GettingReady, Percent = percent },
        _ => this with { Dictate = DictateState.Idle, Percent = 0 },
    };

    /// <summary>What the Dictate button says: what it is doing, or what a tap will do.</summary>
    public string DictateLabel => Dictate switch
    {
        DictateState.Listening => "Listening",
        DictateState.Writing => "Writing",
        DictateState.GettingReady => "Getting ready",
        _ => "Dictate",
    };

    /// <summary>The words beside the Dictate label while the model comes down, or empty.</summary>
    public string DictatePercent => Dictate is DictateState.GettingReady ? $"{Math.Clamp(Percent, 0, 100)}%" : "";

    /// <summary>Whether the Dictate button has something that moves, which the host redraws each step.</summary>
    public bool DictateMoves => Dictate is DictateState.Listening or DictateState.Writing or DictateState.GettingReady;
}
