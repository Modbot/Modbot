using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Twitch;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.Core.Security;
using Modbot.Core.Twitch;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Twitch;

/// <summary>
/// The Twitch poll (Twitch design, steps 1 to 3) against a scripted Twitch: one row per stream, the
/// facts the Live and Now cards redraw on, the "We're live on Twitch" post made once per stream
/// under the rules the user chose (live only, after a few minutes, one in six hours, nothing ticked
/// to start), the event a stream is linked to, and a 429 that stops every call and is never sent
/// again.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class TwitchLivePassTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 7, 19, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;
    private readonly FakeClock _clock = new(Start);
    private readonly FakeTwitch _twitch = new();
    private readonly TwitchClient _client;
    private readonly TwitchSignIn _signIn;

    public TwitchLivePassTests(PostgresFixture db)
    {
        _db = db;
        _client = new TwitchClient(new OneHandlerClients(_twitch));
        _signIn = new TwitchSignIn(_client, _clock);
    }

    private static string NewId() => "s" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Twitch set up, checked and polled, with no streams, no posts and nothing ticked.</summary>
    private async Task SetUpAsync(Action<Core.Data.Entities.Settings>? change = null)
    {
        await using var context = _db.NewContext();
        await context.Posts.Where(p => p.Kind == PostKinds.TwitchLive).ExecuteDeleteAsync(Ct);
        await context.TwitchStreams.ExecuteDeleteAsync(Ct);
        await context.CalendarDateChanges.ExecuteDeleteAsync(Ct);
        await context.CalendarEventPlaces.ExecuteDeleteAsync(Ct);
        await context.CalendarEvents.ExecuteDeleteAsync(Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.PostsPaused = false;
        settings.TwitchClientId = FakeTwitch.ClientId;
        settings.TwitchClientSecretEncrypted = FakeTwitch.ClientSecret;
        settings.TwitchChannelLogin = FakeTwitch.Login;
        settings.TwitchChannelId = FakeTwitch.ChannelId;
        settings.TwitchChannelName = FakeTwitch.DisplayName;
        settings.TwitchCheckedAt = Start.AddHours(-1);
        settings.TwitchProblem = null;
        settings.TwitchStoppedUntil = null;
        settings.TwitchLiveOn = true;
        settings.TwitchPolledAt = null;
        settings.TwitchPollProblem = null;
        settings.TwitchPostAfterMinutes = 3;
        settings.TwitchPostEveryHours = 6;
        settings.TwitchPostTitle = null;
        settings.TwitchPostText = null;
        settings.TwitchPostPlaces = "{}";
        settings.ManagedGroupId = "grp_00000000-0000-0000-0000-000000000001";
        change?.Invoke(settings);

        await context.SaveChangesAsync(Ct);
    }

    private static void TickVRChat(Core.Data.Entities.Settings settings) =>
        settings.TwitchPostPlaces = new TwitchPostPlaces(VRChat: new TwitchVRChatPlace()).Write();

    private async Task<TwitchLivePassResult> PassAsync()
    {
        await using var context = _db.NewContext();
        var pass = new TwitchLivePass(
            context,
            _clock,
            new PlainProtector(),
            _signIn,
            _client,
            new AccountFacts(new FactWriter(context, _clock), new EventPartitionMaintainer(context, _clock), _clock));

        return await pass.RunOnceAsync(Ct);
    }

    private FakeTwitch.FakeStream GoLive(string? id = null, int startedMinutesAgo = 0, string type = "live", int viewers = 12, string title = "Movie night in VRChat")
    {
        var stream = new FakeTwitch.FakeStream(id ?? NewId(), _clock.UtcNow.AddMinutes(-startedMinutesAgo), title, "VRChat", viewers, type);
        _twitch.Live = stream;
        return stream;
    }

    private async Task<TwitchStream> StreamAsync(string id)
    {
        await using var context = _db.NewContext();
        return await context.TwitchStreams.AsNoTracking().SingleAsync(s => s.Id == id, Ct);
    }

    private async Task<List<Post>> LivePostsAsync()
    {
        await using var context = _db.NewContext();
        return await context.Posts.AsNoTracking().Include(p => p.Destinations)
            .Where(p => p.Kind == PostKinds.TwitchLive)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(Ct);
    }

    private async Task<int> FactsAsync(string type, string streamId)
    {
        await using var context = _db.NewContext();
        return await context.Events.CountAsync(e => e.Type == type && e.SubjectId == streamId, Ct);
    }

    // ── Step 1: seeing the channel live ───────────────────────────────────────────────────

    [Fact]
    public async Task NothingIsAskedWhileThePollIsOff()
    {
        await SetUpAsync(s => s.TwitchLiveOn = false);
        GoLive();

        var pass = await PassAsync();

        Assert.False(pass.Polled);
        Assert.Empty(_twitch.Requests);
    }

    [Fact]
    public async Task NothingIsAskedBeforeACheckThatPassed()
    {
        await SetUpAsync(s => s.TwitchCheckedAt = null);
        GoLive();

        await PassAsync();

        Assert.Empty(_twitch.Requests);
    }

    [Fact]
    public async Task AChannelThatIsNotLiveMakesNoRow()
    {
        await SetUpAsync();

        var pass = await PassAsync();

        Assert.True(pass.Polled);
        Assert.False(pass.Live);

        await using var context = _db.NewContext();
        Assert.Empty(await context.TwitchStreams.ToListAsync(Ct));
        Assert.NotNull((await context.GetSettingsAsync(Ct)).TwitchPolledAt);
    }

    [Fact]
    public async Task AStreamSeenLiveIsOneRow_AndTheLiveCardsAreToldOnce()
    {
        await SetUpAsync();
        var stream = GoLive(startedMinutesAgo: 1);

        var pass = await PassAsync();

        Assert.True(pass.Live);
        var row = await StreamAsync(stream.Id);
        Assert.Equal("Movie night in VRChat", row.Title);
        Assert.Equal("VRChat", row.Category);
        Assert.Equal(12, row.Viewers);
        Assert.Equal(stream.StartedAt, row.StartedAt);
        Assert.Null(row.EndedAt);
        Assert.Equal(1, await FactsAsync(FactType.TwitchOnline, stream.Id));

        // The next minute is the same stream: no second row, no second "went live".
        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();

        await using var context = _db.NewContext();
        Assert.Equal(1, await context.TwitchStreams.CountAsync(s => s.Id == stream.Id, Ct));
        Assert.Equal(1, await FactsAsync(FactType.TwitchOnline, stream.Id));
    }

    [Fact]
    public async Task OneTokenIsAskedFor_AndKeptAcrossMinutes()
    {
        await SetUpAsync();
        GoLive();

        await PassAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();

        Assert.Equal(1, _twitch.TokensMade);
        Assert.Equal(3, _twitch.Requests.Count(r => r.Uri.AbsolutePath == "/helix/streams"));
        Assert.All(_twitch.Requests.Where(r => r.Uri.AbsolutePath == "/helix/streams"), r =>
            Assert.Equal(FakeTwitch.ChannelId, System.Web.HttpUtility.ParseQueryString(r.Uri.Query)["user_id"]));
    }

    [Fact]
    public async Task ARefusedTokenIsAskedForAgainOnce()
    {
        await SetUpAsync();
        GoLive();
        _twitch.RefuseTokenOnce = true;

        var pass = await PassAsync();

        Assert.True(pass.Polled);
        Assert.True(pass.Live);
        Assert.Equal(2, _twitch.TokensMade);
    }

    [Fact]
    public async Task ATitleOrCategoryChangeIsToldAtOnce_AViewerCountAtMostEveryFiveMinutes()
    {
        await SetUpAsync();
        var stream = GoLive(viewers: 10);
        await PassAsync();

        // Viewers move a minute later: not worth a fact yet.
        _clock.Advance(TimeSpan.FromMinutes(1));
        _twitch.Live = stream with { Viewers = 15 };
        await PassAsync();
        Assert.Equal(0, await FactsAsync(FactType.TwitchUpdated, stream.Id));
        Assert.Equal(15, (await StreamAsync(stream.Id)).Viewers);

        // A new title is told at once.
        _clock.Advance(TimeSpan.FromMinutes(1));
        _twitch.Live = stream with { Viewers = 15, Title = "Now with cake" };
        await PassAsync();
        Assert.Equal(1, await FactsAsync(FactType.TwitchUpdated, stream.Id));

        // Five minutes on, a moved viewer count is told.
        _clock.Advance(TimeSpan.FromMinutes(5));
        _twitch.Live = stream with { Viewers = 30, Title = "Now with cake" };
        await PassAsync();
        Assert.Equal(2, await FactsAsync(FactType.TwitchUpdated, stream.Id));

        var row = await StreamAsync(stream.Id);
        Assert.Equal(30, row.PeakViewers);
        Assert.Equal("Now with cake", row.Title);
    }

    [Fact]
    public async Task WhenTheChannelGoesOfflineTheStreamEnds()
    {
        await SetUpAsync();
        var stream = GoLive(viewers: 40);
        await PassAsync();

        _clock.Advance(TimeSpan.FromMinutes(30));
        _twitch.Live = null;
        var pass = await PassAsync();

        Assert.False(pass.Live);
        var row = await StreamAsync(stream.Id);
        Assert.Equal(_clock.UtcNow, row.EndedAt);
        Assert.Equal(1, await FactsAsync(FactType.TwitchOffline, stream.Id));

        // Offline stays one fact.
        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();
        Assert.Equal(1, await FactsAsync(FactType.TwitchOffline, stream.Id));
    }

    [Fact]
    public async Task ATwitchThatDoesNotAnswerIsSaid_AndMakesNoRow()
    {
        await SetUpAsync();
        GoLive();
        _twitch.ApiStatus = HttpStatusCode.InternalServerError;

        var pass = await PassAsync();

        Assert.False(pass.Polled);
        Assert.Equal("Twitch did not answer.", pass.Problem);

        await using var context = _db.NewContext();
        Assert.Empty(await context.TwitchStreams.ToListAsync(Ct));
        Assert.Equal("Twitch did not answer.", (await context.GetSettingsAsync(Ct)).TwitchPollProblem);

        // The next good answer clears it.
        _twitch.ApiStatus = null;
        await PassAsync();
        await using var after = _db.NewContext();
        Assert.Null((await after.GetSettingsAsync(Ct)).TwitchPollProblem);
    }

    /// <summary>CLAUDE.md: never retry a 429. A limit stops every call until Twitch's reset.</summary>
    [Fact]
    public async Task ALimitStopsThePollUntilTwitchsReset_AndIsNotSentAgain()
    {
        await SetUpAsync();
        GoLive();
        _twitch.ApiStatus = HttpStatusCode.TooManyRequests;
        _twitch.ResetAt = _clock.UtcNow.AddMinutes(6).ToUnixTimeSeconds();

        var limited = await PassAsync();

        Assert.False(limited.Polled);
        Assert.Equal(1, _twitch.LimitedRequests);

        await using (var context = _db.NewContext())
            Assert.Equal(_clock.UtcNow.AddMinutes(6), (await context.GetSettingsAsync(Ct)).TwitchStoppedUntil);

        var sent = _twitch.Requests.Count;

        // Every minute inside the stop sends nothing at all.
        for (var minute = 0; minute < 5; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            await PassAsync();
        }

        Assert.Equal(sent, _twitch.Requests.Count);
        Assert.Equal(1, _twitch.LimitedRequests);

        // Once it has passed, the poll goes again.
        _twitch.ApiStatus = null;
        _clock.Advance(TimeSpan.FromMinutes(2));
        var again = await PassAsync();

        Assert.True(again.Polled);
        Assert.True(again.Live);
    }

    [Fact]
    public async Task ALimitWithNoResetStopsForFifteenMinutes()
    {
        await SetUpAsync();
        _twitch.ApiStatus = HttpStatusCode.TooManyRequests;

        await PassAsync();

        await using var context = _db.NewContext();
        Assert.Equal(_clock.UtcNow.AddMinutes(15), (await context.GetSettingsAsync(Ct)).TwitchStoppedUntil);
    }

    // ── Step 3: the event the stream was for ──────────────────────────────────────────────

    private async Task<Guid> AddEventAsync(DateTimeOffset starts, TimeSpan length, string title = "Movie night")
    {
        await using var context = _db.NewContext();
        var calendarEvent = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            StartsAt = starts,
            EndsAt = starts + length,
            TimeZone = "UTC",
            State = CalendarEventStates.Scheduled,
            CreatedAt = Start,
            UpdatedAt = Start,
        };

        context.CalendarEvents.Add(calendarEvent);
        await context.SaveChangesAsync(Ct);
        return calendarEvent.Id;
    }

    [Fact]
    public async Task AStreamIsLinkedToTheEventThatWasOn()
    {
        await SetUpAsync();
        var running = await AddEventAsync(Start.AddMinutes(-30), TimeSpan.FromHours(2));
        await AddEventAsync(Start.AddDays(3), TimeSpan.FromHours(2), "Next week");
        var stream = GoLive();

        await PassAsync();

        Assert.Equal(running, (await StreamAsync(stream.Id)).EventId);
    }

    [Fact]
    public async Task OverlappingEventsLeaveTheStreamUnlinked()
    {
        await SetUpAsync();
        await AddEventAsync(Start.AddMinutes(-30), TimeSpan.FromHours(2), "Movie night");
        await AddEventAsync(Start, TimeSpan.FromHours(1), "Dance party");
        var stream = GoLive();

        await PassAsync();

        Assert.Null((await StreamAsync(stream.Id)).EventId);
    }

    [Fact]
    public async Task AStreamWithNoEventOnIsLinkedToNothing()
    {
        await SetUpAsync();
        await AddEventAsync(Start.AddDays(-2), TimeSpan.FromHours(2));
        var stream = GoLive();

        await PassAsync();

        Assert.Null((await StreamAsync(stream.Id)).EventId);
    }

    [Fact]
    public async Task AnEventMadeAfterTheStreamBeganIsLinkedWhileNobodyHasChosen()
    {
        await SetUpAsync();
        var stream = GoLive();
        await PassAsync();
        Assert.Null((await StreamAsync(stream.Id)).EventId);

        var running = await AddEventAsync(Start.AddMinutes(-30), TimeSpan.FromHours(2));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();

        Assert.Equal(running, (await StreamAsync(stream.Id)).EventId);
        Assert.Equal(1, await FactsAsync(FactType.TwitchUpdated, stream.Id));
    }

    [Fact]
    public async Task APersonsLinkIsNotChangedByTheNextMinute()
    {
        await SetUpAsync();
        var running = await AddEventAsync(Start.AddMinutes(-30), TimeSpan.FromHours(2));
        var stream = GoLive();
        await PassAsync();

        await using (var context = _db.NewContext())
        {
            var row = await context.TwitchStreams.SingleAsync(s => s.Id == stream.Id, Ct);
            row.EventId = null;
            row.EventSetByStaff = true;
            await context.SaveChangesAsync(Ct);
        }

        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();

        var after = await StreamAsync(stream.Id);
        Assert.Null(after.EventId);
        Assert.True(after.EventSetByStaff);
        Assert.NotEqual(Guid.Empty, running);
    }

    // ── Step 2: the "We're live on Twitch" post ───────────────────────────────────────────

    [Fact]
    public async Task NothingIsPostedWhileNoSiteIsTicked()
    {
        await SetUpAsync();
        var stream = GoLive(startedMinutesAgo: 5);

        await PassAsync();

        Assert.Empty(await LivePostsAsync());

        // Decided once: ticking a site now does not post the stream that is already going.
        await using (var context = _db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            TickVRChat(settings);
            await context.SaveChangesAsync(Ct);
        }

        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();

        Assert.Empty(await LivePostsAsync());
        Assert.NotNull((await StreamAsync(stream.Id)).PostDecidedAt);
    }

    [Fact]
    public async Task ThePostWaitsUntilTheStreamHasBeenLiveLongEnough()
    {
        await SetUpAsync(TickVRChat);
        var stream = GoLive(startedMinutesAgo: 0);

        await PassAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();
        Assert.Empty(await LivePostsAsync());
        Assert.Null((await StreamAsync(stream.Id)).PostDecidedAt);

        // Three minutes in.
        _clock.Advance(TimeSpan.FromMinutes(1));
        var pass = await PassAsync();

        Assert.True(pass.MadePost);
        Assert.Single(await LivePostsAsync());
    }

    [Fact]
    public async Task ThePostIsAnOrdinaryPost_ForTheTickedSitesOnly_UnderTheStreamId()
    {
        await SetUpAsync(TickVRChat);
        var stream = GoLive(startedMinutesAgo: 4, title: "Movie night in VRChat");

        await PassAsync();

        var post = Assert.Single(await LivePostsAsync());
        Assert.Equal(PostKinds.TwitchLive, post.Kind);
        Assert.Equal(stream.Id, post.ExternalKey);
        Assert.Equal(PostStatuses.Scheduled, post.Status);
        Assert.Equal(_clock.UtcNow, post.SendAt);
        Assert.Null(post.CreatedByUserId);
        Assert.Null(post.EventId);
        Assert.Equal("We're live on Twitch", post.Title);
        Assert.Equal("Movie night in VRChat\nhttps://www.twitch.tv/ourgroup", post.Text);

        var destination = Assert.Single(post.Destinations);
        Assert.Equal(PostNetworks.VRChat, destination.Network);
        Assert.Equal("grp_00000000-0000-0000-0000-000000000001", destination.Target);
        Assert.Equal(PostDestinationStates.Waiting, destination.State);

        Assert.Equal(post.Id, (await StreamAsync(stream.Id)).PostId);
        await using var context = _db.NewContext();
        Assert.True(await context.Events.AnyAsync(e => e.Type == FactType.PostCreated && e.SubjectId == post.Id.ToString(), Ct));
    }

    [Fact]
    public async Task TheOperatorsTemplateIsFilledWithTheStreamsWords()
    {
        await SetUpAsync(s =>
        {
            TickVRChat(s);
            s.TwitchPostTitle = "Watch {category} now";
            s.TwitchPostText = "{title} is on: {link} ({category})";
        });
        GoLive(startedMinutesAgo: 4, title: "Movie night");

        await PassAsync();

        var post = Assert.Single(await LivePostsAsync());
        Assert.Equal("Watch VRChat now", post.Title);
        Assert.Equal("Movie night is on: https://www.twitch.tv/ourgroup (VRChat)", post.Text);
    }

    [Fact]
    public async Task OneStreamMakesOnePost_AcrossMinutesRestartsAndDropouts()
    {
        await SetUpAsync(TickVRChat);
        var stream = GoLive(startedMinutesAgo: 4);
        await PassAsync();
        Assert.Single(await LivePostsAsync());

        // The next minutes, and a "restart": a pass built from nothing.
        for (var minute = 0; minute < 3; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            await PassAsync();
        }

        // A dropout: offline for a minute, then live again under the same Twitch id.
        _twitch.Live = null;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();
        _twitch.Live = stream;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();

        Assert.Single(await LivePostsAsync());

        await using var context = _db.NewContext();
        Assert.Equal(1, await context.TwitchStreams.CountAsync(s => s.Id == stream.Id, Ct));
    }

    [Fact]
    public async Task TheDatabaseItselfRefusesASecondPostForOneStream()
    {
        await SetUpAsync(TickVRChat);
        var stream = GoLive(startedMinutesAgo: 4);
        await PassAsync();

        await using var context = _db.NewContext();
        context.Posts.Add(new Post
        {
            Id = Guid.CreateVersion7(),
            Text = "Again",
            Status = PostStatuses.Scheduled,
            SendAt = _clock.UtcNow,
            TimeZone = "UTC",
            Kind = PostKinds.TwitchLive,
            ExternalKey = stream.Id,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task ASecondStreamInsideTheCoolDownDoesNotPost_OneAfterItDoes()
    {
        await SetUpAsync(TickVRChat);
        GoLive(startedMinutesAgo: 4);
        await PassAsync();
        Assert.Single(await LivePostsAsync());

        // It ends, and an hour later the channel is live again under a new Twitch id.
        _twitch.Live = null;
        _clock.Advance(TimeSpan.FromMinutes(10));
        await PassAsync();

        _clock.Advance(TimeSpan.FromHours(1));
        var second = GoLive(startedMinutesAgo: 4);
        await PassAsync();

        Assert.Single(await LivePostsAsync());
        Assert.NotNull((await StreamAsync(second.Id)).PostDecidedAt);
        Assert.Null((await StreamAsync(second.Id)).PostId);

        // Six hours after the first post, the next stream posts.
        _twitch.Live = null;
        _clock.Advance(TimeSpan.FromMinutes(10));
        await PassAsync();

        _clock.Advance(TimeSpan.FromHours(6));
        GoLive(startedMinutesAgo: 4);
        await PassAsync();

        Assert.Equal(2, (await LivePostsAsync()).Count);
    }

    [Fact]
    public async Task TheCoolDownIsTheOperatorsToChange()
    {
        await SetUpAsync(s =>
        {
            TickVRChat(s);
            s.TwitchPostEveryHours = 1;
        });
        GoLive(startedMinutesAgo: 4);
        await PassAsync();

        _twitch.Live = null;
        _clock.Advance(TimeSpan.FromMinutes(10));
        await PassAsync();

        _clock.Advance(TimeSpan.FromHours(1));
        GoLive(startedMinutesAgo: 4);
        await PassAsync();

        Assert.Equal(2, (await LivePostsAsync()).Count);
    }

    [Fact]
    public async Task TheMinimumTimeIsTheOperatorsToChange()
    {
        await SetUpAsync(s =>
        {
            TickVRChat(s);
            s.TwitchPostAfterMinutes = 10;
        });
        GoLive(startedMinutesAgo: 5);

        await PassAsync();
        Assert.Empty(await LivePostsAsync());

        _clock.Advance(TimeSpan.FromMinutes(5));
        await PassAsync();
        Assert.Single(await LivePostsAsync());
    }

    [Theory]
    [InlineData("rerun")]
    [InlineData("playlist")]
    [InlineData("watch_party")]
    [InlineData("premiere")]
    public async Task OnlyALiveStreamPosts(string type)
    {
        await SetUpAsync(TickVRChat);
        var stream = GoLive(startedMinutesAgo: 5, type: type);

        await PassAsync();

        Assert.Empty(await LivePostsAsync());
        Assert.NotNull((await StreamAsync(stream.Id)).PostDecidedAt);
    }

    [Fact]
    public async Task AStreamFirstSeenLongAfterItStartedDoesNotPost()
    {
        await SetUpAsync(TickVRChat);
        // The poll was switched on 45 minutes into a stream: "we're live" would be old news.
        var stream = GoLive(startedMinutesAgo: 45);

        await PassAsync();

        Assert.Empty(await LivePostsAsync());
        Assert.NotNull((await StreamAsync(stream.Id)).PostDecidedAt);
    }

    [Fact]
    public async Task AStreamThatEndedTooSoonNeverPosts()
    {
        await SetUpAsync(TickVRChat);
        var stream = GoLive(startedMinutesAgo: 0);
        await PassAsync();

        // Gone after a minute: a test stream.
        _twitch.Live = null;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();

        // An hour on, it is settled for good.
        _clock.Advance(TwitchLivePass.EndedStaysOpenFor);
        await PassAsync();

        Assert.Empty(await LivePostsAsync());
        Assert.NotNull((await StreamAsync(stream.Id)).PostDecidedAt);
    }

    [Fact]
    public async Task PausingAllPostingHoldsTheLivePostLikeAnyOther()
    {
        await SetUpAsync(s =>
        {
            TickVRChat(s);
            s.PostsPaused = true;
        });
        GoLive(startedMinutesAgo: 4);

        await PassAsync();

        // The post is made and waits: the senders hold it, and it is late after fifteen minutes.
        var post = Assert.Single(await LivePostsAsync());
        var destination = Assert.Single(post.Destinations);
        Assert.Equal(PostDestinationStates.Waiting, destination.State);

        var sites = new PostSites(Paused: true, DiscordOn: true, DiscordSetUp: true, VRChatOn: true, VRChatSetUp: true);
        Assert.Equal(PostHolds.Paused, PostRules.Shown(destination, sites));
        Assert.False(PostRules.IsLate(post, destination, _clock.UtcNow.AddMinutes(15)));
        Assert.True(PostRules.IsLate(post, destination, _clock.UtcNow.AddMinutes(16)));
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string? Unprotect(string? ciphertext) => ciphertext;
    }
}
