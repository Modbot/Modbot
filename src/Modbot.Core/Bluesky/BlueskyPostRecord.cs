using System.Globalization;
using System.Text.Json.Nodes;

namespace Modbot.Core.Bluesky;

/// <summary>The link card under a post (<c>app.bsky.embed.external</c>), built by Modbot rather than fetched.</summary>
/// <param name="Uri">The link it opens: the first link in the text.</param>
/// <param name="Title">The card's title: the post's title, or the link's host when there is none.</param>
/// <param name="Description">The card's line under the title. May be empty.</param>
public sealed record BlueskyCard(string Uri, string Title, string Description);

/// <summary>
/// The post record Modbot puts at its key (Bluesky design §3.4, §3.5): the text, its links and tags,
/// the time, and the link card with its picture.
/// </summary>
/// <remarks>
/// <c>createdAt</c> is the time of this very send, never the first try's: Bluesky's feeds sort by
/// it, so a post sent again an hour later with the old time would sort into the past where nobody
/// sees it (fact 21). Only the key stays the same across tries.
/// </remarks>
public static class BlueskyPostRecord
{
    /// <summary>The card for a post: one when its text has a link, null when it has none.</summary>
    public static BlueskyCard? CardFor(string? title, string text)
    {
        if (BlueskyText.CardLink(text) is not { } link)
            return null;

        var cardTitle = string.IsNullOrWhiteSpace(title) ? BlueskyText.CardHost(link) : title.Trim();
        return new BlueskyCard(link, cardTitle, string.Empty);
    }

    /// <summary>The record, ready for <c>putRecord</c>.</summary>
    /// <param name="thumb">The card picture's blob, as <c>uploadBlob</c> answered, or null for a card with none.</param>
    public static JsonObject Build(string text, DateTimeOffset createdAt, BlueskyCard? card, JsonObject? thumb)
    {
        ArgumentNullException.ThrowIfNull(text);

        var record = new JsonObject
        {
            ["$type"] = "app.bsky.feed.post",
            ["text"] = text,
            ["createdAt"] = createdAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };

        var facets = BlueskyText.Facets(text);
        if (facets.Count > 0)
            record["facets"] = BlueskyText.FacetsJson(facets);

        if (card is not null)
        {
            var external = new JsonObject
            {
                ["uri"] = card.Uri,
                ["title"] = card.Title,
                ["description"] = card.Description,
            };

            if (thumb is not null)
                external["thumb"] = thumb.DeepClone();

            record["embed"] = new JsonObject
            {
                ["$type"] = "app.bsky.embed.external",
                ["external"] = external,
            };
        }

        return record;
    }
}
