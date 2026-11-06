using Modbot.Companion.Ingest;

namespace Modbot.Companion.Chat;

/// <summary>
/// How a send to AI Chat ended.
/// </summary>
/// <param name="Problem">Null when the answer came whole; otherwise one short line saying what went wrong.</param>
/// <param name="ConversationId">The conversation the server put the message in, when it said so, even if the answer then failed.</param>
/// <param name="ConversationGone">The server no longer has the conversation that was named, so the next message starts a new one.</param>
public sealed record ChatSendResult(string? Problem, Guid? ConversationId = null, bool ConversationGone = false);

/// <summary>The lines the chat panel shows when something goes wrong. One short line each, never more.</summary>
public static class ChatProblems
{
    public const string Off = "AI chat is off";
    public const string SpendLimit = "Spend limit reached";
    public const string NotAllowed = "You are not allowed to use AI chat";
    public const string Unreachable = "Could not reach the server";
    public const string Full = "This chat is full";
    public const string TooLong = "That message is too long";
    public const string Failed = "AI chat could not answer";
}

/// <summary>The companion's way into AI Chat: only whether it is on for this person, and sending one message.</summary>
public interface IChatClient
{
    /// <summary>
    /// Whether AI Chat is on and this device's owner may use it; null when the server could not be asked,
    /// so the caller keeps what it knew.
    /// </summary>
    Task<bool?> GetAvailableAsync(ServerPairing pairing, CancellationToken cancellationToken);

    /// <summary>
    /// Sends one message and waits for the whole answer. <paramref name="onAnswer"/> is given the answer
    /// as it stands, again and again, each time it has grown (or started over, when the AI looked
    /// something up and then wrote a new reply). Throws <see cref="OperationCanceledException"/> only
    /// when <paramref name="cancellationToken"/> was cancelled; every other failure is in the result.
    /// </summary>
    Task<ChatSendResult> SendAsync(
        ServerPairing pairing,
        string text,
        Guid? conversationId,
        Action<string> onAnswer,
        CancellationToken cancellationToken);
}
