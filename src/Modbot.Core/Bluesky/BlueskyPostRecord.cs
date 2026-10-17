using System.Globalization;
using System.Text.Json.Nodes;

namespace Modbot.Core.Bluesky;

/// <summary>The link card under a post (<c>app.bsky.embed.external</c>), built by Modbot rather than fetched. It has no picture of its own: a post with a picture shows that instead.</summary>
/// <param name="Uri">The link it opens: the first link in the text.</param>
/// <param name="Title">The card's title: the post's title, or the link's host when there is none.</param>
/// <param name="Description">The card's line under the title. May be empty.</param>
public sealed record BlueskyCard(string Uri, string Title, string Description);

/// <summary>
/// One picture a post carries in full (<c>app.bsky.embed.images</c>), instead of a link card.
/// </summary>
/// <param name="Blob">The picture's blob, as <c>uploadBlob</c> answered.</param>
/// <param name="Alt">The picture's description for people who cannot see it. Bluesky requires the field; it may be empty.</param>
/// <param name="Width">The picture's width, or null when not known.</param>
/// <param name="Height">The picture's height, or null when not known. Width and height travel together, so the feed keeps the picture's shape instead of cropping it.</param>
public sealed record BlueskyImage(JsonObject Blob, string Alt, int? Width, int? Height);

/// <summary>
/// The one video a post carries in full (<c>app.bsky.embed.video</c>), instead of pictures or a link card.
/// </summary>
/// <param name="Blob">The video's blob, as <c>uploadBlob</c> answered.</param>
/// <param name="Alt">The video's description for people who cannot see it. May be empty.</param>
/// <param name="Width">The video's width, or null when not known.</param>
/// <param name="Height">The video's height, or null when not known. Width and height travel together.</param>
public sealed record BlueskyVideoEmbed(JsonObject Blob, string Alt, int? Width, int? Height);

/// <summary>The post a reply answers: its address and content id, as Bluesky gave them.</summary>
public sealed record BlueskyReplyTo(string Uri, string Cid);

/// <summary>
/// The post record Modbot puts at its key (Bluesky design §3.4, §3.5): the text, its links and tags,
/// the time, and the picture or the link card.
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
    /// <param name="replyTo">The post this one answers, or null for a post of its own. Modbot replies only to its own top-level posts, so that post is both the root and the parent.</param>
    /// <param name="images">The post's pictures, up to four, or null for none. A post has the pictures or the link card, never both: with a picture, <paramref name="card"/> is left out (the link stays clickable in the text).</param>
    /// <param name="langs">The languages the post is written in (<c>langs</c>), up to three; null or empty leaves the field out.</param>
    /// <param name="video">The post's one video, or null for none. A post has the video, the pictures or the link card: a video leaves out both of the others (Bluesky allows no pictures beside a video).</param>
    public static JsonObject Build(
        string text, DateTimeOffset createdAt, BlueskyCard? card, BlueskyReplyTo? replyTo = null, IReadOnlyList<BlueskyImage>? images = null,
        IReadOnlyList<string>? langs = null, BlueskyVideoEmbed? video = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var record = new JsonObject
        {
            ["$type"] = "app.bsky.feed.post",
            ["text"] = text,
            ["createdAt"] = createdAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };

        if (langs is { Count: > 0 })
            record["langs"] = new JsonArray([.. langs.Take(BlueskyLanguages.Most).Select(l => (JsonNode?)JsonValue.Create(l))]);

        var facets = BlueskyText.Facets(text);
        if (facets.Count > 0)
            record["facets"] = BlueskyText.FacetsJson(facets);

        if (replyTo is not null)
        {
            record["reply"] = new JsonObject
            {
                ["root"] = new JsonObject { ["uri"] = replyTo.Uri, ["cid"] = replyTo.Cid },
                ["parent"] = new JsonObject { ["uri"] = replyTo.Uri, ["cid"] = replyTo.Cid },
            };
        }

        if (video is not null)
        {
            var embed = new JsonObject
            {
                ["$type"] = "app.bsky.embed.video",
                ["video"] = video.Blob.DeepClone(),
                ["alt"] = video.Alt,
            };

            if (video.Width is > 0 && video.Height is > 0)
                embed["aspectRatio"] = new JsonObject { ["width"] = video.Width, ["height"] = video.Height };

            record["embed"] = embed;
        }
        else if (images is { Count: > 0 })
        {
            var entries = new JsonArray();

            foreach (var image in images)
            {
                var entry = new JsonObject
                {
                    ["alt"] = image.Alt,
                    ["image"] = image.Blob.DeepClone(),
                };

                if (image.Width is > 0 && image.Height is > 0)
                    entry["aspectRatio"] = new JsonObject { ["width"] = image.Width, ["height"] = image.Height };

                entries.Add(entry);
            }

            record["embed"] = new JsonObject
            {
                ["$type"] = "app.bsky.embed.images",
                ["images"] = entries,
            };
        }
        else if (card is not null)
        {
            record["embed"] = new JsonObject
            {
                ["$type"] = "app.bsky.embed.external",
                ["external"] = new JsonObject
                {
                    ["uri"] = card.Uri,
                    ["title"] = card.Title,
                    ["description"] = card.Description,
                },
            };
        }

        return record;
    }
}
