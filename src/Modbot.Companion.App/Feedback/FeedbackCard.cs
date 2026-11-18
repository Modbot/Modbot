using Modbot.Companion.Feedback;
using Modbot.Overlay.Views;

namespace Modbot.Companion.App.Feedback;

/// <summary>
/// What a Feedback button shows: nothing while it is not offered, otherwise the stage the controller
/// is at, with how far the speech model has come, how loud the voice is and a notice.
/// </summary>
/// <param name="State">The controller's stage, or null while the button is not offered.</param>
/// <param name="Percent">How much of the speech model has come, 0 to 100, while getting ready.</param>
/// <param name="Level">How loud the voice is, 0 to <see cref="Modbot.Companion.Dictation.DictationController.LevelSteps"/>, while listening.</param>
/// <param name="Notice">A few words that take the place of the label for a few seconds ("Sent", "Nothing heard"), or null.</param>
internal sealed record FeedbackCard(FeedbackState? State, int Percent = 0, int Level = 0, string? Notice = null)
{
    public const string Idle = "Feedback";

    /// <summary>The button is not offered.</summary>
    public static FeedbackCard Off { get; } = new(State: null);

    public bool Offered => State is not null;

    /// <summary>What the button says: what it is doing, or what a tap will do. A notice takes its place while at idle.</summary>
    public string Label => State switch
    {
        FeedbackState.Listening => "Listening",
        FeedbackState.Writing => "Writing",
        FeedbackState.Tidying => "Tidying",
        FeedbackState.Sending => "Sending",
        FeedbackState.Preparing => "Getting ready",
        FeedbackState.Idle when Notice is { Length: > 0 } notice => notice,
        _ => Idle,
    };

    /// <summary>The words beside the label while the model comes down, or empty.</summary>
    public string PercentText => State is FeedbackState.Preparing ? $"{Math.Clamp(Percent, 0, 100)}%" : "";

    /// <summary>The microphone picture for the stage, drawn the way Dictate's is.</summary>
    public DictateState Picture => State switch
    {
        null => DictateState.Off,
        FeedbackState.Listening => DictateState.Listening,
        FeedbackState.Writing or FeedbackState.Tidying => DictateState.Writing,
        FeedbackState.Sending => DictateState.Sending,
        FeedbackState.Preparing => DictateState.GettingReady,
        _ => DictateState.Idle,
    };

    /// <summary>Whether the picture turns on its own, which the window redraws each step.</summary>
    public bool Moves => Picture is DictateState.Writing or DictateState.Sending or DictateState.GettingReady;

    public static FeedbackCard Of(FeedbackController controller)
        => new(controller.State, controller.Percent, controller.Level, controller.Notice);
}
