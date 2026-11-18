using Modbot.Companion.Feedback;

namespace Modbot.Overlay.Views;

/// <summary>
/// Everything the feedback panel draws, in one immutable snapshot: the draft the person looks over
/// before sending it.
/// </summary>
/// <remarks>
/// A value, like the other panels' screens. The headset has no keyboard, so the words are shown as they
/// were said and tidied; what can be changed here is the kind, and whether to send or throw it away.
/// </remarks>
/// <param name="Kind">"bug", "idea" or "change", or null while none is chosen.</param>
/// <param name="Title">The short heading, empty when there is none.</param>
/// <param name="Details">The words. The whole text is sent, though the panel only has room to show the first lines.</param>
/// <param name="Problem">One sentence saying why the last send did not go, or null.</param>
/// <param name="Sending">Whether a send is under way, which holds the buttons.</param>
public sealed record FeedbackScreen(string? Kind, string Title, string Details, string? Problem = null, bool Sending = false)
{
    /// <summary>The panel as it first opens on a draft: the kind the server chose, if it did.</summary>
    public static FeedbackScreen Of(FeedbackDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return new FeedbackScreen(FeedbackKinds.Known(draft.Kind), draft.Title ?? string.Empty, draft.Details);
    }

    /// <summary>Whether Send does anything: a kind is chosen, there are words, and no send is under way.</summary>
    public bool CanSend => !Sending && FeedbackKinds.Known(Kind) is not null && !string.IsNullOrWhiteSpace(Details);

    /// <summary>Whether two screens would draw the same pixels.</summary>
    public bool LooksTheSameAs(FeedbackScreen other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return this == other;
    }
}
