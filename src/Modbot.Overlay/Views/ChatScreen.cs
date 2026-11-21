using Modbot.Companion.Chat;
using Modbot.Companion.Dictation;

namespace Modbot.Overlay.Views;

/// <summary>
/// Everything the chat panel draws, in one immutable snapshot: your message and the answers, oldest first.
/// </summary>
/// <remarks>
/// A value, not a live view, like the other panels' screens: a copy of what the chat session holds at a
/// moment. Nothing is fetched while drawing.
/// </remarks>
/// <param name="Lines">Your messages and the answers, oldest first.</param>
/// <param name="Mic">What the chat's own microphone button shows, or null for a panel with none.</param>
public sealed record ChatScreen(IReadOnlyList<ChatLine> Lines, ChatMicScreen? Mic = null)
{
    /// <summary>No messages yet.</summary>
    public static ChatScreen Empty { get; } = new([]);

    /// <summary>Whether an answer is waiting to start, which is when the typing animation moves.</summary>
    public bool IsTyping => Lines.Any(line => line.State is ChatLineState.Typing);

    /// <summary>Whether something on the panel turns on its own, which is when the host redraws each step: the typing animation, or the microphone's ring.</summary>
    public bool Moves => IsTyping || Mic is { Moves: true };

    /// <summary>Whether two screens would draw the same pixels.</summary>
    public bool LooksTheSameAs(ChatScreen other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Mic != other.Mic)
            return false;

        if (Lines.Count != other.Lines.Count)
            return false;

        for (var i = 0; i < Lines.Count; i++)
        {
            if (Lines[i] != other.Lines[i])
                return false;
        }

        return true;
    }
}

/// <summary>
/// What a chat's microphone button draws: the same states, words and meter as the overlay's Dictate
/// button, from where that chat's own <see cref="DictationController"/> is.
/// </summary>
/// <param name="State">What the picture is.</param>
/// <param name="Percent">How much of the speech model has come, 0 to 100, while getting ready.</param>
/// <param name="Level">How loud the voice is, 0 to <see cref="DictationController.LevelSteps"/>, while listening.</param>
/// <param name="Notice">A few words that take the place of the label for a few seconds, or null.</param>
public sealed record ChatMicScreen(DictateState State, int Percent = 0, int Level = 0, string? Notice = null)
{
    /// <summary>The screen for where the controller is now.</summary>
    public static ChatMicScreen Of(DictationController dictation)
    {
        ArgumentNullException.ThrowIfNull(dictation);

        var shown = ButtonScreen.Shown.WithDictate(dictation.State, dictation.Percent, dictation.Level, dictation.Notice);

        return new ChatMicScreen(shown.Dictate, shown.Percent, shown.Level, shown.Notice);
    }

    private ButtonScreen Face => ButtonScreen.Shown with { Dictate = State, Percent = Percent, Level = Level, Notice = Notice };

    /// <summary>What the button says: what it is doing, or what a tap will do.</summary>
    public string Label => Face.DictateLabel;

    /// <summary>The words beside the label while the model comes down, or empty.</summary>
    public string PercentText => Face.DictatePercent;

    /// <summary>Whether the picture turns on its own, which the host redraws each step.</summary>
    public bool Moves => Face.DictateMoves;
}
