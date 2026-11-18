using Modbot.Companion.Ingest;

namespace Modbot.Companion.Feedback;

/// <summary>The three kinds of feedback, as the server spells them.</summary>
public static class FeedbackKinds
{
    public const string Bug = "bug";
    public const string Idea = "idea";
    public const string Change = "change";

    public static readonly IReadOnlyList<string> All = [Bug, Idea, Change];

    /// <summary>The kind in the server's spelling, or null when <paramref name="kind"/> is not one of the three.</summary>
    public static string? Known(string? kind)
        => All.FirstOrDefault(known => string.Equals(known, kind?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>What the person sees to edit before sending. The kind and the title are null when nobody has chosen them yet.</summary>
public sealed record FeedbackDraft(string? Kind, string? Title, string Details, bool Tidied);

/// <summary>How a send ended: null when the server saved it, otherwise one sentence saying why not.</summary>
public sealed record FeedbackSendResult(string? Problem);

/// <summary>The companion's side of the paired server's feedback requests.</summary>
public interface IFeedbackClient
{
    /// <summary>
    /// Whether this device's owner may send feedback, as the server says. Null when the server could not
    /// be asked, which leaves the last answer as it was.
    /// </summary>
    Task<bool?> GetAvailableAsync(ServerPairing pairing, CancellationToken cancellationToken);

    /// <summary>
    /// The words that were said, tidied by the server when AI is on. Null when the server could not be
    /// reached or refused, in which case the caller uses the words as they were.
    /// </summary>
    Task<FeedbackDraft?> TidyAsync(ServerPairing pairing, string text, CancellationToken cancellationToken);

    /// <summary>Saves what the person confirmed on the paired server.</summary>
    Task<FeedbackSendResult> SendAsync(
        ServerPairing pairing,
        string kind,
        string title,
        string details,
        CancellationToken cancellationToken);
}
