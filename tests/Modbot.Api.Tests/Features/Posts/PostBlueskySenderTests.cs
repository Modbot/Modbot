using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Posts;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Net;
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
        settings.BlueskyOAuthSignedIn = false;
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

    private async Task<PostBlueskyPass> PassAsync(VideoLinks? videoLinks = null)
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
            new BlueskyIdentity(new OneHandlerClients(_bluesky)),
            videoLinks: videoLinks);

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

    /// <summary>A JPEG that is only a header: enough to read its size from, padded to <paramref name="length"/> bytes.</summary>
    private static byte[] Jpeg(int width, int height, int length)
    {
        byte[] head = [0xFF, 0xD8, 0xFF, 0xC0, 0, 11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 0, 0];
        var bytes = new byte[length];
        head.CopyTo(bytes, 0);
        return bytes;
    }

    private async Task<(Guid Picture, Guid Copy)> AddPicturesAsync(byte[] copyBytes)
    {
        var picture = Guid.CreateVersion7();
        var copy = Guid.CreateVersion7();
        await using var context = _db.NewContext();
        context.CalendarCoverPictures.Add(new CalendarCoverPicture { Id = picture, Bytes = new byte[4000], ContentType = "image/png", CreatedAt = _clock.UtcNow });
        context.CalendarCoverPictures.Add(new CalendarCoverPicture { Id = copy, Bytes = copyBytes, ContentType = "image/jpeg", CreatedAt = _clock.UtcNow });
        await context.SaveChangesAsync(Ct);
        return (picture, copy);
    }

    [Fact]
    public async Task APostWithAPictureCarriesItInFull_UploadedFirst_WithItsTitleAndShape_AndNoCard()
    {
        await SetUpAsync();
        var (picture, copy) = await AddPicturesAsync(Jpeg(2000, 800, 900));

        await AddPostAsync(text: "Tickets at https://example.com/night", shape: (post, destination) =>
        {
            post.PictureId = picture;
            destination.SitePictureId = copy;
            destination.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(picture));
        });

        await PassAsync();

        Assert.Equal([900], _bluesky.Blobs);
        var record = Assert.Single(PutRecords())["record"]!;
        var embed = record["embed"]!;
        Assert.Equal("app.bsky.embed.images", embed["$type"]!.GetValue<string>());
        Assert.Null(embed["external"]);

        var image = Assert.Single(embed["images"]!.AsArray())!;
        Assert.Equal("Movie night", image["alt"]!.GetValue<string>());
        Assert.Equal(900, image["image"]!["size"]!.GetValue<int>());
        Assert.Equal(2000, image["aspectRatio"]!["width"]!.GetValue<int>());
        Assert.Equal(800, image["aspectRatio"]!["height"]!.GetValue<int>());

        // The link stays clickable in the text.
        Assert.Single(record["facets"]!.AsArray());

        var upload = _bluesky.Requests.FindIndex(r => r.Method == "com.atproto.repo.uploadBlob");
        var put = _bluesky.Requests.FindIndex(r => r.Method == "com.atproto.repo.putRecord");
        Assert.True(upload < put);
    }

    [Fact]
    public async Task APostWithAPictureAndNoLinkStillCarriesThePicture()
    {
        await SetUpAsync();
        var (picture, copy) = await AddPicturesAsync(Jpeg(1000, 1000, 700));

        await AddPostAsync(text: "No link in this one", shape: (post, destination) =>
        {
            post.PictureId = picture;
            destination.SitePictureId = copy;
            destination.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(picture));
        });

        await PassAsync();

        var embed = Assert.Single(PutRecords())["record"]!["embed"]!;
        Assert.Equal("app.bsky.embed.images", embed["$type"]!.GetValue<string>());
    }

    [Fact]
    public async Task ACalendarCoverGoesAsItsSmallBlueskyCopy_WhenItHasOne()
    {
        await SetUpAsync();

        var cover = Guid.CreateVersion7();
        await using (var context = _db.NewContext())
        {
            context.CalendarCoverPictures.Add(new CalendarCoverPicture
            {
                Id = cover,
                Bytes = new byte[3_000_000],
                ContentType = "image/png",
                BlueskyBytes = Jpeg(2000, 800, 800_000),
                CreatedAt = _clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        await AddPostAsync(text: "Tickets at https://example.com/night", shape: (post, destination) =>
        {
            post.PictureId = cover;
            destination.SitePictureId = cover;
            destination.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(cover));
        });

        await PassAsync();

        Assert.Equal([800_000], _bluesky.Blobs);
        var embed = Assert.Single(PutRecords())["record"]!["embed"]!;
        Assert.Equal("app.bsky.embed.images", embed["$type"]!.GetValue<string>());
    }

    [Fact]
    public async Task ACoverOverOneMegabyteWithNoCopyKeepsTheLinkCardAndUploadsNothing()
    {
        await SetUpAsync();

        var cover = Guid.CreateVersion7();
        await using (var context = _db.NewContext())
        {
            context.CalendarCoverPictures.Add(new CalendarCoverPicture { Id = cover, Bytes = new byte[3_000_000], ContentType = "image/png", CreatedAt = _clock.UtcNow });
            await context.SaveChangesAsync(Ct);
        }

        await AddPostAsync(text: "Tickets at https://example.com/night", shape: (post, destination) =>
        {
            post.PictureId = cover;
            destination.SitePictureId = cover;
            destination.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(cover));
        });

        await PassAsync();

        Assert.Empty(_bluesky.Blobs);
        var external = Assert.Single(PutRecords())["record"]!["embed"]!["external"]!;
        Assert.Equal("https://example.com/night", external["uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task APostWithNoPictureKeepsItsLinkCard()
    {
        await SetUpAsync();
        await AddPostAsync(text: "Tickets at https://example.com/night");

        await PassAsync();

        Assert.Empty(_bluesky.Blobs);
        var embed = Assert.Single(PutRecords())["record"]!["embed"]!;
        Assert.Equal("app.bsky.embed.external", embed["$type"]!.GetValue<string>());
        Assert.Equal("Movie night", embed["external"]!["title"]!.GetValue<string>());
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

    private const string ParentUri = "at://did:plc:ewvi7nxzyoun6zhxrhs64oiz/app.bsky.feed.post/3l4pqjzzyde2a";

    private Task<(Guid PostId, Guid DestinationId)> AddParentAsync(string state = PostDestinationStates.Posted) =>
        AddPostAsync(shape: (_, d) =>
        {
            d.State = state;
            d.ExternalId = ParentUri;
            d.ClientKey = "3l4pqjzzyde2a";
            d.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(Cid: "bafy-parent"));
        });

    [Fact]
    public async Task AReplyGoesUnderThePostItAnswers()
    {
        await SetUpAsync();
        var (_, parentId) = await AddParentAsync();
        var (_, replyId) = await AddPostAsync(shape: (_, d) => d.ReplyToId = parentId);

        var pass = await PassAsync();

        Assert.Equal(1, pass.Sent);
        var reply = Assert.Single(PutRecords())["record"]!["reply"]!;
        Assert.Equal(ParentUri, reply["root"]!["uri"]!.GetValue<string>());
        Assert.Equal("bafy-parent", reply["root"]!["cid"]!.GetValue<string>());
        Assert.Equal(ParentUri, reply["parent"]!["uri"]!.GetValue<string>());
        Assert.Equal("bafy-parent", reply["parent"]!["cid"]!.GetValue<string>());
        Assert.Equal(PostDestinationStates.Posted, (await DestinationAsync(replyId)).State);
    }

    [Fact]
    public async Task AReplyWhoseParentIsGoneIsNotSent()
    {
        await SetUpAsync();
        var (_, parentId) = await AddParentAsync(PostDestinationStates.Removed);
        var (_, replyId) = await AddPostAsync(shape: (_, d) => d.ReplyToId = parentId);

        await PassAsync();

        var failed = await DestinationAsync(replyId);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.Equal(PostBlueskySender.ReplyParentGone, failed.Error);
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

    private const string Threadgate = "app.bsky.feed.threadgate";
    private const string Postgate = "app.bsky.feed.postgate";

    [Fact]
    public async Task APostPutsItsThreadgateAndPostgateAfterIt_AndItsLanguagesOnThePost()
    {
        await SetUpAsync();
        var (_, destinationId) = await AddPostAsync(shape: (_, d) => d.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(
            WhoCanReply: ["followers", "mentioned"], QuotesOff: true, Languages: ["en", "de"])));

        var pass = await PassAsync();

        Assert.Equal(1, pass.Sent);
        var puts = PutRecords();
        Assert.Equal(
            [BlueskyClient.PostCollection, Threadgate, Postgate],
            puts.Select(p => p["collection"]!.GetValue<string>()));

        var key = puts[0]["rkey"]!.GetValue<string>();
        Assert.All(puts, p => Assert.Equal(key, p["rkey"]!.GetValue<string>()));
        Assert.All(puts, p => Assert.Null(p["swapRecord"]));
        Assert.Equal(["en", "de"], puts[0]["record"]!["langs"]!.AsArray().Select(l => l!.GetValue<string>()));

        var uri = $"at://{FakeBluesky.Did}/app.bsky.feed.post/{key}";
        var threadgate = puts[1]["record"]!;
        Assert.Equal(uri, threadgate["post"]!.GetValue<string>());
        Assert.Equal(
            ["app.bsky.feed.threadgate#followerRule", "app.bsky.feed.threadgate#mentionRule"],
            threadgate["allow"]!.AsArray().Select(r => r!["$type"]!.GetValue<string>()).Order());

        var postgate = puts[2]["record"]!;
        Assert.Equal(uri, postgate["post"]!.GetValue<string>());
        Assert.Equal("app.bsky.feed.postgate#disableRule", postgate["embeddingRules"]![0]!["$type"]!.GetValue<string>());

        // The post is counted alone; each gate sits in its own collection at the post's key.
        Assert.Equal(1, _bluesky.Puts);
        Assert.Single(_bluesky.Posts);
        Assert.Equal([(Postgate, key), (Threadgate, key)], _bluesky.Gates.Keys.Order());
        Assert.False(PostTexts.BlueskyOptionsOf(await DestinationAsync(destinationId)).SettingsToSend);
    }

    [Fact]
    public async Task APostForAnyone_WithQuotesAllowed_PutsNoGates()
    {
        await SetUpAsync();
        await AddPostAsync(shape: (_, d) => d.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(Languages: ["en"])));

        await PassAsync();

        Assert.Single(PutRecords());
        Assert.Empty(_bluesky.Gates);
    }

    [Fact]
    public async Task AGateThatFailsLeavesTheSettingToSend_AndALaterPassMakesIt_WithoutSendingThePostAgain()
    {
        await SetUpAsync();
        var (_, destinationId) = await AddPostAsync(shape: (_, d) => d.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(
            WhoCanReply: ["nobody"], QuotesOff: true)));

        var refusedOnce = false;
        var gateTries = 0;
        _bluesky.GatePutAnswer = (_, _) =>
        {
            gateTries++;

            if (refusedOnce)
                return null;

            refusedOnce = true;
            return FakeBluesky.Json(HttpStatusCode.BadGateway, FakeBluesky.Error("UpstreamFailure", "Upstream Failure"));
        };

        var first = await PassAsync();

        Assert.Equal(1, first.Sent);
        var waiting = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Posted, waiting.State);
        Assert.True(PostTexts.BlueskyOptionsOf(waiting).SettingsToSend);
        Assert.NotNull(waiting.Error);
        Assert.Equal(_clock.UtcNow + PostRules.NotSentRetryAfter, waiting.CheckAt);
        Assert.Empty(_bluesky.Gates);

        // Not tried again before its wait is over.
        _clock.Advance(TimeSpan.FromSeconds(30));
        await PassAsync();

        Assert.Equal(1, gateTries);
        Assert.True(PostTexts.BlueskyOptionsOf(await DestinationAsync(destinationId)).SettingsToSend);

        _clock.Advance(PostRules.NotSentRetryAfter);
        await PassAsync();

        var done = await DestinationAsync(destinationId);
        Assert.Null(done.CheckAt);
        Assert.False(PostTexts.BlueskyOptionsOf(done).SettingsToSend);
        Assert.Null(done.Error);
        Assert.Equal(2, _bluesky.Gates.Count);
        Assert.Equal(1, _bluesky.Puts);
        Assert.Single(_bluesky.Posts);
    }

    [Fact]
    public async Task PostsOfAnotherAccountWithSettingsToSend_DoNotTakeThePlaceOfTheSignedInAccountsOnes()
    {
        await SetUpAsync();

        // More of them than a pass takes, and older than the signed-in account's post.
        for (var i = 0; i <= PostBlueskySender.LooksPerPass; i++)
        {
            var age = i;
            await AddPostAsync(target: "did:plc:someoneelseentirely000", shape: (_, d) => PostedWithSettingsToSend(d, age + 10));
        }

        var (_, ownId) = await AddPostAsync(shape: (_, d) => PostedWithSettingsToSend(d, 1));

        await PassAsync();

        Assert.False(PostTexts.BlueskyOptionsOf(await DestinationAsync(ownId)).SettingsToSend);
        Assert.Equal(2, _bluesky.Gates.Count);
    }

    private void PostedWithSettingsToSend(PostDestination destination, int minutesAgo)
    {
        var key = $"3k{Guid.NewGuid():N}"[..13];

        destination.State = PostDestinationStates.Posted;
        destination.ClientKey = key;
        destination.ExternalId = $"at://{destination.Target}/app.bsky.feed.post/{key}";
        destination.PostedAt = _clock.UtcNow.AddMinutes(-minutesAgo);
        destination.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(WhoCanReply: ["nobody"], QuotesOff: true, SettingsToSend: true));
    }

    [Fact]
    public async Task ASignInWithoutTheGateScopesWaitsWithSignInAgain_AndPutsNothing()
    {
        // A sign-in with Bluesky made before the two gate collections were asked for.
        var oldScope = "atproto repo:app.bsky.feed.post?action=create&action=update&action=delete blob:image/*";
        var grant = new BlueskyOAuthGrant(
            FakeBluesky.AuthServer, FakeBluesky.TokenEndpoint, "https://modbot.example/client.json", BlueskyKeys.NewPrivateJwk(), _clock.UtcNow.AddHours(1), oldScope);
        var tokens = new BlueskyTokens("access", "refresh", FakeBluesky.Did, FakeBluesky.Handle, grant);

        await SetUpAsync(s =>
        {
            s.BlueskyOAuthSignedIn = true;
            s.BlueskySessionEncrypted = _session.Protect(tokens);
        });

        var (_, destinationId) = await AddPostAsync(shape: (_, d) => d.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(
            WhoCanReply: ["followers"])));

        var pass = await PassAsync();

        Assert.Equal(0, pass.Sent);
        var destination = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Waiting, destination.State);
        Assert.Equal(PostBlueskySender.SignInAgain, destination.Error);
        Assert.Empty(PutRecords());
        Assert.Empty(_bluesky.Gates);
    }

    // --- Video ---

    /// <summary>An MP4 that is only a header: a file-type box and a track header of the size given, padded to <paramref name="length"/> bytes.</summary>
    private static byte[] Mp4(int width, int height, int length)
    {
        static byte[] Box(string type, params byte[][] parts)
        {
            var body = parts.SelectMany(p => p).ToArray();
            var box = new byte[8 + body.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
            body.CopyTo(box, 8);
            return box;
        }

        var track = new byte[84];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(track.AsSpan(40), 0x10000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(track.AsSpan(56), 0x10000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(track.AsSpan(76), (uint)width << 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(track.AsSpan(80), (uint)height << 16);

        var head = Box("ftyp", "isom"u8.ToArray(), new byte[4]).Concat(Box("moov", Box("trak", Box("tkhd", track)))).ToArray();
        var bytes = new byte[Math.Max(length, head.Length)];
        head.CopyTo(bytes, 0);
        return bytes;
    }

    private async Task<Guid> AddVideoAsync(byte[] bytes)
    {
        var id = Guid.CreateVersion7();
        await using var context = _db.NewContext();
        context.CalendarBlueskyVideos.Add(new CalendarBlueskyVideo { Id = id, Bytes = bytes, ContentType = "video/mp4", CreatedAt = _clock.UtcNow });
        await context.SaveChangesAsync(Ct);
        return id;
    }

    /// <summary>Answers every link with one response and counts the asks.</summary>
    private sealed class LinkHandler(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(answer());
        }
    }

    private static HttpResponseMessage VideoAnswer(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public async Task APostWithAVideoFileCarriesItInFull_UploadedFirst_WithItsAltAndShape_AndNoCardOrPictures()
    {
        await SetUpAsync();
        var video = Mp4(1280, 720, 3000);
        var file = await AddVideoAsync(video);
        var (picture, copy) = await AddPicturesAsync(Jpeg(2000, 800, 900));

        var (_, destinationId) = await AddPostAsync(text: "Tickets at https://example.com/night", shape: (post, destination) =>
        {
            post.PictureId = picture;
            destination.SitePictureId = copy;
            destination.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(
                picture, Pictures: [new BlueskyPostPicture(picture, "A poster")], Video: new BlueskyPostVideo(file, null, "The trailer")));
        });

        await PassAsync();

        Assert.Equal([video.Length], _bluesky.Blobs);
        var record = Assert.Single(PutRecords())["record"]!;
        var embed = record["embed"]!;
        Assert.Equal("app.bsky.embed.video", embed["$type"]!.GetValue<string>());
        Assert.Null(embed["external"]);
        Assert.Null(embed["images"]);
        Assert.Equal("video/mp4", embed["video"]!["mimeType"]!.GetValue<string>());
        Assert.Equal(video.Length, embed["video"]!["size"]!.GetValue<int>());
        Assert.Equal("The trailer", embed["alt"]!.GetValue<string>());
        Assert.Equal(1280, embed["aspectRatio"]!["width"]!.GetValue<int>());
        Assert.Equal(720, embed["aspectRatio"]!["height"]!.GetValue<int>());
        Assert.Single(record["facets"]!.AsArray());

        var destinationAfter = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Posted, destinationAfter.State);
        Assert.Null(destinationAfter.Error);
    }

    [Fact]
    public async Task AVideoLinkIsFetchedWhenThePostGoesOut_AndCarriedInFull()
    {
        await SetUpAsync();
        var video = Mp4(640, 360, 2000);
        var handler = new LinkHandler(() => VideoAnswer(video));

        await AddPostAsync(shape: (_, destination) => destination.Options = PostTexts.WriteBlueskyOptions(
            new BlueskyPostOptions(Video: new BlueskyPostVideo(null, "https://videos.example/trailer.mp4", "The trailer"))));

        await PassAsync(new VideoLinks(handler));

        Assert.Equal(1, handler.Asked);
        Assert.Equal([video.Length], _bluesky.Blobs);
        var embed = Assert.Single(PutRecords())["record"]!["embed"]!;
        Assert.Equal("app.bsky.embed.video", embed["$type"]!.GetValue<string>());
        Assert.Equal(640, embed["aspectRatio"]!["width"]!.GetValue<int>());
    }

    [Fact]
    public async Task AVideoLinkThatFailsPostsWithoutTheVideo_AndTheEventSaysWhy()
    {
        await SetUpAsync();
        var handler = new LinkHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound));

        var (_, destinationId) = await AddPostAsync(text: "Tickets at https://example.com/night", shape: (_, destination) =>
            destination.Options = PostTexts.WriteBlueskyOptions(
                new BlueskyPostOptions(Video: new BlueskyPostVideo(null, "https://videos.example/gone.mp4", "The trailer"))));

        var pass = await PassAsync(new VideoLinks(handler));

        Assert.Equal(1, pass.Sent);
        Assert.Empty(_bluesky.Blobs);
        var embed = Assert.Single(PutRecords())["record"]!["embed"]!;
        Assert.NotEqual("app.bsky.embed.video", embed["$type"]!.GetValue<string>());

        var destination = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Posted, destination.State);
        Assert.Equal($"{Modbot.Core.Calendar.CalendarBlueskyText.VideoNotSent} {VideoLinks.Unreachable}", destination.Error);
        Assert.Null(destination.CheckAt);
    }

    [Fact]
    public async Task AVideoFileThatIsGonePostsWithoutTheVideo_AndTheEventSaysWhy()
    {
        await SetUpAsync();

        var (_, destinationId) = await AddPostAsync(shape: (_, destination) =>
            destination.Options = PostTexts.WriteBlueskyOptions(
                new BlueskyPostOptions(Video: new BlueskyPostVideo(Guid.CreateVersion7(), null, "The trailer"))));

        await PassAsync();

        Assert.Empty(_bluesky.Blobs);
        Assert.Null(Assert.Single(PutRecords())["record"]!["embed"]);

        var destination = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Posted, destination.State);
        Assert.Equal(
            $"{Modbot.Core.Calendar.CalendarBlueskyText.VideoNotSent} {Modbot.Core.Calendar.CalendarBlueskyText.VideoGone}", destination.Error);
    }

    [Fact]
    public async Task ASignInWithoutTheVideoScopePostsWithoutTheVideo_SayingToSignInAgain_AndUploadsNothing()
    {
        // A sign-in with Bluesky made before the narrow scope had blob:video/*.
        var oldScope = "atproto repo:app.bsky.feed.post?action=create&action=update&action=delete blob:image/*";
        var key = BlueskyKeys.NewPrivateJwk();
        var access = _bluesky.GrantAccess(key);
        var grant = new BlueskyOAuthGrant(
            FakeBluesky.AuthServer, FakeBluesky.TokenEndpoint, "https://modbot.example/client.json", key, _clock.UtcNow.AddHours(1), oldScope);
        var tokens = new BlueskyTokens(access, "refresh", FakeBluesky.Did, FakeBluesky.Handle, grant);

        await SetUpAsync(s =>
        {
            s.BlueskyAppPasswordEncrypted = null;
            s.BlueskyOAuthSignedIn = true;
            s.BlueskySessionEncrypted = _session.Protect(tokens);
        });

        var file = await AddVideoAsync(Mp4(1280, 720, 3000));
        var (_, destinationId) = await AddPostAsync(shape: (_, destination) =>
            destination.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(Video: new BlueskyPostVideo(file, null, "The trailer"))));

        var pass = await PassAsync();

        Assert.Equal(1, pass.Sent);
        Assert.Empty(_bluesky.Blobs);

        // A sign-in with Bluesky: the first put is answered with the server's nonce and made once more,
        // so the last one is the post that landed.
        Assert.Null(PutRecords()[^1]["record"]!["embed"]);

        var destination = await DestinationAsync(destinationId);
        Assert.Equal(PostDestinationStates.Posted, destination.State);
        Assert.Equal($"{Modbot.Core.Calendar.CalendarBlueskyText.VideoNotSent} {PostBlueskySender.SignInAgainForVideo}", destination.Error);
    }

    /// <summary>The app password and session are stored as they are: the protector's own tests cover the encryption.</summary>
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string? Unprotect(string? ciphertext) => ciphertext;
    }
}
