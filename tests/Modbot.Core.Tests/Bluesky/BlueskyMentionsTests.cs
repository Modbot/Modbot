using System.Text.Json.Nodes;
using Modbot.Core.Bluesky;

namespace Modbot.Core.Tests.Bluesky;

/// <summary>
/// The account's replies, mentions and quotes, read from <c>app.bsky.notification.listNotifications</c>:
/// only the three reasons, a reply carrying the post it answers, a notification missing its address,
/// author or time skipped, a handle Bluesky could not confirm kept as <c>handle.invalid</c>, and the cursor.
/// </summary>
public class BlueskyMentionsTests
{
    [Fact]
    public void RepliesMentionsAndQuotesAreRead_AndOtherReasonsSkipped()
    {
        var body = JsonNode.Parse("""
            {"cursor":"next-page","notifications":[
              {"uri":"at://did:plc:a/app.bsky.feed.post/1","reason":"reply","reasonSubject":"at://did:plc:me/app.bsky.feed.post/9",
               "author":{"did":"did:plc:a","handle":"ann.bsky.social","displayName":"  Ann  "},
               "record":{"text":"Thanks!","reply":{"parent":{"uri":"at://did:plc:me/app.bsky.feed.post/8"}}},
               "indexedAt":"2026-10-14T10:00:00.000Z"},
              {"uri":"at://did:plc:b/app.bsky.feed.post/2","reason":"mention",
               "author":{"did":"did:plc:b","handle":"bob.bsky.social"},
               "record":{"text":"@group look"},"indexedAt":"2026-10-14T09:00:00.000Z"},
              {"uri":"at://did:plc:c/app.bsky.feed.post/3","reason":"quote",
               "author":{"did":"did:plc:c","handle":"cy.bsky.social","displayName":""},
               "record":{"text":"quoting"},"indexedAt":"2026-10-14T08:00:00.000Z"},
              {"uri":"at://did:plc:d/app.bsky.feed.like/4","reason":"like",
               "author":{"did":"did:plc:d","handle":"di.bsky.social"},"indexedAt":"2026-10-14T07:00:00.000Z"},
              {"uri":"at://did:plc:e/app.bsky.graph.follow/5","reason":"follow",
               "author":{"did":"did:plc:e","handle":"ed.bsky.social"},"indexedAt":"2026-10-14T06:00:00.000Z"}
            ]}
            """)!.AsObject();

        var page = BlueskyClient.MentionsOf(body);

        Assert.Equal(["reply", "mention", "quote"], page.Mentions.Select(m => m.Reason));
        Assert.Equal("next-page", page.Cursor);

        var reply = page.Mentions[0];
        Assert.Equal("at://did:plc:a/app.bsky.feed.post/1", reply.Uri);
        Assert.Equal("did:plc:a", reply.AuthorDid);
        Assert.Equal("ann.bsky.social", reply.AuthorHandle);
        Assert.Equal("Ann", reply.AuthorName);
        Assert.Equal("Thanks!", reply.Text);
        Assert.Equal("at://did:plc:me/app.bsky.feed.post/8", reply.Answered);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero), reply.IndexedAt);

        Assert.Null(page.Mentions[1].Answered);
        Assert.Null(page.Mentions[1].AuthorName);
        Assert.Null(page.Mentions[2].AuthorName);
    }

    [Fact]
    public void AReplyWithoutAParentFallsBackToTheNotificationsSubject()
    {
        var body = JsonNode.Parse("""
            {"notifications":[
              {"uri":"at://did:plc:a/app.bsky.feed.post/1","reason":"reply","reasonSubject":"at://did:plc:me/app.bsky.feed.post/9",
               "author":{"did":"did:plc:a","handle":"ann.bsky.social"},"record":{"text":"hi"},"indexedAt":"2026-10-14T10:00:00Z"}
            ]}
            """)!.AsObject();

        var page = BlueskyClient.MentionsOf(body);

        Assert.Equal("at://did:plc:me/app.bsky.feed.post/9", Assert.Single(page.Mentions).Answered);
        Assert.Null(page.Cursor);
    }

    [Fact]
    public void ANotificationMissingItsAddressAuthorOrTimeIsSkipped_AndMissingTextIsEmpty()
    {
        var body = JsonNode.Parse("""
            {"notifications":[
              {"reason":"mention","author":{"did":"did:plc:a","handle":"a"},"indexedAt":"2026-10-14T10:00:00Z"},
              {"uri":"at://x/1","reason":"mention","indexedAt":"2026-10-14T10:00:00Z"},
              {"uri":"at://x/2","reason":"mention","author":{"handle":"a"},"indexedAt":"2026-10-14T10:00:00Z"},
              {"uri":"at://x/3","reason":"mention","author":{"did":"did:plc:a","handle":"a"}},
              {"uri":"at://x/4","reason":"mention","author":{"did":"did:plc:a","handle":"a"},"indexedAt":"not a time"},
              {"uri":"at://x/5","reason":"mention","author":{"did":"did:plc:a","handle":"a"},"indexedAt":"2026-10-14T10:00:00Z"}
            ]}
            """)!.AsObject();

        var page = BlueskyClient.MentionsOf(body);

        var kept = Assert.Single(page.Mentions);
        Assert.Equal("at://x/5", kept.Uri);
        Assert.Equal(string.Empty, kept.Text);
    }

    [Fact]
    public void AHandleBlueskyCouldNotConfirmIsKeptAsItCame()
    {
        var body = JsonNode.Parse("""
            {"notifications":[
              {"uri":"at://x/1","reason":"quote","author":{"did":"did:plc:a","handle":"handle.invalid"},
               "record":{"text":"q"},"indexedAt":"2026-10-14T10:00:00Z"}
            ]}
            """)!.AsObject();

        Assert.Equal("handle.invalid", Assert.Single(BlueskyClient.MentionsOf(body).Mentions).AuthorHandle);
    }

    [Fact]
    public void AnAnswerWithNoNotificationsIsEmpty_AndAnEmptyCursorIsNone()
    {
        Assert.Empty(BlueskyClient.MentionsOf(new JsonObject()).Mentions);
        Assert.Empty(BlueskyClient.MentionsOf(null).Mentions);
        Assert.Null(BlueskyClient.MentionsOf(JsonNode.Parse("""{"notifications":[],"cursor":""}""")!.AsObject()).Cursor);
    }
}
