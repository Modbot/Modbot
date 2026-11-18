using System.Text.Json;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Chat;

/// <summary>What the model made of spoken feedback.</summary>
/// <param name="Kind">One of <see cref="FeedbackKinds"/>, or null when the model named none of them.</param>
public sealed record FeedbackDraftText(string? Kind, string? Title, string Details);

/// <summary>
/// Turns what a member of staff said out loud into a kind, a title and details, and takes the names
/// of people, worlds and groups out of them (feedback design §4).
/// </summary>
/// <remarks>
/// The model is asked for one small JSON object. What comes back is read leniently, because models
/// wrap JSON in fences and sentences: anything that does not hold usable details is no draft, and the
/// caller then gives the sender their own words back, which is a worse draft and never a failure.
/// </remarks>
public static class FeedbackTidy
{
    /// <summary>Tokens the call may write. A title and a few sentences.</summary>
    public const int MaxOutputTokens = 800;

    public const string Instructions = """
        You tidy feedback about Modbot, a moderation tool for VRChat groups, that a member of staff
        said out loud. The text may have mistakes from speech to text.
        Answer with one JSON object and nothing else, with these fields:
        "kind": "bug" (something is broken), "idea" (something new) or "change" (something that
        exists should work differently).
        "title": at most 80 characters.
        "details": the feedback in clear sentences, in the speaker's language, without adding anything
        they did not say.
        Remove the names of people, worlds and groups: write "a member", "a world" or "a group"
        instead. Keep the names of Modbot's own pages and buttons.
        """;

    /// <summary>The words as the model is shown them. Anything longer is cut.</summary>
    public const int MaxTextLength = 6000;

    /// <summary>Reads the model's answer. Null when it holds no details.</summary>
    public static FeedbackDraftText? Parse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return null;

        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            using var document = JsonDocument.Parse(answer[start..(end + 1)]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var details = Text(root, "details");
            if (details is null)
                return null;

            var kind = Text(root, "kind")?.ToLowerInvariant();
            var title = Text(root, "title");

            if (title is { Length: > FeedbackItem.MaxTitleLength })
                title = string.Concat(title.AsSpan(0, FeedbackItem.MaxTitleLength - 1), "…");

            if (details.Length > FeedbackItem.MaxDetailsLength)
                details = details[..FeedbackItem.MaxDetailsLength];

            return new FeedbackDraftText(kind is not null && FeedbackKinds.All.Contains(kind) ? kind : null, title, details);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
