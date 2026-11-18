using System.Text.Json.Serialization;

namespace Modbot.Api.Features.Feedback;

/// <summary>What the sender confirmed on the draft.</summary>
/// <param name="Kind"><c>bug</c>, <c>idea</c> or <c>change</c>.</param>
/// <param name="Title">A short heading. Left empty, the first line of <paramref name="Details"/> is used.</param>
/// <param name="Details">The words. Required.</param>
public sealed record FeedbackRequest(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("details")] string? Details);

/// <summary>One line of the Feedback page's list.</summary>
public sealed record FeedbackSummary(
    Guid Id,
    DateTimeOffset CreatedAt,
    string SenderName,
    string Kind,
    string Title,
    string Status);

/// <summary>Every feedback item, newest first.</summary>
public sealed record FeedbackListView(IReadOnlyList<FeedbackSummary> Items);

/// <summary>One feedback item, in full.</summary>
public sealed record FeedbackView(
    Guid Id,
    DateTimeOffset CreatedAt,
    Guid SenderUserId,
    string SenderName,
    string Kind,
    string Title,
    string Details,
    string Status,
    DateTimeOffset? StatusChangedAt,
    string? StatusChangedBy);

/// <summary>The new status.</summary>
/// <param name="Status"><c>new</c>, <c>reviewed</c>, <c>done</c> or <c>dropped</c>.</param>
public sealed record FeedbackStatusRequest(string? Status);

/// <param name="Available">The device's owner may send feedback. Not tied to AI chat or a VRChat link.</param>
/// <param name="Ai">AI is set up, so the words can be tidied. The button shows either way.</param>
public sealed record CompanionFeedbackAvailability(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("ai")] bool Ai);

/// <summary>The words that were spoken, as text.</summary>
public sealed record CompanionFeedbackTidyRequest([property: JsonPropertyName("text")] string? Text);

/// <summary>The draft the sender sees: tidied by AI, or the raw words when it could not be.</summary>
/// <param name="Kind">Null when the kind is left for the sender to pick.</param>
/// <param name="Title">Null when there is no title yet.</param>
/// <param name="Details">Always set.</param>
/// <param name="Tidied">True only when AI wrote the draft.</param>
public sealed record CompanionFeedbackDraft(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("details")] string Details,
    [property: JsonPropertyName("tidied")] bool Tidied);

/// <summary>The id of the item a send saved.</summary>
public sealed record CompanionFeedbackSaved([property: JsonPropertyName("id")] Guid Id);
