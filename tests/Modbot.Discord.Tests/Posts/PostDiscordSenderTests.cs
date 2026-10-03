using Discord;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.Discord.Gateway;
using Modbot.Discord.Posts;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Posts;

/// <summary>
/// Posts sent to Discord from the Marketing tab (posts design §3.4, §3.5): sent once at their time,
/// looked for and adopted after an unclear answer, never sent again by themselves, held by the
/// pause and the switch, and Failed when too late.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PostDiscordSenderTests(PostgresFixture db)
{
    private const string Guild = "111111111111111111";
    private const string Channel = "222222222222222222";
    private const string Role = "333333333333333333";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestServices> StartAsync(PostgresFixture db)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        return services;
    }

    private static FakeGateway Gateway(TestServices services) => new()
    {
        State = DiscordGatewayState.Ready,
        PostClock = () => services.Clock.UtcNow,
    };

    private static async Task<PostDiscordPass> RunAsync(TestServices services, FakeGateway gateway)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<PostDiscordSender>().RunOnceAsync(gateway, Ct);
    }

    private static async Task<Post> AddPostAsync(
        TestServices services, Action<Post>? shape = null, DiscordPostOptions? options = null, TimeSpan? dueIn = null)
    {
        var now = services.Clock.UtcNow;
        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Text = "Friday at eight. Bring snacks.",
            Status = PostStatuses.Scheduled,
            SendAt = now + (dueIn ?? TimeSpan.Zero),
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };

        post.Destinations.Add(new PostDestination
        {
            Id = Guid.CreateVersion7(),
            PostId = post.Id,
            Network = PostNetworks.Discord,
            Target = Channel,
            Options = PostTexts.WriteDiscordOptions(options ?? new DiscordPostOptions()),
            State = PostDestinationStates.Waiting,
            UpdatedAt = now,
        });

        shape?.Invoke(post);

        await using var context = services.Database.NewContext();
        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);
        return post;
    }

    private static async Task<PostDestination> DestinationAsync(TestServices services, Guid postId)
    {
        await using var context = services.Database.NewContext();
        return await context.PostDestinations.AsNoTracking().SingleAsync(d => d.PostId == postId, Ct);
    }

    private static async Task ChangeDestinationAsync(TestServices services, Guid postId, Action<Post, PostDestination> change)
    {
        await using var context = services.Database.NewContext();
        var post = await context.Posts.Include(p => p.Destinations).SingleAsync(p => p.Id == postId, Ct);
        change(post, post.Destinations.Single());
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<List<string>> FactsAsync(TestServices services, Guid postId)
    {
        await using var context = services.Database.NewContext();
        return await context.Events.AsNoTracking()
            .Where(e => e.SubjectId == postId.ToString())
            .OrderBy(e => e.Id)
            .Select(e => e.Type)
            .ToListAsync(Ct);
    }

    [Fact]
    public async Task ADuePostIsSentOnce_AndPosted()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        var post = await AddPostAsync(services);

        await RunAsync(services, gateway);
        await RunAsync(services, gateway);

        var sent = Assert.Single(gateway.PostSends);
        Assert.Equal(Channel, sent.ChannelId);
        Assert.Equal("**Movie night**\nFriday at eight. Bring snacks.", sent.Text);

        var destination = await DestinationAsync(services, post.Id);
        Assert.Equal(PostDestinationStates.Posted, destination.State);
        Assert.Equal(sent.MessageId, destination.ExternalId);
        Assert.Equal($"https://discord.com/channels/{Guild}/{Channel}/{sent.MessageId}", destination.Link);
        Assert.Equal(sent.Text, destination.SentText);
        Assert.Contains(FactType.PostSent, await FactsAsync(services, post.Id));
    }

    [Fact]
    public async Task APostBeforeItsTimeWaits()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        var post = await AddPostAsync(services, dueIn: TimeSpan.FromMinutes(5));

        await RunAsync(services, gateway);

        Assert.Empty(gateway.PostSends);
        Assert.Equal(PostDestinationStates.Waiting, (await DestinationAsync(services, post.Id)).State);
    }

    /// <summary>
    /// A timeout after Discord took the message: Modbot looks a minute later, finds it and keeps its
    /// id. It never sends a second one.
    /// </summary>
    [Fact]
    public async Task AnUnclearAnswerIsLookedFor_AndAdopted_WithNoSecondPost()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        var post = await AddPostAsync(services);

        gateway.NoClearAnswerToNextPost(landed: true);
        await RunAsync(services, gateway);

        var checking = await DestinationAsync(services, post.Id);
        Assert.Equal(PostDestinationStates.Checking, checking.State);
        Assert.True(checking.MayBeSent);

        // Not yet a minute: no look, and nothing sent again.
        await RunAsync(services, gateway);
        Assert.Empty(gateway.RecentReads);

        services.Clock.Advance(PostRules.FirstLookAfter);
        await RunAsync(services, gateway);

        var adopted = await DestinationAsync(services, post.Id);
        Assert.Equal(PostDestinationStates.Posted, adopted.State);
        Assert.Equal(gateway.PostSends[0].MessageId, adopted.ExternalId);
        Assert.Single(gateway.PostSends);
        Assert.Single(gateway.RecentReads);
    }

    [Fact]
    public async Task TheLookFindingNothing_IsFailed_AndNotSentAgain()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        var post = await AddPostAsync(services);

        gateway.NoClearAnswerToNextPost(landed: false);
        await RunAsync(services, gateway);
        services.Clock.Advance(PostRules.FirstLookAfter);
        await RunAsync(services, gateway);

        var failed = await DestinationAsync(services, post.Id);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.Equal(PostDiscordSender.NotTaken, failed.Error);
        Assert.True(failed.MayBeSent);

        services.Clock.Advance(TimeSpan.FromHours(2));
        await RunAsync(services, gateway);
        Assert.Single(gateway.PostSends);
    }

    [Fact]
    public async Task TryAgainLooksFirst_AndSendsOnlyWhenItIsNotThere()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        var post = await AddPostAsync(services);

        gateway.NoClearAnswerToNextPost(landed: false);
        await RunAsync(services, gateway);
        services.Clock.Advance(PostRules.FirstLookAfter);
        await RunAsync(services, gateway);

        await ChangeDestinationAsync(services, post.Id, (p, d) => PostChanges.TryAgain(p, d, services.Clock.UtcNow));

        // The look comes first and finds nothing; then it is sent, once.
        await RunAsync(services, gateway);
        await RunAsync(services, gateway);

        Assert.Equal(2, gateway.RecentReads.Count);
        Assert.Equal(2, gateway.PostSends.Count);
        Assert.Equal(PostDestinationStates.Posted, (await DestinationAsync(services, post.Id)).State);
    }

    [Fact]
    public async Task TryAgainAdoptsAPostThatTurnedUp_InsteadOfSendingAnother()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        var post = await AddPostAsync(services);

        gateway.NoClearAnswerToNextPost(landed: false);
        await RunAsync(services, gateway);
        services.Clock.Advance(PostRules.FirstLookAfter);
        await RunAsync(services, gateway);

        // It shows up in the channel after all.
        var sentText = (await DestinationAsync(services, post.Id)).SentText!;
        var lateId = gateway.Land(Channel, sentText, []);

        await ChangeDestinationAsync(services, post.Id, (p, d) => PostChanges.TryAgain(p, d, services.Clock.UtcNow));
        await RunAsync(services, gateway);
        await RunAsync(services, gateway);

        var destination = await DestinationAsync(services, post.Id);
        Assert.Equal(PostDestinationStates.Posted, destination.State);
        Assert.Equal(lateId, destination.ExternalId);
        Assert.Single(gateway.PostSends);
    }

    /// <summary>Without Read Message History the look cannot be made: it is tried again, then Failed, and never resent.</summary>
    [Fact]
    public async Task WithoutReadMessageHistory_ItIsNeverSentAgain()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        gateway.NoAccess.Add(Channel);
        var post = await AddPostAsync(services);

        gateway.NoClearAnswerToNextPost(landed: false);
        await RunAsync(services, gateway);

        services.Clock.Advance(PostRules.FirstLookAfter);
        await RunAsync(services, gateway);

        var still = await DestinationAsync(services, post.Id);
        Assert.Equal(PostDestinationStates.Checking, still.State);
        Assert.Equal(services.Clock.UtcNow + PostRules.LookAgainAfter, still.CheckAt);

        for (var i = 0; i < 5; i++)
        {
            services.Clock.Advance(PostRules.LookAgainAfter);
            await RunAsync(services, gateway);
        }

        var failed = await DestinationAsync(services, post.Id);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.Equal(PostDiscordSender.CouldNotCheck, failed.Error);
        Assert.Single(gateway.PostSends);
    }

    [Fact]
    public async Task AClearRefusalFails_WithDiscordsWords()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        var post = await AddPostAsync(services);

        gateway.AnswerNextPost(DiscordPostOutcome.Failed("Could not post to Discord: Missing Permissions.", permanent: true));
        await RunAsync(services, gateway);

        var failed = await DestinationAsync(services, post.Id);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.Equal("Could not post to Discord: Missing Permissions.", failed.Error);
        Assert.False(failed.MayBeSent);
        Assert.Contains(FactType.PostFailed, await FactsAsync(services, post.Id));
    }

    [Fact]
    public async Task ARateLimitWaitsForALaterPass()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        var post = await AddPostAsync(services);

        gateway.AnswerNextPost(DiscordPostOutcome.Failed("Discord is rate limiting the bot; it will try again shortly."));
        await RunAsync(services, gateway);
        Assert.Equal(PostDestinationStates.Waiting, (await DestinationAsync(services, post.Id)).State);

        await RunAsync(services, gateway);
        Assert.Equal(PostDestinationStates.Posted, (await DestinationAsync(services, post.Id)).State);
    }

    [Fact]
    public async Task ARestartMidSendIsLookedFor_NotSentAgain()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        var post = await AddPostAsync(services);

        // Claimed and cut off: Sending, with no answer written.
        await ChangeDestinationAsync(services, post.Id, (_, d) =>
        {
            d.State = PostDestinationStates.Sending;
            d.SentAt = services.Clock.UtcNow;
            d.SentText = "**Movie night**\nFriday at eight. Bring snacks.";
        });

        services.Clock.Advance(PostRules.StuckSendingAfter + TimeSpan.FromSeconds(1));
        await RunAsync(services, gateway);

        Assert.Empty(gateway.PostSends);
        Assert.Single(gateway.RecentReads);
        Assert.Equal(PostDestinationStates.Failed, (await DestinationAsync(services, post.Id)).State);
    }

    [Fact]
    public async Task TheRoleIsPingedOnTheFirstSend()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        await AddPostAsync(services, options: new DiscordPostOptions(Role));

        await RunAsync(services, gateway);

        var sent = Assert.Single(gateway.PostSends);
        Assert.Equal(Role, sent.RoleId);
        Assert.StartsWith($"<@&{Role}>\n", sent.Text, StringComparison.Ordinal);
    }

    /// <summary>An edit keeps the role shown and pings nobody: it is a text change, never a new send.</summary>
    [Fact]
    public async Task AnEditNeverPingsTheRole()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        var post = await AddPostAsync(services, options: new DiscordPostOptions(Role));
        await RunAsync(services, gateway);

        var destination = await DestinationAsync(services, post.Id);
        var actions = new DiscordPostActions(() => gateway);
        var outcome = await actions.EditAsync(Channel, destination.ExternalId!, PostTexts.Discord("Movie night", "Now at nine.", Role), Ct);

        Assert.True(outcome.Done);
        var edit = Assert.Single(gateway.PostEdits);
        Assert.StartsWith($"<@&{Role}>\n", edit.Text, StringComparison.Ordinal);
        Assert.Single(gateway.PostSends);
    }

    [Fact]
    public async Task PublishIsAskedFor_OnlyWhenTicked()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        var published = await AddPostAsync(services, options: new DiscordPostOptions(Publish: true));
        await RunAsync(services, gateway);
        var plain = await AddPostAsync(services);
        await RunAsync(services, gateway);

        var publishedRow = await DestinationAsync(services, published.Id);
        Assert.Equal((Channel, publishedRow.ExternalId!), Assert.Single(gateway.Published));
        Assert.NotNull(publishedRow.PublishedAt);
        Assert.Null((await DestinationAsync(services, plain.Id)).PublishedAt);
    }

    [Fact]
    public async Task APublishRefusedLeavesThePostPosted_AndSaysSo()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);
        gateway.PublishError = "Could not post to Discord: Discord answered 400.";
        var post = await AddPostAsync(services, options: new DiscordPostOptions(Publish: true));

        await RunAsync(services, gateway);

        var destination = await DestinationAsync(services, post.Id);
        Assert.Equal(PostDestinationStates.Posted, destination.State);
        Assert.Null(destination.PublishedAt);
        Assert.Equal("Could not post to Discord: Discord answered 400.", destination.Error);
    }

    [Fact]
    public async Task PauseAllPostingHoldsIt()
    {
        await using var services = await StartAsync(db);
        await services.ConfigureAsync(s => s.PostsPaused = true, Ct);
        var gateway = Gateway(services);
        var post = await AddPostAsync(services);

        await RunAsync(services, gateway);

        Assert.Empty(gateway.PostSends);
        Assert.Equal(PostDestinationStates.Waiting, (await DestinationAsync(services, post.Id)).State);
    }

    [Fact]
    public async Task DiscordPostsOffHoldsIt()
    {
        await using var services = await StartAsync(db);
        await services.ConfigureAsync(s => s.DiscordPostsOn = false, Ct);
        var gateway = Gateway(services);
        await AddPostAsync(services);

        await RunAsync(services, gateway);

        Assert.Empty(gateway.PostSends);
    }

    [Fact]
    public async Task MoreThanAnHourLateIsFailed_NotSent()
    {
        await using var services = await StartAsync(db);
        await services.ConfigureAsync(s => s.PostsPaused = true, Ct);
        var gateway = Gateway(services);
        var post = await AddPostAsync(services);

        services.Clock.Advance(PostRules.LateLimit + TimeSpan.FromMinutes(1));
        await services.ConfigureAsync(s => s.PostsPaused = false, Ct);
        await RunAsync(services, gateway);

        Assert.Empty(gateway.PostSends);
        var failed = await DestinationAsync(services, post.Id);
        Assert.Equal(PostDestinationStates.Failed, failed.State);
        Assert.Equal(PostRules.NotSentOnTime, failed.Error);
        Assert.False(failed.MayBeSent);
    }

    [Fact]
    public async Task OneSendAPass_AndNoMoreThanTenAnHour()
    {
        await using var services = await StartAsync(db);
        var gateway = Gateway(services);

        for (var i = 0; i < PostRules.PerSitePerHour + 2; i++)
            await AddPostAsync(services);

        await RunAsync(services, gateway);
        Assert.Single(gateway.PostSends);

        for (var i = 0; i < PostRules.PerSitePerHour + 2; i++)
        {
            services.Clock.Advance(TimeSpan.FromSeconds(20));
            await RunAsync(services, gateway);
        }

        Assert.Equal(PostRules.PerSitePerHour, gateway.PostSends.Count);
    }

    [Fact]
    public void TheLibraryNeverSendsAPostAgainByItself()
    {
        Assert.Equal(RetryMode.AlwaysFail, DiscordNetGateway.PostOptions(Ct).RetryMode);
    }

    [Fact]
    public async Task DeletingAPostAlreadyGoneCountsAsDone()
    {
        var gateway = new FakeGateway { State = DiscordGatewayState.Ready };
        gateway.FailNextDelete("Unknown Message", notFound: true);

        var outcome = await new DiscordPostActions(() => gateway).DeleteAsync(Channel, "444", "Post deleted from Modbot", Ct);

        Assert.True(outcome.Done);
    }

    [Fact]
    public async Task WithTheBotOffline_NothingIsAskedOfDiscord()
    {
        var outcome = await new DiscordPostActions(() => null).DeleteAsync(Channel, "444", "Post deleted from Modbot", Ct);

        Assert.True(outcome.BotOffline);
    }
}
