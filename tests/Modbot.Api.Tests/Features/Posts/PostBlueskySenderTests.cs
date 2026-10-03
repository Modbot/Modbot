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
/// Posts sent to Bluesky (Bluesky design §3.4, §3.9; posts design §4.2c) against a scripted Bluesky
/// that keeps posts by record key the way the reference server does: one key made once and kept for
/// every try; a lost answer read back and taken, never sent again; a try that did not land sent again
/// under the same key with a new time; InvalidSwap read back; a 429 stops everything until it resets;
/// a refresh saves the new tokens; Check and the loop at once sign in once.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PostBlueskySenderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PostgresFixture _db;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeBluesky _bluesky;
    private readonly BlueskySession _session;

    public PostBlueskySenderTests(PostgresFixture db)
    {
        _db = db;
        _bluesky = new FakeBluesky(() => _clock.UtcNow);
        _session = new BlueskySession(Client(), new PlainProtector(), _clock, new BlueskyOAuth(new OneHandlerClients(_bluesky), new PlainProtector(), _clock));
    }

    private BlueskyClient Client() => new(new OneHandlerClients(_bluesky), _clock);

    /// <summary>Bluesky set up, checked and posting, with no posts.</summary>
    private async Task SetUpAsync(Action<Core.Data.Entities.Settings>? change = null)
    {
        await using var context = _db.NewContext();
        await context.Posts.ExecuteDeleteAsync(Ct);

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
        change?.Invoke(settings);

        await context.SaveChangesAsync(Ct);
    }

    private async Task<PostBlueskyPass> PassAsync()
    {
        await using var context = _db.NewContext();
        var sender = new PostBlueskySender(
            context,
            _clock,
            new FactWriter(context, _clock),
            new EventPartitionMaintainer(context, _clock),
            new PostClaim(context, _clock),
            _session,
            Client(),
            new BlueskyIdentity(new OneHandlerClients(_bluesky)));

        return await sender.RunOnceAsync(Ct);
    }

    /// <summary>A post due now, ticked for Bluesky only.</summary>
    private async Task<(Guid PostId, Guid DestinationId)> AddPostAsync(
        string? title = "Movie night",
        string text = "Friday at eight. Bring snacks.",
        string target = FakeBluesky.Did,
        Action<Post, PostDestination>? shape = null)
    {
        await using var context = _db.NewContext();

        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            Text = text,
            Status = PostStatuses.Scheduled,
            SendAt = _clock.UtcNow,
            TimeZone = "UTC",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        };

        var destination = new PostDestination
        {
            Id = Guid.CreateVersion7(),
            PostId = post.Id,
            Network = PostNetworks.Bluesky,
            Target = target,
            Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions()),
            State = PostDestinationStates.Waiting,
            UpdatedAt = _clock.UtcNow,
        };

        shape?.Invoke(post, destination);
        post.Destinations.Add(destination);
        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);

        return (post.Id, destination.Id);
    }

    private async Task<PostDestination> DestinationAsync(Guid id)
    {
        await using var context = _db.NewContext();
        return await context.PostDestinations.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
    }

    private List<JsonObject> PutRecords() =>
        [.. _bluesky.Requests
            .Where(r => r.Method == "com.atproto.repo.putRecord")
            .Select(r => (JsonObject)JsonNode.Parse(r.Body!)!)];

    [Fact]
    public async Task APostGoesOnceAtItsTime_UnderAKeyModbotMade()
    {
        await SetUpAsync();
        var (postId, destinationId) = await AddPostAsync();

        var pass = await PassAsync();

        Assert.Equal(1, pass.Sent);
        var put = Assert.Single(PutRecords());
        var key = put["rkey"]!.GetValue<string>();
        Assert.True(Tid.IsTid(key));
        Assert.True(put.ContainsKey("swapRecord"));
        Assert.Null(put["swapRecord"]);
        Assert.Equal(FakeBluesky.Did, put["repo"]!.GetValue<string>());
        Assert.Equal("Movie night\nFriday at eight. Bring snacks.", put["record"]!["text"]!.GetValue<string>());
        Assert.Equal("2026-10-05T12:00:00.000Z", put["record"]!["createdAt"]!.GetValue<string>());

        var destination = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Posted, destination.State);
        Assert.Equal(key, destination.ClientKey);
        Assert.Equal($"at://{FakeBluesky.Did}/app.bsky.feed.post/{key}", destination.ExternalId);
        Assert.Equal($"https://bsky.app/profile/{FakeBluesky.Did}/post/{key}", destination.Link);
        Assert.Equal($"bafy-{key}", PostTexts.BlueskyOptionsOf(destination).Cid);

        // Nothing more on the next pass.
        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();
        Assert.Equal(1, _bluesky.Puts);

        await using var context = _db.NewContext();
        Assert.True(await context.Events.AnyAsync(e => e.Type == FactType.PostSent && e.SubjectId == postId.ToString(), Ct));
    }

    [Fact]
    public async Task ALostAnswerIsReadBack_AndTaken_NeverSentAgain()
    {
        await SetUpAsync();
        var (_, destinationId) = await AddPostAsync();
        _bluesky.DropNextPutAnswer = true;

        await PassAsync();

        var checking = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Checking, checking.State);
        Assert.True(checking.MayBeSent);

        // Not before the minute is up.
        _clock.Advance(TimeSpan.FromSeconds(30));
        await PassAsync();
        Assert.DoesNotContain(_bluesky.Requests, r => r.Method == "com.atproto.repo.getRecord");

        _clock.Advance(TimeSpan.FromSeconds(35));
        var pass = await PassAsync();

        Assert.Equal(1, pass.Looked);
        Assert.Equal(1, pass.Sent);
        Assert.Equal(1, _bluesky.Puts);
        Assert.Equal(PostDestinationStates.Posted, (await DestinationAsync(destinationId)).State);
    }

    [Fact]
    public async Task ATryThatDidNotLandIsSentAgainUnderTheSameKey_WithANewTime()
    {
        await SetUpAsync();
        var (_, destinationId) = await AddPostAsync();

        // The first put fails on Bluesky's side and nothing is kept.
        var refusedOnce = false;
        _bluesky.PutAnswer = (_, _) =>
        {
            if (refusedOnce)
                return null;

            refusedOnce = true;
            return FakeBluesky.Json(HttpStatusCode.BadGateway, FakeBluesky.Error("UpstreamFailure", "Upstream Failure"));
        };

        await PassAsync();
        Assert.Equal(PostDestinationStates.Checking, (await DestinationAsync(destinationId)).State);

        _clock.Advance(TimeSpan.FromSeconds(65));
        await PassAsync();

        var puts = PutRecords();
        Assert.Equal(2, puts.Count);
        Assert.Equal(puts[0]["rkey"]!.GetValue<string>(), puts[1]["rkey"]!.GetValue<string>());
        Assert.NotEqual(puts[0]["record"]!["createdAt"]!.GetValue<string>(), puts[1]["record"]!["createdAt"]!.GetValue<string>());
        Assert.Equal(PostDestinationStates.Posted, (await DestinationAsync(destinationId)).State);
        Assert.Single(_bluesky.Posts);
    }

    [Fact]
    public async Task InvalidSwapIsReadBack_AndTheEarlierTryTaken()
    {
        await SetUpAsync();
        var (_, destinationId) = await AddPostAsync();

        // An earlier try landed late: something is at the key already.
        _bluesky.PutAnswer = (key, record) =>
        {
            _bluesky.Posts[key] = (JsonObject)record.DeepClone();
            return FakeBluesky.Json(HttpStatusCode.BadRequest, FakeBluesky.Error("InvalidSwap", "Record was at bafy-old"));
        };

        var pass = await PassAsync();

        Assert.Equal(1, pass.Sent);
        Assert.Equal(1, _bluesky.Puts);
        Assert.Equal(PostDestinationStates.Posted, (await DestinationAsync(destinationId)).State);
    }

    [Fact]
    public async Task InvalidSwapThenNothingFound_IsMaybeSent_AndNeverSentAgain_EvenByTryAgain()
    {
        await SetUpAsync();
        var (postId, destinationId) = await AddPostAsync();

        // Bluesky says the key is taken, then cannot show what is there.
        _bluesky.PutAnswer = (key, record) =>
        {
            _bluesky.Posts[key] = (JsonObject)record.DeepClone();
            return FakeBluesky.Json(HttpStatusCode.BadRequest, FakeBluesky.Error("InvalidSwap", "Record was at bafy-old"));
        };
        _bluesky.GetAnswer = key => FakeBluesky.Json(HttpStatusCode.BadRequest, FakeBluesky.Error("RecordNotFound", "Could not locate record"));

        await PassAsync();

        var failed = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.True(failed.MayBeSent);
        Assert.Equal(PostBlueskySender.KeyTaken, failed.Error);
        Assert.True(PostTexts.BlueskyOptionsOf(failed).KeyTaken);

        // A person presses Try again: it reads back, still finds nothing, and sends nothing.
        await TryAgainAsync(postId, destinationId);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await PassAsync();
        _clock.Advance(TimeSpan.FromMinutes(2));
        await PassAsync();

        Assert.Equal(1, _bluesky.Puts);
        var again = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Failed, again.State);
        Assert.True(again.MayBeSent);

        // Once Bluesky shows it, Try again takes it.
        _bluesky.GetAnswer = null;
        await TryAgainAsync(postId, destinationId);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync();

        Assert.Equal(1, _bluesky.Puts);
        Assert.Equal(PostDestinationStates.Posted, (await DestinationAsync(destinationId)).State);
    }

    /// <summary>What the Try again button does to the rows.</summary>
    private async Task TryAgainAsync(Guid postId, Guid destinationId)
    {
        await using var context = _db.NewContext();
        var post = await context.Posts.Include(p => p.Destinations).SingleAsync(p => p.Id == postId, Ct);
        PostChanges.TryAgain(post, post.Destinations.Single(d => d.Id == destinationId), _clock.UtcNow);
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ARateLimitStopsEveryCall_UntilItResets_AndIsNeverRetriedBefore()
    {
        await SetUpAsync();
        var (_, destinationId) = await AddPostAsync();
        var reset = _clock.UtcNow.AddMinutes(30);
        _bluesky.LimitedUntil = reset;

        await PassAsync();

        var waiting = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Waiting, waiting.State);
        await using (var context = _db.NewContext())
        {
            Assert.Equal(reset, (await context.GetSettingsAsync(Ct)).BlueskyStoppedUntil);
        }

        // Bluesky would answer now, but its limit has not reset: nothing is sent.
        _bluesky.LimitedUntil = null;
        var before = _bluesky.Requests.Count;
        _clock.Advance(TimeSpan.FromMinutes(20));
        await PassAsync();
        Assert.Equal(before, _bluesky.Requests.Count);

        _clock.Advance(TimeSpan.FromMinutes(11));
        await PassAsync();
        Assert.Equal(PostDestinationStates.Posted, (await DestinationAsync(destinationId)).State);
    }

    [Fact]
    public async Task ARefreshSavesTheNewTokens_BeforeTheNextCall()
    {
        await SetUpAsync();
        await AddPostAsync();
        await PassAsync();
        Assert.Equal(1, _bluesky.SignIns);

        // The access token is near its end two hours on: refreshed, not signed in again.
        _clock.Advance(TimeSpan.FromHours(2));
        var (_, second) = await AddPostAsync(text: "Second post");
        await PassAsync();

        Assert.Equal(1, _bluesky.SignIns);
        Assert.Equal(1, _bluesky.Refreshes);

        var refresh = _bluesky.Requests.FindIndex(r => r.Method == "com.atproto.server.refreshSession");
        var put = _bluesky.Requests.FindLastIndex(r => r.Method == "com.atproto.repo.putRecord");
        Assert.True(refresh < put);
        Assert.Equal(_bluesky.AccessJwt, _bluesky.Requests[put].Bearer);
        Assert.Equal(PostDestinationStates.Posted, (await DestinationAsync(second)).State);

        await using var context = _db.NewContext();
        var stored = _session.Read((await context.GetSettingsAsync(Ct)).BlueskySessionEncrypted);
        Assert.Equal(_bluesky.RefreshJwt, stored!.RefreshJwt);
    }

    [Fact]
    public async Task CheckAndTheLoopAtOnce_SignInOnce()
    {
        await SetUpAsync();
        await AddPostAsync();

        await using var checkContext = _db.NewContext();
        await Task.WhenAll(
            PassAsync(),
            _session.AccessAsync(checkContext, prove: true, Ct));

        Assert.Equal(1, _bluesky.SignIns);
    }

    [Fact]
    public async Task ATextOverThreeHundredCharactersIsNotSent()
    {
        await SetUpAsync();
        var (_, destinationId) = await AddPostAsync(title: null, text: new string('a', 301));

        await PassAsync();

        var failed = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.Equal(BlueskyText.TooLong, failed.Error);
        Assert.Equal(0, _bluesky.Puts);
    }

    [Fact]
    public async Task AHandleInTheTextIsNeverAMention()
    {
        await SetUpAsync();
        await AddPostAsync(title: null, text: "Thanks @friend.bsky.social! More at https://example.com/night #movienight");

        await PassAsync();

        var facets = Assert.Single(PutRecords())["record"]!["facets"]!.AsArray();
        var kinds = facets.Select(f => f!["features"]![0]!["$type"]!.GetValue<string>()).ToList();
        Assert.Equal(["app.bsky.richtext.facet#link", "app.bsky.richtext.facet#tag"], kinds);
    }

    [Fact]
    public async Task TheCardCarriesTheSmallPicture_UploadedFirst()
    {
        await SetUpAsync();

        var picture = Guid.CreateVersion7();
        var copy = Guid.CreateVersion7();
        await using (var context = _db.NewContext())
        {
            context.CalendarCoverPictures.Add(new CalendarCoverPicture { Id = picture, Bytes = new byte[4000], ContentType = "image/png", CreatedAt = _clock.UtcNow });
            context.CalendarCoverPictures.Add(new CalendarCoverPicture { Id = copy, Bytes = new byte[900], ContentType = "image/jpeg", CreatedAt = _clock.UtcNow });
            await context.SaveChangesAsync(Ct);
        }

        await AddPostAsync(text: "Tickets at https://example.com/night", shape: (post, destination) =>
        {
            post.PictureId = picture;
            destination.SitePictureId = copy;
            destination.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(picture));
        });

        await PassAsync();

        Assert.Equal([900], _bluesky.Blobs);
        var external = Assert.Single(PutRecords())["record"]!["embed"]!["external"]!;
        Assert.Equal("https://example.com/night", external["uri"]!.GetValue<string>());
        Assert.Equal("Movie night", external["title"]!.GetValue<string>());
        Assert.Equal(900, external["thumb"]!["size"]!.GetValue<int>());

        var upload = _bluesky.Requests.FindIndex(r => r.Method == "com.atproto.repo.uploadBlob");
        var put = _bluesky.Requests.FindIndex(r => r.Method == "com.atproto.repo.putRecord");
        Assert.True(upload < put);
    }

    [Fact]
    public async Task APostForAnotherAccountIsNotSent()
    {
        await SetUpAsync();
        var (_, destinationId) = await AddPostAsync(target: "did:plc:someoneelseentirely000");

        await PassAsync();

        var failed = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.Equal(PostBlueskySender.AccountChanged, failed.Error);
        Assert.Equal(0, _bluesky.Puts);
    }

    [Fact]
    public async Task APostMoreThanAnHourLateIsNotSent()
    {
        await SetUpAsync();
        var (_, destinationId) = await AddPostAsync();

        _clock.Advance(TimeSpan.FromMinutes(61));
        await PassAsync();

        var failed = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.Equal(PostRules.NotSentOnTime, failed.Error);
        Assert.Equal(0, _bluesky.Puts);
    }

    [Fact]
    public async Task NothingGoesWhilePostingIsOff_OrPaused()
    {
        await SetUpAsync(s => s.BlueskyPostingOn = false);
        var (_, destinationId) = await AddPostAsync();

        await PassAsync();
        Assert.Equal(PostDestinationStates.Waiting, (await DestinationAsync(destinationId)).State);

        await SetUpAsync(s => s.PostsPaused = true);
        await AddPostAsync();
        await PassAsync();

        Assert.Empty(_bluesky.Requests);
    }

    [Fact]
    public async Task OnePostAPass()
    {
        await SetUpAsync();
        await AddPostAsync(text: "One");
        await AddPostAsync(text: "Two");

        var pass = await PassAsync();

        Assert.Equal(1, pass.Sent);
        Assert.Equal(1, _bluesky.Puts);
    }

    /// <summary>The app password and session are stored as they are: the protector's own tests cover the encryption.</summary>
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string? Unprotect(string? ciphertext) => ciphertext;
    }
}
