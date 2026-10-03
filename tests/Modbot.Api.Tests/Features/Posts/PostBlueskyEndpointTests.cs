using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Bluesky;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Posts;

/// <summary>
/// The Marketing tab's Bluesky posts (posts design §4.2c): the Bluesky section and what it saves,
/// the 300-character limit counted the way Bluesky counts, the preview being what the sender sends,
/// the card picture kept only as a copy of the post's picture, Try again reading back first, Delete
/// on Bluesky (on the account in Settings only), and no Edit.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PostBlueskyEndpointTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const ModbotPermissions Manager = ModbotPermissions.ViewPosts | ModbotPermissions.ManagePosts;

    /// <summary>Stands in for Bluesky's delete: records what was asked and answers as told.</summary>
    private sealed class FakeBlueskyPosts : IBlueskyPostActions
    {
        public List<(string Did, string Key)> Deletes { get; } = [];

        public PostSiteOutcome Answer { get; set; } = PostSiteOutcome.Ok;

        public Task<PostSiteOutcome> DeleteAsync(string did, string recordKey, CancellationToken ct = default)
        {
            Deletes.Add((did, recordKey));
            return Task.FromResult(Answer);
        }
    }

    private async Task<(ApiTestHost Host, FakeBlueskyPosts Bluesky, string Cookie)> StartAsync(bool checkedAccount = true)
    {
        await using (var context = db.NewContext())
        {
            await context.Posts.ExecuteDeleteAsync(Ct);
            var settings = await context.GetSettingsAsync(Ct);
            settings.PostsPaused = false;
            settings.BlueskyHandle = checkedAccount ? FakeBluesky.Handle : null;
            settings.BlueskyDid = checkedAccount ? FakeBluesky.Did : null;
            settings.BlueskyServer = checkedAccount ? FakeBluesky.Server : null;
            settings.BlueskyAppPasswordEncrypted = checkedAccount ? "sealed" : null;
            settings.BlueskyCheckedAt = checkedAccount ? DateTimeOffset.UnixEpoch : null;
            settings.BlueskyDisplayName = checkedAccount ? "Our group" : null;
            settings.BlueskyProblem = null;
            settings.BlueskySignInRefused = false;
            settings.BlueskyStoppedUntil = null;
            settings.BlueskyPostingOn = checkedAccount;
            await context.SaveChangesAsync(Ct);
        }

        var bluesky = new FakeBlueskyPosts();
        var host = await ApiTestHost.StartAsync(db, configure: s => s.AddScoped<IBlueskyPostActions>(_ => bluesky));
        var (_, cookie) = await host.SignedInAsync(Manager, Ct);
        return (host, bluesky, cookie);
    }

    private static object Body(ApiTestHost host, object bluesky, string? title = "Movie night", string text = "Friday at eight. https://example.com/night", Guid? pictureId = null) => new
    {
        title,
        text,
        pictureId,
        when = "later",
        sendAt = host.Clock.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        timeZone = "UTC",
        bluesky,
    };

    private static async Task<JsonElement> CreateAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static async Task<List<string?>> ProblemsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return [.. (await ApiTestHost.BodyOf(response, Ct)).GetProperty("problems").EnumerateArray().Select(p => p.GetString())];
    }

    private async Task<Guid> PictureAsync(int size, string type = "image/jpeg")
    {
        await using var context = db.NewContext();
        var picture = new CalendarCoverPicture { Id = Guid.CreateVersion7(), Bytes = new byte[size], ContentType = type, CreatedAt = DateTimeOffset.UnixEpoch };
        context.CalendarCoverPictures.Add(picture);
        await context.SaveChangesAsync(Ct);
        return picture.Id;
    }

    private async Task PostedAsync(Guid postId, string key = "3l4pqjzzyde2a")
    {
        await using var context = db.NewContext();
        var destination = await context.PostDestinations.SingleAsync(d => d.PostId == postId, Ct);
        destination.State = PostDestinationStates.Posted;
        destination.ClientKey = key;
        destination.ExternalId = BlueskyText.PostUri(destination.Target, key);
        destination.Link = BlueskyText.PostLink(destination.Target, key);
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task SchedulingSavesAWaitingBlueskyDestinationForTheAccount()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var post = await CreateAsync(host, cookie, Body(host, new { }));

        var destination = post.GetProperty("destinations")[0];
        Assert.Equal(PostNetworks.Bluesky, destination.GetProperty("network").GetString());
        Assert.Equal(PostDestinationStates.Waiting, destination.GetProperty("state").GetString());
        Assert.Equal(FakeBluesky.Did, destination.GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Null, destination.GetProperty("textOverride").ValueKind);
    }

    [Fact]
    public async Task WithNoAccountBlueskyCannotBeScheduled_ButADraftCanBeSaved()
    {
        var (host, _, cookie) = await StartAsync(checkedAccount: false);
        await using var running = host;

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", Body(host, new { }), cookie, Ct);
        Assert.Contains("No Bluesky account is set up yet.", await ProblemsAsync(response));

        var draft = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", new { title = "Later", text = "Some words", draft = true, bluesky = new { } }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
    }

    [Fact]
    public async Task MoreThanThreeHundredCharactersIsRefused_CountedAsAReaderSeesThem()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        // 121 family emoji are only 121 characters to a reader, but 3025 bytes: over the byte limit.
        var families = string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", 121));
        var over = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", Body(host, new { text = families }), cookie, Ct);
        Assert.Contains(BlueskyText.TooLong, await ProblemsAsync(over));

        var tooLong = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", Body(host, new { }, title: null, text: new string('a', 301)), cookie, Ct);
        Assert.Contains(BlueskyText.TooLong, await ProblemsAsync(tooLong));

        // 299 thumbs with a skin tone and a letter are 300 characters to a reader: they fit, though
        // they are 1197 UTF-16 units.
        var fits = string.Concat(Enumerable.Repeat("👍🏽", 299)) + "a";
        await CreateAsync(host, cookie, Body(host, new { text = fits }));
    }

    [Fact]
    public async Task ThePreviewIsWhatTheSenderSends()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        var body = Body(host, new { }, text: "Friday #movienight at https://example.com/night with @friend.bsky.social");

        var preview = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, "/api/posts/preview", body, cookie, Ct), Ct);
        var created = await CreateAsync(host, cookie, body);

        await using var context = db.NewContext();
        var post = await context.Posts.AsNoTracking().Include(p => p.Destinations)
            .SingleAsync(p => p.Id == created.GetProperty("id").GetGuid(), Ct);
        var sent = PostTexts.Bluesky(post, post.Destinations[0]);

        var bluesky = preview.GetProperty("bluesky");
        Assert.Equal(sent, bluesky.GetProperty("text").GetString());
        Assert.Equal(BlueskyText.Graphemes(sent), bluesky.GetProperty("graphemes").GetInt32());
        Assert.Equal(300, bluesky.GetProperty("limit").GetInt32());
        Assert.Equal(["text", "tag", "text", "link", "text"], bluesky.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("kind").GetString()));
        Assert.Equal("https://example.com/night", bluesky.GetProperty("card").GetProperty("uri").GetString());
        Assert.Equal("Movie night", bluesky.GetProperty("card").GetProperty("title").GetString());
        Assert.Equal(FakeBluesky.Handle, bluesky.GetProperty("handle").GetString());
        Assert.Equal("Our group", bluesky.GetProperty("displayName").GetString());
        Assert.Empty(preview.GetProperty("problems").EnumerateArray());
    }

    [Fact]
    public async Task TheCardPictureIsKeptOnlyAsACopyOfThePostsPicture()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        var picture = await PictureAsync(5000, "image/png");
        var copy = await PictureAsync(800);
        var other = Guid.CreateVersion7();

        var kept = await CreateAsync(host, cookie, Body(host, new { cardPictureId = copy, cardPictureFrom = picture }, pictureId: picture));
        var dropped = await CreateAsync(host, cookie, Body(host, new { cardPictureId = copy, cardPictureFrom = other }, pictureId: picture));

        Assert.Equal(copy, kept.GetProperty("destinations")[0].GetProperty("bluesky").GetProperty("cardPictureId").GetGuid());
        Assert.Equal(JsonValueKind.Null, dropped.GetProperty("destinations")[0].GetProperty("bluesky").GetProperty("cardPictureId").ValueKind);

        var big = await PictureAsync(BlueskyText.CardPictureMaxBytes + 1);
        var tooBig = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", Body(host, new { cardPictureId = big, cardPictureFrom = picture }, pictureId: picture), cookie, Ct);
        Assert.Contains("The Bluesky picture is larger than 1 MB.", await ProblemsAsync(tooBig));
    }

    [Fact]
    public async Task TryAgainOnAPostBlueskyMayHaveReadsBackFirst()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        var created = await CreateAsync(host, cookie, Body(host, new { }));
        var postId = created.GetProperty("id").GetGuid();

        await using (var context = db.NewContext())
        {
            var destination = await context.PostDestinations.SingleAsync(d => d.PostId == postId, Ct);
            destination.State = PostDestinationStates.Failed;
            destination.MayBeSent = true;
            destination.ClientKey = "3l4pqjzzyde2a";
            destination.Error = "Could not reach Bluesky.";
            await context.SaveChangesAsync(Ct);
        }

        var destinationId = created.GetProperty("destinations")[0].GetProperty("id").GetGuid();
        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/posts/{postId}/destinations/{destinationId}/try-again", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var after = db.NewContext();
        var row = await after.PostDestinations.AsNoTracking().SingleAsync(d => d.Id == destinationId, Ct);
        Assert.Equal(PostDestinationStates.Checking, row.State);
        Assert.True(row.SendIfMissing);
        Assert.Equal("3l4pqjzzyde2a", row.ClientKey);
    }

    [Fact]
    public async Task DeleteOnBlueskyAsksOnce_AndMarksItDeleted()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        var created = await CreateAsync(host, cookie, Body(host, new { }));
        var postId = created.GetProperty("id").GetGuid();
        var destinationId = created.GetProperty("destinations")[0].GetProperty("id").GetGuid();
        await PostedAsync(postId);

        var response = await host.SendJsonAsync(HttpMethod.Delete, $"/api/posts/{postId}/destinations/{destinationId}", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([(FakeBluesky.Did, "3l4pqjzzyde2a")], bluesky.Deletes);
        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal(PostDestinationStates.Removed, body.GetProperty("destinations")[0].GetProperty("state").GetString());
        Assert.Single(await host.FactsAsync(FactType.PostRemoved, postId.ToString(), Ct));
    }

    [Fact]
    public async Task ADeleteBlueskyRefusesIsSaid_AndTheRowStays()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        bluesky.Answer = PostSiteOutcome.Failed(BlueskyPostActions.OtherAccount);
        var created = await CreateAsync(host, cookie, Body(host, new { }));
        var postId = created.GetProperty("id").GetGuid();
        var destinationId = created.GetProperty("destinations")[0].GetProperty("id").GetGuid();
        await PostedAsync(postId);

        var response = await host.SendJsonAsync(HttpMethod.Delete, $"/api/posts/{postId}/destinations/{destinationId}", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(BlueskyPostActions.OtherAccount, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());

        await using var context = db.NewContext();
        Assert.Equal(PostDestinationStates.Posted, (await context.PostDestinations.AsNoTracking().SingleAsync(d => d.Id == destinationId, Ct)).State);
    }

    [Fact]
    public async Task ABlueskyPostCannotBeEdited()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        var created = await CreateAsync(host, cookie, Body(host, new { }));
        var postId = created.GetProperty("id").GetGuid();
        var destinationId = created.GetProperty("destinations")[0].GetProperty("id").GetGuid();
        await PostedAsync(postId);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, $"/api/posts/{postId}/destinations/{destinationId}/edit", new { title = "x", text = "y" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task TheListSaysWhetherBlueskyIsOnAndSetUp()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/posts?list=scheduled", null, cookie, Ct), Ct);

        Assert.True(list.GetProperty("sites").GetProperty("blueskyOn").GetBoolean());
        Assert.True(list.GetProperty("sites").GetProperty("blueskySetUp").GetBoolean());
    }
}
