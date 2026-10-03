using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Posts;

/// <summary>
/// The Marketing tab's posts (posts design §4.7): who may see and change them, what scheduling
/// saves, that a change during a send is refused, that the preview is what the sender sends,
/// Cancel, and edit and delete on the site with their facts.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PostEndpointTests(PostgresFixture db)
{
    private const string Guild = "111111111111111111";
    private const string Channel = "222222222222222222";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const ModbotPermissions Manager = ModbotPermissions.ViewPosts | ModbotPermissions.ManagePosts;

    /// <summary>Stands in for the bot: records what was asked of Discord and answers as told.</summary>
    private sealed class FakeDiscordPosts : IDiscordPostActions
    {
        public List<(string ChannelId, string MessageId, string Text)> Edits { get; } = [];

        public List<(string ChannelId, string MessageId)> Deletes { get; } = [];

        public PostSiteOutcome Answer { get; set; } = PostSiteOutcome.Ok;

        public Task<PostSiteOutcome> EditAsync(string channelId, string messageId, string text, CancellationToken ct = default)
        {
            Edits.Add((channelId, messageId, text));
            return Task.FromResult(Answer);
        }

        public Task<PostSiteOutcome> DeleteAsync(string channelId, string messageId, string reason, CancellationToken ct = default)
        {
            Deletes.Add((channelId, messageId));
            return Task.FromResult(Answer);
        }

        public Task<PostSiteOutcome> PublishAsync(string channelId, string messageId, CancellationToken ct = default)
            => Task.FromResult(Answer);
    }

    private async Task<(ApiTestHost Host, FakeDiscordPosts Discord)> StartAsync()
    {
        await using (var context = db.NewContext())
        {
            await context.Posts.ExecuteDeleteAsync(Ct);
            var settings = await context.GetSettingsAsync(Ct);
            settings.DiscordGuildId = Guild;
            settings.PostsPaused = false;
            settings.DiscordPostsOn = true;
            await context.SaveChangesAsync(Ct);
        }

        var discord = new FakeDiscordPosts();
        var host = await ApiTestHost.StartAsync(db, configure: s => s.AddSingleton<IDiscordPostActions>(discord));
        return (host, discord);
    }

    private static object Body(ApiTestHost host, bool draft = false, string when = "later", int? version = null, object? discord = null) => new
    {
        title = "Movie night",
        text = "Friday at eight. Bring snacks.",
        when,
        sendAt = host.Clock.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        timeZone = "UTC",
        draft,
        version,
        discord = discord ?? new { channelId = Channel },
    };

    private static async Task<JsonElement> CreateAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private async Task ChangeAsync(Guid postId, Action<Post, PostDestination> change)
    {
        await using var context = db.NewContext();
        var post = await context.Posts.Include(p => p.Destinations).SingleAsync(p => p.Id == postId, Ct);
        change(post, post.Destinations.Single());
        await context.SaveChangesAsync(Ct);
    }

    private async Task<PostDestination> DestinationAsync(Guid postId)
    {
        await using var context = db.NewContext();
        return await context.PostDestinations.AsNoTracking().SingleAsync(d => d.PostId == postId, Ct);
    }

    [Fact]
    public async Task SeeingPostsNeedsSeePosts()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, nobody) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewPosts, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/posts", null, nobody, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, "/api/posts", null, viewer, Ct)).StatusCode);
    }

    [Fact]
    public async Task WritingAPostNeedsManagePosts()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewPosts | ModbotPermissions.ManageCalendar | ModbotPermissions.ManageGroupPosts, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", Body(host), viewer, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SchedulingSavesAWaitingDiscordDestination_AndAFact()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var post = await CreateAsync(host, manager, Body(host));

        Assert.Equal(PostStatuses.Scheduled, post.GetProperty("status").GetString());
        Assert.Equal("scheduled", post.GetProperty("lists")[0].GetString());
        var destination = post.GetProperty("destinations")[0];
        Assert.Equal(PostNetworks.Discord, destination.GetProperty("network").GetString());
        Assert.Equal(PostDestinationStates.Waiting, destination.GetProperty("state").GetString());
        Assert.Equal(Channel, destination.GetProperty("target").GetString());

        Assert.Single(await host.FactsAsync(FactType.PostCreated, post.GetProperty("id").GetString()!, Ct));
    }

    [Fact]
    public async Task NoSiteIsTickedUnlessSent()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", new
        {
            text = "Friday at eight.",
            when = "now",
            timeZone = "UTC",
        }, manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problems = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("problems");
        Assert.Contains(problems.EnumerateArray(), p => p.GetString() == "Pick where it goes.");
    }

    [Fact]
    public async Task EveryProblemIsSaidAtOnce()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", new
        {
            text = new string('a', 2001),
            when = "later",
            sendAt = host.Clock.UtcNow.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            timeZone = "UTC",
            discord = new { channelId = (string?)null },
        }, manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problems = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("problems").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("That time has passed.", problems);
        Assert.Contains("Pick a channel.", problems);
        Assert.Contains("The Discord text is longer than 2000 characters.", problems);
    }

    [Fact]
    public async Task OnlyAnAnnouncementChannelCanPublish()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        await using (var context = db.NewContext())
        {
            await context.DiscordChannels.Where(c => c.ChannelId == Channel).ExecuteDeleteAsync(Ct);
            context.DiscordChannels.Add(new DiscordChannel
            {
                ChannelId = Channel,
                GuildId = Guild,
                Name = "general",
                Type = DiscordChannelTypes.Text,
                FirstSeenAt = host.Clock.UtcNow,
                UpdatedAt = host.Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", Body(host, discord: new { channelId = Channel, publish = true }), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Only an Announcement channel can publish to followers.", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
    }

    /// <summary>The preview is built by the code the sender uses: what it shows is what goes out.</summary>
    [Fact]
    public async Task ThePreviewIsWhatTheSenderSends()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var body = Body(host, discord: new { channelId = Channel, text = "Popcorn provided." });

        var preview = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, "/api/posts/preview", body, manager, Ct), Ct);
        var created = await CreateAsync(host, manager, body);

        await using var context = db.NewContext();
        var post = await context.Posts.AsNoTracking().Include(p => p.Destinations)
            .SingleAsync(p => p.Id == created.GetProperty("id").GetGuid(), Ct);

        var content = preview.GetProperty("discord").GetProperty("content").GetString();
        Assert.Equal(PostTexts.Discord(post, post.Destinations[0]), content);
        Assert.Equal("**Movie night**\nPopcorn provided.", content);
        Assert.Empty(preview.GetProperty("problems").EnumerateArray());
    }

    [Fact]
    public async Task AChangeWhileTheSendIsUnderWayIsRefused()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var created = await CreateAsync(host, manager, Body(host));
        var id = created.GetProperty("id").GetGuid();

        await ChangeAsync(id, (p, d) =>
        {
            d.State = PostDestinationStates.Sending;
            p.Version++;
        });

        var response = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/posts/{id}", Body(host, version: created.GetProperty("version").GetInt32()), manager, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(PostRules.BeingSent, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AChangeBeforeTheTimeIsSaved_WithAFactSayingWhatChanged()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var created = await CreateAsync(host, manager, Body(host));
        var id = created.GetProperty("id").GetString()!;

        var response = await host.SendJsonAsync(HttpMethod.Put, $"/api/posts/{id}", new
        {
            title = "Film night",
            text = "Friday at eight. Bring snacks.",
            when = "later",
            sendAt = host.Clock.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            timeZone = "UTC",
            version = created.GetProperty("version").GetInt32(),
            discord = new { channelId = Channel },
        }, manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = ApiTestHost.DataOf(Assert.Single(await host.FactsAsync(FactType.PostChanged, id, Ct))).GetProperty("changed");
        Assert.Equal("Film night", changed.GetProperty("title").GetProperty("new").GetString());
    }

    [Fact]
    public async Task CancelSkipsTheWaitingSites()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var id = (await CreateAsync(host, manager, Body(host))).GetProperty("id").GetGuid();

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/posts/{id}/cancel", null, manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PostDestinationStates.Skipped, (await DestinationAsync(id)).State);
        Assert.Single(await host.FactsAsync(FactType.PostCancelled, id.ToString(), Ct));
    }

    [Fact]
    public async Task OnlyADraftIsDeleted()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var scheduled = (await CreateAsync(host, manager, Body(host))).GetProperty("id").GetGuid();
        var draft = (await CreateAsync(host, manager, Body(host, draft: true))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Conflict, (await host.SendJsonAsync(HttpMethod.Delete, $"/api/posts/{scheduled}", null, manager, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Delete, $"/api/posts/{draft}", null, manager, Ct)).StatusCode);
    }

    [Fact]
    public async Task TryAgainLooksFirst_WhenDiscordMayHaveThePost()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var id = (await CreateAsync(host, manager, Body(host))).GetProperty("id").GetGuid();

        await ChangeAsync(id, (p, d) =>
        {
            d.State = PostDestinationStates.Failed;
            d.MayBeSent = true;
            d.Error = "Discord did not take the post.";
            p.Version++;
        });

        var destination = await DestinationAsync(id);
        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/posts/{id}/destinations/{destination.Id}/try-again", null, manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await DestinationAsync(id);
        Assert.Equal(PostDestinationStates.Checking, after.State);
        Assert.True(after.SendIfMissing);
    }

    [Fact]
    public async Task AnEditOnDiscordChangesTheMessage_AndWritesAFact()
    {
        var (host, discord) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var id = (await CreateAsync(host, manager, Body(host))).GetProperty("id").GetGuid();

        await ChangeAsync(id, (p, d) =>
        {
            d.State = PostDestinationStates.Posted;
            d.ExternalId = "555";
            d.SentText = "**Movie night**\nFriday at eight. Bring snacks.";
        });

        var destination = await DestinationAsync(id);
        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            $"/api/posts/{id}/destinations/{destination.Id}/edit",
            new { title = "Movie night", text = "Now at nine." },
            manager,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((Channel, "555", "**Movie night**\nNow at nine."), Assert.Single(discord.Edits));
        Assert.Equal("**Movie night**\nNow at nine.", (await DestinationAsync(id)).SentText);

        var fact = ApiTestHost.DataOf(Assert.Single(await host.FactsAsync(FactType.PostEdited, id.ToString(), Ct)));
        Assert.Equal("**Movie night**\nFriday at eight. Bring snacks.", fact.GetProperty("before").GetString());
    }

    [Fact]
    public async Task DeletingOnDiscordMarksItDeleted_AndWritesAFact()
    {
        var (host, discord) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var id = (await CreateAsync(host, manager, Body(host))).GetProperty("id").GetGuid();

        await ChangeAsync(id, (_, d) =>
        {
            d.State = PostDestinationStates.Posted;
            d.ExternalId = "555";
        });

        var destination = await DestinationAsync(id);
        var response = await host.SendJsonAsync(HttpMethod.Delete, $"/api/posts/{id}/destinations/{destination.Id}", null, manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((Channel, "555"), Assert.Single(discord.Deletes));
        Assert.Equal(PostDestinationStates.Removed, (await DestinationAsync(id)).State);
        Assert.Single(await host.FactsAsync(FactType.PostRemoved, id.ToString(), Ct));
    }

    [Fact]
    public async Task WithTheBotOffline_EditSaysSo_AndChangesNothing()
    {
        var (host, discord) = await StartAsync();
        await using var _host = host;
        discord.Answer = PostSiteOutcome.Offline;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var id = (await CreateAsync(host, manager, Body(host))).GetProperty("id").GetGuid();

        await ChangeAsync(id, (_, d) =>
        {
            d.State = PostDestinationStates.Posted;
            d.ExternalId = "555";
            d.SentText = "**Movie night**\nFriday at eight. Bring snacks.";
        });

        var destination = await DestinationAsync(id);
        var response = await host.SendJsonAsync(
            HttpMethod.Post, $"/api/posts/{id}/destinations/{destination.Id}/edit", new { text = "Now at nine." }, manager, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("**Movie night**\nFriday at eight. Bring snacks.", (await DestinationAsync(id)).SentText);
    }

    [Fact]
    public async Task ThePostsSwitchesNeedChangeSettings_AndAreRecorded()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var (_, settings) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, "/api/settings/posts", new { paused = true }, manager, Ct)).StatusCode);

        var response = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/posts", new { paused = true }, settings, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ApiTestHost.BodyOf(response, Ct)).GetProperty("paused").GetBoolean());

        var fact = ApiTestHost.DataOf((await host.FactsAsync(FactType.SettingsChanged, "settings", Ct))[0]);
        Assert.Equal("posts", fact.GetProperty("setting").GetString());
        Assert.True(fact.GetProperty("changed").GetProperty("paused").GetProperty("new").GetBoolean());
    }

    [Fact]
    public async Task APausedSiteIsShownAsPaused()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        await CreateAsync(host, manager, Body(host));

        await using (var context = db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.PostsPaused = true;
            await context.SaveChangesAsync(Ct);
        }

        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/posts?list=scheduled", null, manager, Ct), Ct);

        Assert.True(list.GetProperty("sites").GetProperty("paused").GetBoolean());
        Assert.Equal(PostHolds.Paused, list.GetProperty("posts")[0].GetProperty("destinations")[0].GetProperty("shown").GetString());
        Assert.Equal(1, list.GetProperty("counts").GetProperty("scheduled").GetInt32());
    }
}
