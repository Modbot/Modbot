using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Posts;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.Core.Security;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Posts;

/// <summary>
/// Replies, mentions and quotes of the group's Bluesky account into the audit log, against a scripted
/// Bluesky: the first pass starts from then, each one is written once, only those three reasons are
/// asked for, the account's own posts are skipped, a post that is a reply and a mention is one entry,
/// a rate limit stops the pass without a retry until it resets, another failure pauses it for five
/// minutes, both show on Health until a read goes through, and a sign-in without the permission says
/// to sign in again with no call.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PostBlueskyRepliesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Types = [FactType.BlueskyReply, FactType.BlueskyMention, FactType.BlueskyQuote];

    private readonly PostgresFixture _db;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeBluesky _bluesky;
    private readonly BlueskySession _session;

    public PostBlueskyRepliesTests(PostgresFixture db)
    {
        _db = db;
        _bluesky = new FakeBluesky(() => _clock.UtcNow);
        _session = new BlueskySession(Client(), new PlainProtector(), _clock, new BlueskyOAuth(new OneHandlerClients(_bluesky), new PlainProtector(), _clock));
    }

    private BlueskyClient Client() => new(new OneHandlerClients(_bluesky), _clock);

    /// <summary>A checked Bluesky account with an app password, no earlier reading, no entries, one Modbot post on Bluesky.</summary>
    private async Task<Guid> SetUpAsync(Action<Core.Data.Entities.Settings>? change = null)
    {
        await using var context = _db.NewContext();
        await context.Posts.ExecuteDeleteAsync(Ct);
        await context.Events.Where(e => Types.Contains(e.Type)).ExecuteDeleteAsync(Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.PostsPaused = false;
        settings.BlueskyHandle = FakeBluesky.Handle;
        settings.BlueskyDid = FakeBluesky.Did;
        settings.BlueskyServer = FakeBluesky.Server;
        settings.BlueskyAppPasswordEncrypted = FakeBluesky.AppPassword;
        settings.BlueskySessionEncrypted = null;
        settings.BlueskyOAuthSignedIn = false;
        settings.BlueskyCheckedAt = _clock.UtcNow.AddHours(-1);
        settings.BlueskyProblem = null;
        settings.BlueskyPostingOn = true;
        settings.BlueskyStoppedUntil = null;
        settings.BlueskySignInRefused = false;
        settings.BlueskySignedInAt = null;
        settings.BlueskySignInsDay = null;
        settings.BlueskySignInsUsed = 0;
        settings.BlueskyRepliesReadAt = null;
        settings.BlueskyRepliesCheckedAt = null;
        settings.BlueskyRepliesStoppedUntil = null;
        settings.BlueskyRepliesError = null;
        settings.BlueskyRepliesErrorAt = null;
        change?.Invoke(settings);

        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Text = "Friday at eight.",
            Status = PostStatuses.Scheduled,
            SendAt = _clock.UtcNow.AddDays(-1),
            TimeZone = "UTC",
            CreatedAt = _clock.UtcNow.AddDays(-1),
            UpdatedAt = _clock.UtcNow.AddDays(-1),
        };

        post.Destinations.Add(new PostDestination
        {
            Id = Guid.CreateVersion7(),
            PostId = post.Id,
            Network = PostNetworks.Bluesky,
            Target = FakeBluesky.Did,
            State = PostDestinationStates.Posted,
            ClientKey = "ours",
            ExternalId = OurPost,
            PostedAt = _clock.UtcNow.AddDays(-1),
            UpdatedAt = _clock.UtcNow.AddDays(-1),
        });

        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);
        return post.Id;
    }

    private static string OurPost => $"at://{FakeBluesky.Did}/app.bsky.feed.post/ours";

    private async Task<PostBlueskyRepliesPass> PassAsync()
    {
        await using var context = _db.NewContext();
        var pass = new PostBlueskyReplies(
            context,
            _clock,
            new FactWriter(context, _clock),
            new EventPartitionMaintainer(context, _clock),
            _session,
            Client());

        return await pass.RunOnceAsync(Ct);
    }

    private async Task<Core.Data.Entities.Settings> SettingsAsync()
    {
        await using var context = _db.NewContext();
        return await context.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct);
    }

    private async Task<List<ModbotEvent>> FactsAsync()
    {
        await using var context = _db.NewContext();
        return await context.Events.AsNoTracking().Where(e => Types.Contains(e.Type)).OrderBy(e => e.OccurredAt).ToListAsync(Ct);
    }

    private static JsonObject D(ModbotEvent fact) => (JsonObject)JsonNode.Parse(fact.Data)!;

    private int Reads => _bluesky.Requests.Count(r => r.Method == "app.bsky.notification.listNotifications");

    /// <summary>A notification, indexed <paramref name="minutes"/> minutes after the pass-less start of the test.</summary>
    private void Add(string reason, string key, int minutes, string text = "Hello!", string? author = null, string handle = "alice.bsky.social", string? name = "Alice", string? answers = null)
    {
        var record = new JsonObject { ["$type"] = "app.bsky.feed.post", ["text"] = text };

        if (answers is not null)
        {
            record["reply"] = new JsonObject
            {
                ["root"] = new JsonObject { ["uri"] = answers },
                ["parent"] = new JsonObject { ["uri"] = answers },
            };
        }

        _bluesky.Notifications.Add(new JsonObject
        {
            ["uri"] = $"at://{author ?? "did:plc:alice"}/app.bsky.feed.post/{key}",
            ["cid"] = "bafy-" + key,
            ["author"] = new JsonObject { ["did"] = author ?? "did:plc:alice", ["handle"] = handle, ["displayName"] = name },
            ["reason"] = reason,
            ["reasonSubject"] = OurPost,
            ["record"] = record,
            ["isRead"] = false,
            ["indexedAt"] = (new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero) + TimeSpan.FromMinutes(minutes)).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    /// <summary>The first pass, which only marks where reading starts, then <paramref name="minutes"/> minutes on.</summary>
    private async Task StartAsync(int minutes = 5)
    {
        var first = await PassAsync();
        Assert.Equal(new PostBlueskyRepliesPass(0, false), first);
        _clock.Advance(TimeSpan.FromMinutes(minutes));
    }

    [Fact]
    public async Task TheFirstPass_StartsFromNow_AndWritesNothingFromBefore()
    {
        await SetUpAsync();
        Add("reply", "old", -30, answers: OurPost);

        await StartAsync();

        Assert.Equal(0, Reads);
        Assert.Empty(await FactsAsync());
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), (await SettingsAsync()).BlueskyRepliesReadAt);

        // The old one stays unwritten when the next pass reads.
        var pass = await PassAsync();
        Assert.Equal(new PostBlueskyRepliesPass(0, true), pass);
        Assert.Empty(await FactsAsync());
    }

    [Fact]
    public async Task RepliesMentionsAndQuotesAreEachWrittenOnce_WithWhoWhatAndTheLink()
    {
        var postId = await SetUpAsync();
        await StartAsync();

        Add("reply", "r1", 1, "Count me in", answers: OurPost);
        Add("mention", "m1", 2, "@ourgroup see you", handle: "bob.example.com", name: null, author: "did:plc:bob");
        Add("quote", "q1", 3, "Worth a look");
        Add("like", "l1", 4);
        Add("follow", "f1", 4);

        var pass = await PassAsync();
        Assert.Equal(new PostBlueskyRepliesPass(3, true), pass);

        var facts = await FactsAsync();
        Assert.Equal([FactType.BlueskyReply, FactType.BlueskyMention, FactType.BlueskyQuote], facts.Select(f => f.Type));

        var reply = D(facts[0]);
        Assert.Equal("alice.bsky.social", reply["handle"]!.GetValue<string>());
        Assert.Equal("Alice", reply["displayName"]!.GetValue<string>());
        Assert.Equal("Count me in", reply["text"]!.GetValue<string>());
        Assert.Equal("https://bsky.app/profile/alice.bsky.social/post/r1", reply["link"]!.GetValue<string>());
        Assert.Equal(postId.ToString(), reply["postId"]!.GetValue<string>());
        Assert.Equal("Movie night", reply["title"]!.GetValue<string>());
        Assert.Equal("at://did:plc:alice/app.bsky.feed.post/r1", facts[0].SubjectId);

        var mention = D(facts[1]);
        Assert.Equal("bob.example.com", mention["handle"]!.GetValue<string>());
        Assert.Null(mention["displayName"]);
        Assert.Null(mention["postId"]);

        // Asked for the three reasons and no others.
        var asked = _bluesky.Requests.Single(r => r.Method == "app.bsky.notification.listNotifications").Uri.Query;
        Assert.Contains("reasons=reply", asked);
        Assert.Contains("reasons=mention", asked);
        Assert.Contains("reasons=quote", asked);
        Assert.DoesNotContain("like", asked);

        // Read again later: the same ones are not written twice, and a new one is.
        _clock.Advance(PostBlueskyReplies.Every);
        Assert.Equal(new PostBlueskyRepliesPass(0, true), await PassAsync());

        Add("reply", "r2", 10, "One more", answers: OurPost);
        _clock.Advance(PostBlueskyReplies.Every);
        Assert.Equal(new PostBlueskyRepliesPass(1, true), await PassAsync());
        Assert.Equal(4, (await FactsAsync()).Count);
    }

    [Fact]
    public async Task TheGroupsOwnAccount_IsSkipped_ButStillMovesTheReadingOn()
    {
        await SetUpAsync();
        await StartAsync();

        Add("reply", "mine", 1, "Thread continues", author: FakeBluesky.Did, handle: FakeBluesky.Handle, name: "Our group", answers: OurPost);

        Assert.Equal(new PostBlueskyRepliesPass(0, true), await PassAsync());
        Assert.Empty(await FactsAsync());
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 12, 1, 0, TimeSpan.Zero), (await SettingsAsync()).BlueskyRepliesReadAt);
    }

    [Fact]
    public async Task APostThatIsAReplyAndAMention_IsOneEntry_AsTheReply()
    {
        await SetUpAsync();
        await StartAsync();

        Add("mention", "both", 1, "@ourgroup yes", answers: OurPost);
        Add("reply", "both", 1, "@ourgroup yes", answers: OurPost);

        Assert.Equal(new PostBlueskyRepliesPass(1, true), await PassAsync());
        Assert.Equal(FactType.BlueskyReply, Assert.Single(await FactsAsync()).Type);
    }

    [Fact]
    public async Task ReadsWaitAtLeastTheGap()
    {
        await SetUpAsync();
        await StartAsync(minutes: 1);

        Assert.Equal(new PostBlueskyRepliesPass(0, false), await PassAsync());
        Assert.Equal(0, Reads);

        _clock.Advance(PostBlueskyReplies.Every);
        Assert.True((await PassAsync()).Asked);
        Assert.Equal(1, Reads);
    }

    [Fact]
    public async Task MoreThanOnePage_IsRead_NewestFirst_UpToThePageLimit()
    {
        await SetUpAsync();
        await StartAsync();
        _bluesky.NotificationPageSize = 2;

        for (var i = 1; i <= 8; i++)
            Add("mention", "n" + i, i, "Number " + i);

        var pass = await PassAsync();

        // Three pages of two: the six newest, written oldest first.
        Assert.Equal(PostBlueskyReplies.MostPages, Reads);
        Assert.Equal(new PostBlueskyRepliesPass(6, true), pass);
        Assert.Equal(["Number 3", "Number 4", "Number 5", "Number 6", "Number 7", "Number 8"], (await FactsAsync()).Select(f => D(f)["text"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ARateLimit_StopsThePassUntilItResets_IsNotRetried_AndShowsOnHealth()
    {
        await SetUpAsync();
        await StartAsync();

        // One read that goes through first, so the session is saved and only the read is limited after.
        _clock.Advance(PostBlueskyReplies.Every);
        Assert.True((await PassAsync()).Asked);

        var resets = _clock.UtcNow.AddHours(1);
        _bluesky.LimitedUntil = resets;
        _clock.Advance(PostBlueskyReplies.Every);
        var before = Reads;

        Assert.Equal(new PostBlueskyRepliesPass(0, true), await PassAsync());
        Assert.Equal(before + 1, Reads);

        var settings = await SettingsAsync();
        Assert.Equal(BlueskyErrors.Limited, settings.BlueskyRepliesError);
        Assert.Equal(resets, settings.BlueskyRepliesStoppedUntil);
        Assert.Null(settings.BlueskyStoppedUntil);

        // Not asked again before the reset.
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(new PostBlueskyRepliesPass(0, false), await PassAsync());
        Assert.Equal(before + 1, Reads);

        // After it, a read that goes through clears the error.
        _bluesky.LimitedUntil = null;
        _clock.Advance(TimeSpan.FromMinutes(31));
        Assert.True((await PassAsync()).Asked);
        Assert.Null((await SettingsAsync()).BlueskyRepliesError);
    }

    [Fact]
    public async Task AnotherFailure_PausesThePassFiveMinutes_AndShowsOnHealthUntilAReadGoesThrough()
    {
        await SetUpAsync();
        await StartAsync();
        _bluesky.NotificationsStatus = HttpStatusCode.BadGateway;

        Assert.Equal(new PostBlueskyRepliesPass(0, true), await PassAsync());

        var settings = await SettingsAsync();
        Assert.Equal(_clock.UtcNow + PostBlueskyReplies.FailurePause, settings.BlueskyRepliesStoppedUntil);
        Assert.NotNull(settings.BlueskyRepliesError);
        Assert.Equal(_clock.UtcNow, settings.BlueskyRepliesErrorAt);
        Assert.Equal(1, Reads);

        // Inside the pause nothing is asked.
        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(new PostBlueskyRepliesPass(0, false), await PassAsync());
        Assert.Equal(1, Reads);

        // After it, a read that goes through clears the error.
        _bluesky.NotificationsStatus = null;
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True((await PassAsync()).Asked);
        Assert.Null((await SettingsAsync()).BlueskyRepliesError);
    }

    [Fact]
    public async Task AFailureBeforeAnythingIsWritten_LeavesTheMarkWhereItWas_SoNothingIsLost()
    {
        await SetUpAsync();
        await StartAsync();
        Add("reply", "r1", 1, answers: OurPost);
        _bluesky.NotificationsStatus = HttpStatusCode.BadGateway;

        await PassAsync();
        Assert.Empty(await FactsAsync());

        _bluesky.NotificationsStatus = null;
        _clock.Advance(PostBlueskyReplies.FailurePause + TimeSpan.FromMinutes(1));
        Assert.Equal(new PostBlueskyRepliesPass(1, true), await PassAsync());
    }

    [Fact]
    public async Task ASignInWithoutThePermission_SaysToSignInAgain_WithNoCall()
    {
        await SetUpAsync(s =>
        {
            s.BlueskyAppPasswordEncrypted = null;
            s.BlueskyOAuthSignedIn = true;
            s.BlueskySessionEncrypted = _session.Protect(new BlueskyTokens(
                "access", "refresh", FakeBluesky.Did, FakeBluesky.Handle,
                new BlueskyOAuthGrant(
                    FakeBluesky.AuthServer, FakeBluesky.TokenEndpoint, "https://modbot.example.com/client.json", "{}", _clock.UtcNow.AddHours(1),
                    "atproto repo:app.bsky.feed.post?action=create&action=update&action=delete repo:app.bsky.actor.profile?action=create&action=update blob:image/*")));
        });

        Assert.Equal(new PostBlueskyRepliesPass(0, false), await PassAsync());

        Assert.Empty(_bluesky.Requests);
        Assert.Equal(PostBlueskyReplies.SignInAgain, (await SettingsAsync()).BlueskyRepliesError);
    }

    [Fact]
    public async Task WhileTheAccountsOwnLaneIsStopped_NothingIsAsked()
    {
        await SetUpAsync(s => s.BlueskyStoppedUntil = _clock.UtcNow.AddHours(1));
        await StartAsync();

        _clock.Advance(PostBlueskyReplies.Every);
        Assert.Equal(new PostBlueskyRepliesPass(0, false), await PassAsync());
        Assert.Empty(_bluesky.Requests);
    }

    [Fact]
    public async Task WithoutAnAccountThatPassedCheck_NothingHappens()
    {
        await SetUpAsync(s => s.BlueskyCheckedAt = null);

        Assert.Equal(new PostBlueskyRepliesPass(0, false), await PassAsync());
        Assert.Null((await SettingsAsync()).BlueskyRepliesReadAt);
    }

    /// <summary>The app password and session are stored as they are: the protector's own tests cover the encryption.</summary>
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string? Unprotect(string? ciphertext) => ciphertext;
    }
}
