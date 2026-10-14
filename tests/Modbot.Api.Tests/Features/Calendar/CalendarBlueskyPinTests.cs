using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.Core.Security;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// Pin the next event (calendar design, Bluesky extras): the post of the first date that has not ended
/// is pinned on the account's profile; when that date ends the next date's post takes the pin; with
/// none left the pin comes off; a post a person pinned by hand is left alone; the rest of the profile
/// is written back as it was; a 429 stops the lane.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CalendarBlueskyPinTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PostgresFixture _db;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeBluesky _bluesky;
    private readonly BlueskySession _session;

    public CalendarBlueskyPinTests(PostgresFixture db)
    {
        _db = db;
        _bluesky = new FakeBluesky(() => _clock.UtcNow);
        _session = new BlueskySession(Client(), new PlainProtector(), _clock, new BlueskyOAuth(new OneHandlerClients(_bluesky), new PlainProtector(), _clock));
    }

    private BlueskyClient Client() => new(new OneHandlerClients(_bluesky), _clock);

    /// <summary>A checked Bluesky account with Posting and Pin the next event on, and no events or posts.</summary>
    private async Task SetUpAsync(Action<Core.Data.Entities.Settings>? change = null)
    {
        await using var context = _db.NewContext();
        await context.Posts.ExecuteDeleteAsync(Ct);
        await context.CalendarOpenings.ExecuteDeleteAsync(Ct);
        await context.CalendarDateChanges.ExecuteDeleteAsync(Ct);
        await context.CalendarEventPlaces.ExecuteDeleteAsync(Ct);
        await context.CalendarEvents.IgnoreAutoIncludes().ExecuteDeleteAsync(Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.PostsPaused = false;
        settings.BlueskyHandle = FakeBluesky.Handle;
        settings.BlueskyDid = FakeBluesky.Did;
        settings.BlueskyServer = FakeBluesky.Server;
        settings.BlueskyAppPasswordEncrypted = FakeBluesky.AppPassword;
        settings.BlueskySessionEncrypted = null;
        settings.BlueskyCheckedAt = _clock.UtcNow.AddHours(-1);
        settings.BlueskyProblem = null;
        settings.BlueskyPostingOn = true;
        settings.BlueskyStoppedUntil = null;
        settings.BlueskySignInRefused = false;
        settings.BlueskySignedInAt = null;
        settings.BlueskySignInsDay = null;
        settings.BlueskySignInsUsed = 0;
        settings.BlueskyPinNextEvent = true;
        settings.BlueskyPinnedUri = null;
        settings.BlueskyPinCheckedAt = null;
        change?.Invoke(settings);

        await context.SaveChangesAsync(Ct);
    }

    private static string UriOf(string key) => $"at://{FakeBluesky.Did}/app.bsky.feed.post/{key}";

    /// <summary>
    /// A one-time event whose date starts <paramref name="startsIn"/> from now and lasts two hours, with
    /// its Bluesky post for that date.
    /// </summary>
    private async Task<Guid> AddDateAsync(
        string key, TimeSpan startsIn, string state = PostDestinationStates.Posted, string? cid = "bafy-post")
    {
        var now = _clock.UtcNow;
        var startsAt = now + startsIn;

        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night " + key,
            StartsAt = startsAt,
            EndsAt = startsAt.AddHours(2),
            OccurrenceStartsAt = startsAt,
            TimeZone = "Europe/London",
            Visibility = "public",
            AccessType = "public",
            PublishToBluesky = true,
            State = CalendarEventStates.Scheduled,
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-2),
        };

        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            Title = e.Title,
            Text = e.Title,
            Kind = PostKinds.EventDate,
            EventId = e.Id,
            DateStartsAt = startsAt,
            Status = PostStatuses.Scheduled,
            SendAt = startsAt.AddDays(-1),
            TimeZone = "UTC",
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-2),
        };

        post.Destinations.Add(new PostDestination
        {
            Id = Guid.CreateVersion7(),
            PostId = post.Id,
            Network = PostNetworks.Bluesky,
            Target = FakeBluesky.Did,
            State = state,
            ExternalId = state == PostDestinationStates.Posted ? UriOf(key) : null,
            ClientKey = key,
            Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(Cid: cid)),
            UpdatedAt = now.AddDays(-1),
        });

        await using var context = _db.NewContext();
        context.CalendarEvents.Add(e);
        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);

        return post.Id;
    }

    private async Task<CalendarBlueskyPinPass> PassAsync()
    {
        await using var context = _db.NewContext();
        var pin = new CalendarBlueskyPin(
            context,
            _clock,
            new FactWriter(context, _clock),
            new EventPartitionMaintainer(context, _clock),
            _session,
            Client());

        return await pin.RunOnceAsync(Ct);
    }

    private async Task<Core.Data.Entities.Settings> SettingsAsync()
    {
        await using var context = _db.NewContext();
        return await context.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct);
    }

    private async Task<bool> FactAsync(string type, Guid postId)
    {
        await using var context = _db.NewContext();
        return await context.Events.AnyAsync(e => e.Type == type && e.SubjectId == postId.ToString(), Ct);
    }

    private int ProfileReads => _bluesky.Requests.Count(r => r.Method == "com.atproto.repo.getRecord");

    private static JsonObject Pin(string key) => new()
    {
        ["$type"] = "com.atproto.repo.strongRef",
        ["uri"] = UriOf(key),
        ["cid"] = "bafy-post",
    };

    [Fact]
    public async Task TheFirstDatesPostIsPinned_AndTheRestOfTheProfileIsWrittenBackAsItWas()
    {
        await SetUpAsync();
        var first = await AddDateAsync("first", TimeSpan.FromDays(1));
        await AddDateAsync("second", TimeSpan.FromDays(8));

        var pass = await PassAsync();

        Assert.Equal(1, pass.Pinned);
        Assert.Equal(UriOf("first"), _bluesky.PinnedPost!["uri"]!.GetValue<string>());
        Assert.Equal("bafy-post", _bluesky.PinnedPost["cid"]!.GetValue<string>());
        Assert.Equal("com.atproto.repo.strongRef", _bluesky.PinnedPost["$type"]!.GetValue<string>());

        // The profile went back whole: its name and its self-label are still there.
        Assert.Equal("Our group", _bluesky.LastProfilePut!["displayName"]!.GetValue<string>());
        Assert.NotNull(_bluesky.LastProfilePut["labels"]);

        Assert.Equal(UriOf("first"), (await SettingsAsync()).BlueskyPinnedUri);
        Assert.True(await FactAsync(FactType.PostPinned, first));
    }

    [Fact]
    public async Task ARepeatPassAsksBlueskyNothing()
    {
        await SetUpAsync();
        await AddDateAsync("first", TimeSpan.FromDays(1));
        await PassAsync();

        var reads = ProfileReads;
        _clock.Advance(TimeSpan.FromMinutes(1));
        var pass = await PassAsync();

        Assert.Equal(0, pass.Pinned);
        Assert.Equal(reads, ProfileReads);
        Assert.Equal(1, _bluesky.ProfilePuts);
    }

    [Fact]
    public async Task WhenTheDateEndsTheNextDatesPostTakesThePin()
    {
        await SetUpAsync();
        await AddDateAsync("first", TimeSpan.FromHours(1));
        var second = await AddDateAsync("second", TimeSpan.FromDays(7));
        await PassAsync();
        Assert.Equal(UriOf("first"), _bluesky.PinnedPost!["uri"]!.GetValue<string>());

        // Still under way: two hours long, started one hour ago.
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, (await PassAsync()).Pinned);
        Assert.Equal(UriOf("first"), _bluesky.PinnedPost["uri"]!.GetValue<string>());

        // Ended.
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, (await PassAsync()).Pinned);
        Assert.Equal(UriOf("second"), _bluesky.PinnedPost["uri"]!.GetValue<string>());
        Assert.Equal(UriOf("second"), (await SettingsAsync()).BlueskyPinnedUri);
        Assert.True(await FactAsync(FactType.PostPinned, second));
    }

    [Fact]
    public async Task WithNoDateLeftThePinComesOff()
    {
        await SetUpAsync();
        var only = await AddDateAsync("only", TimeSpan.FromHours(1));
        await PassAsync();

        _clock.Advance(TimeSpan.FromHours(4));
        var pass = await PassAsync();

        Assert.Equal(1, pass.Unpinned);
        Assert.Null(_bluesky.PinnedPost);
        Assert.False(_bluesky.LastProfilePut!.ContainsKey("pinnedPost"));
        Assert.Equal("Our group", _bluesky.LastProfilePut["displayName"]!.GetValue<string>());
        Assert.Null((await SettingsAsync()).BlueskyPinnedUri);
        Assert.True(await FactAsync(FactType.PostUnpinned, only));

        // And nothing more is asked once it is off.
        var reads = ProfileReads;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();
        Assert.Equal(reads, ProfileReads);
    }

    [Fact]
    public async Task APostAPersonPinnedByHandIsLeftAlone_AndLookedAtAgainOnlyAfterAWhile()
    {
        await SetUpAsync();
        await AddDateAsync("first", TimeSpan.FromDays(1));
        _bluesky.PinnedPost = Pin("by-hand");

        var pass = await PassAsync();

        Assert.Equal(0, pass.Pinned);
        Assert.Equal(0, _bluesky.ProfilePuts);
        Assert.Equal(UriOf("by-hand"), _bluesky.PinnedPost["uri"]!.GetValue<string>());
        Assert.NotNull((await SettingsAsync()).BlueskyPinCheckedAt);

        var reads = ProfileReads;
        _clock.Advance(TimeSpan.FromMinutes(5));
        await PassAsync();
        Assert.Equal(reads, ProfileReads);

        _clock.Advance(CalendarBlueskyPin.LookEvery);
        await PassAsync();
        Assert.Equal(reads + 1, ProfileReads);
        Assert.Equal(0, _bluesky.ProfilePuts);
    }

    [Fact]
    public async Task APinOfModbotsOwnIsReplacedEvenIfSettingsForgotIt()
    {
        await SetUpAsync();
        await AddDateAsync("past", TimeSpan.FromHours(-30));
        await AddDateAsync("next", TimeSpan.FromDays(1));
        _bluesky.PinnedPost = Pin("past");

        var pass = await PassAsync();

        Assert.Equal(1, pass.Pinned);
        Assert.Equal(UriOf("next"), _bluesky.PinnedPost["uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task OnlyAPostThatIsOnBlueskyWithAContentIdIsPinned()
    {
        await SetUpAsync();
        await AddDateAsync("waiting", TimeSpan.FromHours(5), state: PostDestinationStates.Waiting);
        await AddDateAsync("no-id", TimeSpan.FromHours(6), cid: null);
        await AddDateAsync("ready", TimeSpan.FromDays(2));

        await PassAsync();

        Assert.Equal(UriOf("ready"), _bluesky.PinnedPost!["uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task NothingHappensWithTheSwitchOff_OrWhileBlueskyIsHeld()
    {
        await SetUpAsync(s => s.BlueskyPinNextEvent = false);
        await AddDateAsync("first", TimeSpan.FromDays(1));
        await PassAsync();
        Assert.Empty(_bluesky.Requests);

        await SetUpAsync(s => s.BlueskyPostingOn = false);
        await AddDateAsync("first", TimeSpan.FromDays(1));
        await PassAsync();
        Assert.Empty(_bluesky.Requests);

        await SetUpAsync(s => s.PostsPaused = true);
        await AddDateAsync("first", TimeSpan.FromDays(1));
        await PassAsync();
        Assert.Empty(_bluesky.Requests);
        Assert.Null(_bluesky.PinnedPost);
    }

    [Fact]
    public async Task ARateLimitStopsTheLane_AndNothingIsAskedAgainBeforeItResets()
    {
        await SetUpAsync();
        await AddDateAsync("first", TimeSpan.FromDays(1));
        _bluesky.LimitedUntil = _clock.UtcNow.AddMinutes(30);

        var pass = await PassAsync();

        Assert.Equal(0, pass.Pinned);
        Assert.NotNull((await SettingsAsync()).BlueskyStoppedUntil);

        var requests = _bluesky.Requests.Count;
        _bluesky.LimitedUntil = null;
        _clock.Advance(TimeSpan.FromMinutes(5));
        await PassAsync();

        Assert.Equal(requests, _bluesky.Requests.Count);
        Assert.Null(_bluesky.PinnedPost);
    }

    [Fact]
    public async Task AProfileSavedMeanwhileIsNotWrittenOver_AndIsReadAgainLater()
    {
        await SetUpAsync();
        await AddDateAsync("first", TimeSpan.FromDays(1));

        // The write finds a different profile than the one read.
        _bluesky.ProfileChangesAfterNextRead = true;

        var pass = await PassAsync();

        Assert.Equal(0, pass.Pinned);
        Assert.Null(_bluesky.PinnedPost);
        Assert.Null((await SettingsAsync()).BlueskyPinnedUri);
        Assert.NotNull((await SettingsAsync()).BlueskyPinCheckedAt);

        _clock.Advance(CalendarBlueskyPin.LookEvery);
        Assert.Equal(1, (await PassAsync()).Pinned);
        Assert.Equal(UriOf("first"), _bluesky.PinnedPost!["uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task APinWhoseWritingIsRefused_IsNotRetriedUntilTheWaitIsOver()
    {
        await SetUpAsync();
        await AddDateAsync("first", TimeSpan.FromDays(1));
        _bluesky.ProfilePutStatus = HttpStatusCode.Forbidden;

        Assert.Equal(0, (await PassAsync()).Pinned);
        var reads = ProfileReads;

        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();
        Assert.Equal(reads, ProfileReads);
        Assert.Null((await SettingsAsync()).BlueskyPinnedUri);
    }

    /// <summary>The app password and session are stored as they are: the protector's own tests cover the encryption.</summary>
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string? Unprotect(string? ciphertext) => ciphertext;
    }
}
