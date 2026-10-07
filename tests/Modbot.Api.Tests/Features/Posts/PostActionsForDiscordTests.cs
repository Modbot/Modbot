using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Posts;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Posts;
using Modbot.TestSupport;
using StoredSettings = Modbot.Core.Data.Entities.Settings;

namespace Modbot.Api.Tests.Features.Posts;

/// <summary>
/// <c>/post</c>'s side of the API (Discord commands design §3.7, step 8): the preview is the message
/// the Discord sender builds, Post now saves one due post under the confirmation's key, Manage posts
/// is needed to write and See posts to list, nothing that could not go is saved, and the list is in
/// the Marketing tab's order.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PostActionsForDiscordTests(PostgresFixture db)
{
    private const string Guild = "111111111111111111";
    private const string Channel = "222222222222222222";
    private const string OtherServersChannel = "444444444444444444";
    private const string VoiceChannel = "555555555555555555";
    private const string Quiet = "666666666666666666";

    private const ModbotPermissions Host = ModbotPermissions.ViewPosts | ModbotPermissions.ManagePosts;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The bot, connected.</summary>
    private sealed class ConnectedBot : IDiscordBotStatus
    {
        public DateTimeOffset? StartedAt => DateTimeOffset.UnixEpoch;

        public DiscordBotSnapshot Snapshot()
            => new(DiscordBotState.Connected, DateTimeOffset.UnixEpoch, null, null, 0, false, null, 0);
    }

    private async Task<ApiTestHost> StartAsync(Action<StoredSettings>? settings = null, bool connected = true)
    {
        await using (var context = db.NewContext())
        {
            await context.Posts.ExecuteDeleteAsync(Ct);

            var stored = await context.GetSettingsAsync(Ct);
            stored.DiscordGuildId = Guild;
            stored.PostsPaused = false;
            stored.DiscordPostsOn = true;
            settings?.Invoke(stored);

            await context.DiscordChannels
                .Where(c => c.ChannelId == Channel || c.ChannelId == OtherServersChannel || c.ChannelId == VoiceChannel || c.ChannelId == Quiet)
                .ExecuteDeleteAsync(Ct);

            context.DiscordChannels.Add(Listed(Channel, Guild, "announcements"));
            context.DiscordChannels.Add(Listed(Quiet, Guild, "quiet"));
            context.DiscordChannels.Add(Listed(OtherServersChannel, "999999999999999999", "elsewhere"));
            context.DiscordChannels.Add(Listed(VoiceChannel, Guild, "lounge", DiscordChannelTypes.Voice));
            await context.SaveChangesAsync(Ct);
        }

        return await ApiTestHost.StartAsync(db, configure: services =>
        {
            if (connected)
                services.AddSingleton<IDiscordBotStatus>(new ConnectedBot());
        });
    }

    private static DiscordChannel Listed(string channelId, string guildId, string name, string type = DiscordChannelTypes.Text) => new()
    {
        ChannelId = channelId,
        GuildId = guildId,
        Name = name,
        Type = type,
        FirstSeenAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static StaffMember Staff(ModbotUser user, ModbotPermissions held = Host) => new(user.Id, user.Username, held);

    private static IPostActions Actions(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IPostActions>();

    private static PostDraft Draft(string? title = "Movie night", string text = "Friday at eight. Bring snacks.", string channel = Channel)
        => new(title, text, channel);

    // ── The preview is what is sent ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePreview_IsTheMessageTheSenderBuildsFromTheSavedPost_AndTheComposersOwnPreview()
    {
        await using var host = await StartAsync();
        var (user, manager) = await host.SignedInAsync(Host, Ct);
        var draft = Draft(text: "Friday at eight.\r\nBring snacks.  ");

        using var scope = host.Services.CreateScope();
        var actions = Actions(scope);

        var preview = await actions.PreviewAsync(draft, Staff(user), Ct);
        Assert.True(preview.Ready);
        Assert.Equal("announcements", preview.ChannelName);

        // The composer's preview of the same post, drawn by the same code.
        var composer = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(
                HttpMethod.Post,
                "/api/posts/preview",
                new { title = draft.Title, text = draft.Text, when = "now", timeZone = "UTC", draft = false, discord = new { channelId = Channel } },
                manager,
                Ct),
            Ct);
        Assert.Equal(composer.GetProperty("discord").GetProperty("content").GetString(), preview.Content);

        // And the sender's: PostTexts.Discord(post, destination) of the row Post now saved.
        var saved = await actions.PostNowAsync("discord:one", draft, Staff(user), Ct);
        Assert.True(saved.Created);

        await using var context = db.NewContext();
        var post = await context.Posts.AsNoTracking().Include(p => p.Destinations).SingleAsync(p => p.Id == saved.PostId, Ct);
        Assert.Equal(PostTexts.Discord(post, Assert.Single(post.Destinations)), preview.Content);
        Assert.Equal("**Movie night**\nFriday at eight.\nBring snacks.", preview.Content);
    }

    [Fact]
    public async Task ThePreview_OfAPostWithNoTitle_IsTheTextAlone()
    {
        await using var host = await StartAsync();
        var (user, _) = await host.SignedInAsync(Host, Ct);

        using var scope = host.Services.CreateScope();
        var preview = await Actions(scope).PreviewAsync(Draft(title: null, text: "Doors open at eight."), Staff(user), Ct);

        Assert.Equal("Doors open at eight.", preview.Content);
    }

    // ── Who may ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WritingNeedsManagePosts_SeePostsAloneCannotPreviewOrSave()
    {
        await using var host = await StartAsync();
        var (user, _) = await host.SignedInAsync(ModbotPermissions.ViewPosts, Ct);

        using var scope = host.Services.CreateScope();
        var actions = Actions(scope);
        var viewer = Staff(user, ModbotPermissions.ViewPosts);

        var preview = await actions.PreviewAsync(Draft(), viewer, Ct);
        Assert.False(preview.Ready);
        Assert.Equal([PostActionsForDiscord.NoPermission], preview.Problems);

        var saved = await actions.PostNowAsync("discord:viewer", Draft(), viewer, Ct);
        Assert.False(saved.Created);
        Assert.Equal([PostActionsForDiscord.NoPermission], saved.Problems);

        await using var context = db.NewContext();
        Assert.False(await context.Posts.AnyAsync(Ct));
    }

    [Fact]
    public async Task AdministratorMayWrite_AsInTheWebApp()
    {
        await using var host = await StartAsync();
        var (user, _) = await host.SignedInAsync(Host, Ct);

        using var scope = host.Services.CreateScope();
        var saved = await Actions(scope).PostNowAsync("discord:admin", Draft(), Staff(user, ModbotPermissions.Administrator), Ct);

        Assert.True(saved.Created);
    }

    [Fact]
    public async Task ListingNeedsSeePosts_ManagePostsAloneListsNothing()
    {
        await using var host = await StartAsync();
        var (user, _) = await host.SignedInAsync(Host, Ct);

        using var scope = host.Services.CreateScope();
        var actions = Actions(scope);
        await actions.PostNowAsync("discord:one", Draft(), Staff(user), Ct);

        Assert.Single((await actions.ListAsync(10, Staff(user), Ct)).Waiting);

        var writer = await actions.ListAsync(10, Staff(user, ModbotPermissions.ManagePosts), Ct);
        Assert.Empty(writer.Waiting);
        Assert.Empty(writer.Failed);
        Assert.Equal(0, writer.WaitingMore);
    }

    // ── Post now ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostNow_SavesOnePost_DueNow_ForDiscordAlone_UnderTheAccountsName()
    {
        await using var host = await StartAsync();
        var (user, manager) = await host.SignedInAsync(Host, Ct);

        using var scope = host.Services.CreateScope();
        var saved = await Actions(scope).PostNowAsync("discord:one", Draft(), Staff(user), Ct);

        Assert.True(saved.Created);
        Assert.False(saved.Repeat);
        Assert.Empty(saved.Problems);

        await using var context = db.NewContext();
        var post = await context.Posts.AsNoTracking().Include(p => p.Destinations).SingleAsync(Ct);
        Assert.Equal(saved.PostId, post.Id);
        Assert.Equal(PostStatuses.Scheduled, post.Status);
        Assert.Equal(host.Clock.UtcNow, post.SendAt);
        Assert.Equal("UTC", post.TimeZone);
        Assert.Equal(user.Id, post.CreatedByUserId);
        Assert.Equal("Movie night", post.Title);

        // Nothing is sent from here: the row waits for the Discord sender, which claims it first.
        var destination = Assert.Single(post.Destinations);
        Assert.Equal(PostNetworks.Discord, destination.Network);
        Assert.Equal(Channel, destination.Target);
        Assert.Equal(PostDestinationStates.Waiting, destination.State);
        Assert.Null(destination.ExternalId);
        Assert.Null(destination.SentAt);

        var fact = Assert.Single(await host.FactsAsync(FactType.PostCreated, post.Id.ToString(), Ct));
        Assert.Equal(user.Id.ToString(), fact.ActorId);

        // And it is there on the Marketing tab like any other.
        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/posts?list=scheduled", null, manager, Ct), Ct);
        Assert.Equal(post.Id, Assert.Single(list.GetProperty("posts").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task OneConfirmation_IsOnePost_HoweverManyTimesItIsPressed()
    {
        await using var host = await StartAsync();
        var (user, _) = await host.SignedInAsync(Host, Ct);

        using (var scope = host.Services.CreateScope())
        {
            var first = await Actions(scope).PostNowAsync("discord:same", Draft(), Staff(user), Ct);
            Assert.True(first.Created);

            var second = await Actions(scope).PostNowAsync("discord:same", Draft(), Staff(user), Ct);
            Assert.False(second.Created);
            Assert.True(second.Repeat);
            Assert.Equal(first.PostId, second.PostId);
        }

        await using var context = db.NewContext();
        Assert.Single(await context.Posts.ToListAsync(Ct));
        Assert.Single(await host.FactsAsync(FactType.PostCreated, (await context.Posts.SingleAsync(Ct)).Id.ToString(), Ct));
    }

    [Fact]
    public async Task PressesAtTheSameMoment_MakeOnePost()
    {
        await using var host = await StartAsync();
        var (user, _) = await host.SignedInAsync(Host, Ct);

        async Task<PostNowAnswer> PressAsync()
        {
            using var scope = host.Services.CreateScope();
            return await Actions(scope).PostNowAsync("discord:race", Draft(), Staff(user), Ct);
        }

        var answers = await Task.WhenAll(PressAsync(), PressAsync(), PressAsync());

        Assert.Equal(1, answers.Count(a => a.Created));
        Assert.Equal(2, answers.Count(a => a.Repeat));

        await using var context = db.NewContext();
        Assert.Single(await context.Posts.ToListAsync(Ct));
    }

    [Fact]
    public async Task TwoConfirmations_AreTwoPosts()
    {
        await using var host = await StartAsync();
        var (user, _) = await host.SignedInAsync(Host, Ct);

        using var scope = host.Services.CreateScope();
        await Actions(scope).PostNowAsync("discord:a", Draft(), Staff(user), Ct);
        await Actions(scope).PostNowAsync("discord:b", Draft(), Staff(user), Ct);

        await using var context = db.NewContext();
        Assert.Equal(2, await context.Posts.CountAsync(Ct));
    }

    // ── What stops a post ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APostThatCouldNotGo_IsRefusedWithEverythingWrongAtOnce_AndNothingIsSaved()
    {
        await using var host = await StartAsync();
        var (user, _) = await host.SignedInAsync(Host, Ct);

        using var scope = host.Services.CreateScope();
        var actions = Actions(scope);

        var elsewhere = await actions.PreviewAsync(Draft(channel: OtherServersChannel), Staff(user), Ct);
        Assert.Equal(["That channel is not in the Discord server."], elsewhere.Problems);

        var missing = await actions.PreviewAsync(Draft(channel: "777777777777777777"), Staff(user), Ct);
        Assert.Equal(["That channel is not in the Discord server."], missing.Problems);

        var voice = await actions.PreviewAsync(Draft(channel: VoiceChannel), Staff(user), Ct);
        Assert.Equal(["Pick a text channel."], voice.Problems);

        var empty = await actions.PreviewAsync(Draft(text: "  "), Staff(user), Ct);
        Assert.Equal(["Write some text."], empty.Problems);

        var long2000 = await actions.PreviewAsync(Draft(text: new string('x', 1990)), Staff(user), Ct);
        Assert.Equal([$"The Discord text is longer than {PostTexts.DiscordLimit} characters."], long2000.Problems);

        // Several at once, as the composer lists them.
        var many = await actions.PreviewAsync(Draft(text: " ", channel: OtherServersChannel), Staff(user), Ct);
        Assert.Equal(2, many.Problems.Count);

        var saved = await actions.PostNowAsync("discord:bad", Draft(channel: VoiceChannel), Staff(user), Ct);
        Assert.False(saved.Created);
        Assert.False(saved.Repeat);
        Assert.Equal(["Pick a text channel."], saved.Problems);

        await using var context = db.NewContext();
        Assert.False(await context.Posts.AnyAsync(Ct));
    }

    [Theory]
    [InlineData("paused", "Posting is paused in Modbot.")]
    [InlineData("off", "Discord posts are switched off in Modbot.")]
    public async Task APostIsNotSaved_WhilePostingIsPausedOrDiscordPostsAreOff(string why, string words)
    {
        await using var host = await StartAsync(s =>
        {
            s.PostsPaused = why == "paused";
            s.DiscordPostsOn = why != "off";
        });
        var (user, _) = await host.SignedInAsync(Host, Ct);

        using var scope = host.Services.CreateScope();
        var actions = Actions(scope);

        Assert.Equal([words], (await actions.PreviewAsync(Draft(), Staff(user), Ct)).Problems);
        Assert.Equal([words], (await actions.PostNowAsync("discord:held", Draft(), Staff(user), Ct)).Problems);

        await using var context = db.NewContext();
        Assert.False(await context.Posts.AnyAsync(Ct));
    }

    [Fact]
    public async Task APostIsNotSaved_WhileDiscordIsNotSetUp()
    {
        await using var host = await StartAsync(connected: false);
        var (user, _) = await host.SignedInAsync(Host, Ct);

        using var scope = host.Services.CreateScope();
        var saved = await Actions(scope).PostNowAsync("discord:down", Draft(), Staff(user), Ct);

        Assert.False(saved.Created);
        Assert.Equal([PostActionsForDiscord.NotSetUpMessage], saved.Problems);

        await using var context = db.NewContext();
        Assert.False(await context.Posts.AnyAsync(Ct));
    }

    // ── The list ────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> AddPostAsync(
        string title,
        DateTimeOffset createdAt,
        DateTimeOffset? sendAt,
        string state,
        string status = PostStatuses.Scheduled,
        string? error = null,
        DateTimeOffset? updatedAt = null,
        string channel = Channel)
    {
        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            Text = "Text of " + title,
            Status = status,
            SendAt = sendAt,
            TimeZone = "UTC",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt ?? createdAt,
        };

        post.Destinations.Add(new PostDestination
        {
            Id = Guid.CreateVersion7(),
            PostId = post.Id,
            Network = PostNetworks.Discord,
            Target = channel,
            Options = PostTexts.WriteDiscordOptions(new DiscordPostOptions()),
            State = state,
            Error = error,
            ErrorAt = error is null ? null : updatedAt ?? createdAt,
            UpdatedAt = updatedAt ?? createdAt,
        });

        await using var context = db.NewContext();
        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);
        return post.Id;
    }

    [Fact]
    public async Task TheList_IsTheNextTenWaiting_SoonestFirst_ThenTheFailedOnes_NewestChangeFirst()
    {
        await using var host = await StartAsync();
        var (user, _) = await host.SignedInAsync(Host, Ct);
        var now = host.Clock.UtcNow;

        // Waiting, added out of order; two share a time and keep the order they were written in.
        var third = await AddPostAsync("Third", now, now.AddHours(3), PostDestinationStates.Waiting);
        var first = await AddPostAsync("First", now.AddMinutes(1), now.AddHours(1), PostDestinationStates.Waiting);
        var secondA = await AddPostAsync("Second A", now.AddMinutes(2), now.AddHours(2), PostDestinationStates.Waiting);
        var secondB = await AddPostAsync("Second B", now.AddMinutes(3), now.AddHours(2), PostDestinationStates.Waiting, channel: Quiet);

        // Failed, the older change first in the data.
        var failedOld = await AddPostAsync("Old failure", now.AddDays(-3), now.AddDays(-3), PostDestinationStates.Failed, error: "Missing Access", updatedAt: now.AddDays(-2));
        var failedNew = await AddPostAsync("New failure", now.AddDays(-1), now.AddDays(-1), PostDestinationStates.Failed, error: "Not sent on time.", updatedAt: now.AddHours(-5));

        // Not in either part: sent, a draft, cancelled.
        await AddPostAsync("Sent", now.AddDays(-4), now.AddDays(-4), PostDestinationStates.Posted);
        await AddPostAsync("Draft", now, null, PostDestinationStates.Waiting, status: PostStatuses.Draft);
        await AddPostAsync("Cancelled", now, now.AddHours(9), PostDestinationStates.Skipped, status: PostStatuses.Cancelled);

        using var scope = host.Services.CreateScope();
        var list = await Actions(scope).ListAsync(10, Staff(user), Ct);

        Assert.Equal([first, secondA, secondB, third], list.Waiting.Select(p => p.Id));
        Assert.Equal(["First", "Second A", "Second B", "Third"], list.Waiting.Select(p => p.Headline));
        Assert.Equal(now.AddHours(1), list.Waiting[0].SendAt);
        Assert.Equal(["Discord #announcements", "Discord #announcements", "Discord #quiet", "Discord #announcements"], list.Waiting.Select(p => p.Where));
        Assert.Equal(0, list.WaitingMore);

        Assert.Equal([failedNew, failedOld], list.Failed.Select(p => p.Id));
        Assert.Equal(["Not sent on time.", "Missing Access"], list.Failed.Select(p => p.Error));
        Assert.Equal(0, list.FailedMore);
    }

    [Fact]
    public async Task TheList_CapsEachPart_AndSaysHowManyMoreThereAre()
    {
        await using var host = await StartAsync();
        var (user, _) = await host.SignedInAsync(Host, Ct);
        var now = host.Clock.UtcNow;

        for (var i = 0; i < 13; i++)
            await AddPostAsync("Waiting " + i.ToString(CultureInfo.InvariantCulture), now, now.AddHours(i + 1), PostDestinationStates.Waiting);

        for (var i = 0; i < 12; i++)
            await AddPostAsync("Failed " + i.ToString(CultureInfo.InvariantCulture), now, now, PostDestinationStates.Failed, error: "Missing Access", updatedAt: now.AddMinutes(i));

        using var scope = host.Services.CreateScope();
        var list = await Actions(scope).ListAsync(10, Staff(user), Ct);

        Assert.Equal(10, list.Waiting.Count);
        Assert.Equal(3, list.WaitingMore);
        Assert.Equal("Waiting 0", list.Waiting[0].Headline);
        Assert.Equal("Waiting 9", list.Waiting[^1].Headline);

        Assert.Equal(10, list.Failed.Count);
        Assert.Equal(2, list.FailedMore);
        Assert.Equal("Failed 11", list.Failed[0].Headline);
    }

    [Fact]
    public async Task APostWithNoTitle_IsNamedByItsFirstLine()
    {
        await using var host = await StartAsync();
        var (user, _) = await host.SignedInAsync(Host, Ct);

        using var scope = host.Services.CreateScope();
        await Actions(scope).PostNowAsync("discord:untitled", Draft(title: null, text: "Doors open at eight.\nBring snacks."), Staff(user), Ct);

        var line = Assert.Single((await Actions(scope).ListAsync(10, Staff(user), Ct)).Waiting);
        Assert.Equal("Doors open at eight.", line.Headline);
    }

    [Fact]
    public void TheKeyMakesThePostsId_AndOnlyTheKeyDoes()
    {
        Assert.Equal(PostActionsForDiscord.IdFor("discord:a"), PostActionsForDiscord.IdFor("discord:a"));
        Assert.NotEqual(PostActionsForDiscord.IdFor("discord:a"), PostActionsForDiscord.IdFor("discord:b"));
        Assert.NotEqual(Guid.Empty, PostActionsForDiscord.IdFor("discord:a"));
    }

    [Fact]
    public async Task TheEndpointsStillRefuseWhatTheyAlwaysRefused()
    {
        // The commands are an addition: the Marketing tab's own rules are the same code, and a
        // viewer is still refused a write there.
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewPosts, Ct);

        var refused = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/posts",
            new { title = "x", text = "y", when = "now", timeZone = "UTC", draft = false, discord = new { channelId = Channel } },
            viewer,
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}
