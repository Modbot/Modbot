using Modbot.Companion.Chat;

namespace Modbot.Overlay.Views;

/// <summary>
/// Everything the chat panel draws, in one immutable snapshot: your message and the answers, oldest first.
/// </summary>
/// <remarks>
/// A value, not a live view, like the other panels' screens: a copy of what the chat session holds at a
/// moment. Nothing is fetched while drawing.
/// </remarks>
public sealed record ChatScreen(IReadOnlyList<ChatLine> Lines)
{
    /// <summary>No messages yet.</summary>
    public static ChatScreen Empty { get; } = new([]);

    /// <summary>Whether an answer is waiting to start, which is when the typing animation moves.</summary>
    public bool IsTyping => Lines.Any(line => line.State is ChatLineState.Typing);

    /// <summary>Whether two screens would draw the same pixels.</summary>
    public bool LooksTheSameAs(ChatScreen other)
    {
        ArgumentNullException.ThrowIfNull(other);

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
