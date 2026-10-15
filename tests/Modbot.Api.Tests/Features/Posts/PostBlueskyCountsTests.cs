using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Posts;
using Modbot.Core.Bluesky;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Posts;

/// <summary>
/// The numbers under sent Bluesky posts, read from Bluesky's public API against a scripted answer: only
/// posts sent within 30 days, each at most every 30 minutes, 25 to a call, a post gone from Bluesky keeping
/// its last numbers, a rate limit stopping the pass without a retry until it resets, and a failure kept for
/// Health until a read goes through.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PostBlueskyCountsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PostgresFixture _db;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly PublicApi _api = new();

    public PostBlueskyCountsTests(PostgresFixture db) => _db = db;

    /// <summary>The public API: records each request and answers as told.</summary>
    private sealed class PublicApi : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        /// <summary>Counts by post address; an address not here is gone from Bluesky.</summary>
        public Dictionary<string, (int Likes, int Reposts, int Replies)> Posts { get; } = new(StringComparer.Ordinal);

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);

            if (Status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(Status)
                {
                    Content = new StringContent(
                    Status == HttpStatusCode.TooManyRequests ? """{"error":"RateLimitExceeded","message":"Rate Limit Exceeded"}""" : "{}",
                    Encoding.UTF8,
                    "application/json"),
                });
            }

            var asked = request.RequestUri!.Query.TrimStart('?').Split('&')
                .Where(p => p.StartsWith("uris=", StringComparison.Ordinal))
                .Select(p => Uri.UnescapeDataString(p["uris=".Length..]))
                .ToList();

            var posts = new JsonArray();
            foreach (var uri in asked.Where(Posts.ContainsKey))
            {
                var (likes, reposts, replies) = Posts[uri];
                posts.Add(new JsonObject { ["uri"] = uri, ["likeCount"] = likes, ["repostCount"] = reposts, ["replyCount"] = replies, ["quoteCount"] = 1 });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["posts"] = posts }.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private async Task SetUpAsync()
    {
        await using var context = _db.NewContext();
        await context.Posts.ExecuteDeleteAsync(Ct);
        await context.GetSettingsAsync(Ct);
        await context.Settings.Where(s => s.Id == 1).ExecuteUpdateAsync(
            u => u.SetProperty(s => s.BlueskyCountsStoppedUntil, (DateTimeOffset?)null)
                .SetProperty(s => s.BlueskyStoppedUntil, (DateTimeOffset?)null)
                .SetProperty(s => s.BlueskyCountsError, (string?)null)
                .SetProperty(s => s.BlueskyCountsErrorAt, (DateTimeOffset?)null), Ct);
    }

    /// <summary>One pass; <paramref name="lastCall"/> is the process-wide last-call time, new for each pass unless given.</summary>
    private async Task<PostBlueskyCountsPass> PassAsync(PostBlueskyCountsLastCall? lastCall = null)
    {
        await using var context = _db.NewContext();
        await context.GetSettingsAsync(Ct);
        var pass = new PostBlueskyCounts(context, _clock, new BlueskyClient(new OneHandlerClients(_api), _clock), lastCall);
        return await pass.RunOnceAsync(Ct);
    }

    private static string UriOf(string key) => $"at://{FakeBluesky.Did}/app.bsky.feed.post/{key}";

    /// <summary>A Posted Bluesky destination sent <paramref name="sentAgo"/> ago, counted <paramref name="countedAgo"/> ago (null: never).</summary>
    private async Task<Guid> AddAsync(string key, TimeSpan sentAgo, TimeSpan? countedAgo = null, string state = PostDestinationStates.Posted, int? likes = null)
    {
        await using var context = _db.NewContext();
        var sent = _clock.UtcNow - sentAgo;

        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            Text = "Friday at eight.",
            Status = PostStatuses.Scheduled,
            SendAt = sent,
            TimeZone = "UTC",
            CreatedAt = sent,
            UpdatedAt = sent,
        };

        var destination = new PostDestination
        {
            Id = Guid.CreateVersion7(),
            PostId = post.Id,
            Network = PostNetworks.Bluesky,
            Target = FakeBluesky.Did,
            State = state,
            ClientKey = key,
            ExternalId = UriOf(key),
            PostedAt = sent,
            SentAt = sent,
            UpdatedAt = sent,
            LikeCount = likes,
            RepostCount = likes is null ? null : 0,
            ReplyCount = likes is null ? null : 0,
            CountedAt = countedAgo is { } ago ? _clock.UtcNow - ago : null,
        };

        post.Destinations.Add(destination);
        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);
        return destination.Id;
    }

    private async Task<PostDestination> DestinationAsync(Guid id)
    {
        await using var context = _db.NewContext();
        return await context.PostDestinations.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
    }

    private async Task<Core.Data.Entities.Settings> SettingsAsync()
    {
        await using var context = _db.NewContext();
        return await context.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct);
    }

    [Fact]
    public async Task ASentPostGetsItsLikesRepostsAndReplies_FromThePublicApiWithNoSignIn()
    {
        await SetUpAsync();
        var id = await AddAsync("3kaaaaaaaaaaa", TimeSpan.FromHours(2));
        _api.Posts[UriOf("3kaaaaaaaaaaa")] = (12, 3, 5);

        var pass = await PassAsync();

        var destination = await DestinationAsync(id);
        Assert.Equal(1, pass.Counted);
        Assert.Equal((12, 3, 5), (destination.LikeCount, destination.RepostCount, destination.ReplyCount));
        Assert.Equal(_clock.UtcNow, destination.CountedAt);

        var request = Assert.Single(_api.Requests);
        Assert.Equal("public.api.bsky.app", request.Host);
        Assert.Equal("/xrpc/app.bsky.feed.getPosts", request.AbsolutePath);
    }

    [Fact]
    public async Task OnlyPostsSentWithinThirtyDaysAreCounted_AnOlderOneKeepsItsLastNumbers()
    {
        await SetUpAsync();
        var recent = await AddAsync("3kbbbbbbbbbbb", TimeSpan.FromDays(29));
        var old = await AddAsync("3kccccccccccc", TimeSpan.FromDays(31), likes: 4);
        _api.Posts[UriOf("3kbbbbbbbbbbb")] = (1, 1, 1);
        _api.Posts[UriOf("3kccccccccccc")] = (99, 99, 99);

        await PassAsync();

        Assert.Equal(1, (await DestinationAsync(recent)).LikeCount);
        Assert.Equal(4, (await DestinationAsync(old)).LikeCount);
        var asked = Assert.Single(_api.Requests).Query;
        Assert.DoesNotContain("3kccccccccccc", asked);
    }

    [Fact]
    public async Task APostIsCountedAgainOnlyThirtyMinutesAfterTheLastCount()
    {
        await SetUpAsync();
        var id = await AddAsync("3kddddddddddd", TimeSpan.FromHours(5), TimeSpan.FromMinutes(20), likes: 1);
        _api.Posts[UriOf("3kddddddddddd")] = (8, 0, 0);

        var early = await PassAsync();

        Assert.Equal(0, early.Asked);
        Assert.Empty(_api.Requests);
        Assert.Equal(1, (await DestinationAsync(id)).LikeCount);

        _clock.Advance(TimeSpan.FromMinutes(11));
        var later = await PassAsync();

        Assert.Equal(1, later.Counted);
        Assert.Equal(8, (await DestinationAsync(id)).LikeCount);
    }

    [Fact]
    public async Task OnlyPostedDestinationsAreCounted()
    {
        await SetUpAsync();
        await AddAsync("3keeeeeeeeeee", TimeSpan.FromHours(1), state: PostDestinationStates.Removed);
        await AddAsync("3kfffffffffff", TimeSpan.FromHours(1), state: PostDestinationStates.Failed);

        var pass = await PassAsync();

        Assert.Equal(0, pass.Asked);
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task APostGoneFromBlueskyKeepsItsLastNumbers_AndIsNotAskedForAgainAtOnce()
    {
        await SetUpAsync();
        var id = await AddAsync("3kggggggggggg", TimeSpan.FromHours(5), TimeSpan.FromHours(2), likes: 6);

        var pass = await PassAsync();

        var destination = await DestinationAsync(id);
        Assert.Equal(1, pass.Missing);
        Assert.Equal(6, destination.LikeCount);
        Assert.Equal(_clock.UtcNow, destination.CountedAt);

        await PassAsync();
        Assert.Single(_api.Requests);
    }

    [Fact]
    public async Task OneCallAsksForTwentyFivePostsAtMost_ThoseNeverCountedFirst()
    {
        await SetUpAsync();
        var counted = await AddAsync("3khhhhhhhhhhh", TimeSpan.FromHours(9), TimeSpan.FromHours(3), likes: 1);
        var fresh = new List<Guid>();
        for (var i = 0; i < BlueskyClient.MostCountedAtOnce; i++)
        {
            var key = $"3kn{i:D10}";
            fresh.Add(await AddAsync(key, TimeSpan.FromMinutes(10 + i)));
            _api.Posts[UriOf(key)] = (i, 0, 0);
        }

        var pass = await PassAsync();

        Assert.Equal(BlueskyClient.MostCountedAtOnce, pass.Asked);
        Assert.Single(_api.Requests);
        Assert.Equal(1, (await DestinationAsync(counted)).LikeCount);

        foreach (var id in fresh)
            Assert.NotNull((await DestinationAsync(id)).CountedAt);
    }

    [Fact]
    public async Task ARateLimitStopsThePassWithoutARetry_UntilItResets()
    {
        await SetUpAsync();
        var id = await AddAsync("3kiiiiiiiiiii", TimeSpan.FromHours(2));
        _api.Posts[UriOf("3kiiiiiiiiiii")] = (2, 0, 0);
        _api.Status = HttpStatusCode.TooManyRequests;

        await PassAsync();

        Assert.Single(_api.Requests);
        var settings = await SettingsAsync();
        Assert.Equal(BlueskyErrors.Limited, settings.BlueskyCountsError);
        Assert.NotNull(settings.BlueskyCountsStoppedUntil);
        Assert.True(settings.BlueskyCountsStoppedUntil > _clock.UtcNow);
        Assert.Null((await DestinationAsync(id)).CountedAt);

        // Still stopped: nothing is asked.
        _clock.Advance(TimeSpan.FromSeconds(30));
        await PassAsync();
        Assert.Single(_api.Requests);

        // The limit has reset: asked again, and a read that goes through clears the error.
        _api.Status = HttpStatusCode.OK;
        _clock.Advance(BlueskyErrors.LimitStop);
        await PassAsync();

        Assert.Equal(2, _api.Requests.Count);
        Assert.Equal(2, (await DestinationAsync(id)).LikeCount);
        Assert.Null((await SettingsAsync()).BlueskyCountsError);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, BlueskyErrors.Unreachable, PostBlueskyCounts.CouldNotReach)]
    [InlineData(HttpStatusCode.BadRequest, null, null)]
    public async Task AFailureThatIsNotARateLimitPausesThePassForFiveMinutes_AndSaysItIsBluesky(HttpStatusCode status, string? notThis, string? sentence)
    {
        await SetUpAsync();
        var id = await AddAsync("3kmmmmmmmmmmm", TimeSpan.FromHours(2));
        _api.Posts[UriOf("3kmmmmmmmmmmm")] = (4, 0, 0);
        _api.Status = status;

        await PassAsync();

        Assert.Single(_api.Requests);
        var settings = await SettingsAsync();
        Assert.Equal(_clock.UtcNow + PostBlueskyCounts.FailurePause, settings.BlueskyCountsStoppedUntil);
        Assert.NotNull(settings.BlueskyCountsError);
        Assert.NotEqual(notThis, settings.BlueskyCountsError);

        if (sentence is not null)
            Assert.Equal(sentence, settings.BlueskyCountsError);

        // Not asked again every poll.
        _clock.Advance(PostBlueskyService.PollInterval);
        await PassAsync();
        Assert.Single(_api.Requests);

        // Asked again once the pause is over, and a read that goes through clears the error.
        _api.Status = HttpStatusCode.OK;
        _clock.Advance(PostBlueskyCounts.FailurePause);
        await PassAsync();

        Assert.Equal(2, _api.Requests.Count);
        Assert.Equal(4, (await DestinationAsync(id)).LikeCount);
        Assert.Null((await SettingsAsync()).BlueskyCountsError);
    }

    [Fact]
    public async Task TheErrorClearsWhenThePauseIsOverAndNothingIsLeftToCount()
    {
        await SetUpAsync();
        await AddAsync("3knnnnnnnnnnn", TimeSpan.FromHours(2));
        _api.Status = HttpStatusCode.ServiceUnavailable;
        await PassAsync();
        Assert.NotNull((await SettingsAsync()).BlueskyCountsError);

        // The only post is now older than 30 days: no rows left, so no call, yet the error goes.
        await using (var context = _db.NewContext())
        {
            await context.PostDestinations.ExecuteUpdateAsync(
                u => u.SetProperty(d => d.PostedAt, _clock.UtcNow - PostBlueskyCounts.Window - TimeSpan.FromDays(1)), Ct);
        }

        // Still paused: the error stays.
        _clock.Advance(PostBlueskyService.PollInterval);
        await PassAsync();
        Assert.NotNull((await SettingsAsync()).BlueskyCountsError);

        _clock.Advance(PostBlueskyCounts.FailurePause);
        await PassAsync();

        Assert.Single(_api.Requests);
        var settings = await SettingsAsync();
        Assert.Null(settings.BlueskyCountsError);
        Assert.Null(settings.BlueskyCountsErrorAt);
    }

    [Fact]
    public async Task TwoCallsAreNeverLessThanTheCallGapApart_EvenAcrossPasses()
    {
        await SetUpAsync();
        var lastCall = new PostBlueskyCountsLastCall();
        await AddAsync("3koooooooooo1", TimeSpan.FromHours(2));
        await PassAsync(lastCall);
        Assert.Single(_api.Requests);

        await AddAsync("3koooooooooo2", TimeSpan.FromHours(2));
        _clock.Advance(PostBlueskyCounts.CallGap - TimeSpan.FromSeconds(1));
        var tooSoon = await PassAsync(lastCall);

        Assert.Equal(0, tooSoon.Asked);
        Assert.Single(_api.Requests);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await PassAsync(lastCall);

        Assert.Equal(2, _api.Requests.Count);
    }

    [Fact]
    public async Task TheRateLimitOnThePublicApiDoesNotStopThePostingLane()
    {
        await SetUpAsync();
        await AddAsync("3kjjjjjjjjjjj", TimeSpan.FromHours(2));
        _api.Status = HttpStatusCode.TooManyRequests;

        await PassAsync();

        Assert.Null((await SettingsAsync()).BlueskyStoppedUntil);
    }

    [Fact]
    public void TheLoopRunsLessOftenThanTheCallGap()
    {
        Assert.True(PostBlueskyService.PollInterval >= PostBlueskyCounts.CallGap);
    }
}
