using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Posts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.TestSupport;
using NodaTime;

namespace Modbot.Api.Tests.Features.Posts;

/// <summary>
/// Keep updated (posts design §8): a post that went out is edited on Discord and VRChat when an
/// event in one of its lists changes, once a window, never while posting is paused, and never on a
/// site that turned the last edit away. Bluesky keeps its first version.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PostListUpdaterTests
{
    private const string Channel = "222222222222222222";
    private const string Group = "grp_00000000-0000-0000-0000-000000000001";

    private static readonly DateTimeOffset Friday = new(2026, 10, 16, 19, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PostgresFixture _db;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeDiscord _discord = new();
    private readonly FakeVRChat _vrchat = new();
    private readonly PostListWindow _window = new();

    public PostListUpdaterTests(PostgresFixture db) => _db = db;

    private sealed class FakeDiscord : IDiscordPostActions
    {
        public List<(string ChannelId, string MessageId, string Text)> Edits { get; } = [];

        public PostSiteOutcome Answer { get; set; } = PostSiteOutcome.Ok;

        public Task<PostSiteOutcome> EditAsync(string channelId, string messageId, string text, CancellationToken ct = default)
        {
            Edits.Add((channelId, messageId, text));
            return Task.FromResult(Answer);
        }

        public Task<PostSiteOutcome> DeleteAsync(string channelId, string messageId, string reason, CancellationToken ct = default)
            => Task.FromResult(Answer);

        public Task<PostSiteOutcome> PublishAsync(string channelId, string messageId, CancellationToken ct = default)
            => Task.FromResult(Answer);
    }

    private sealed class FakeVRChat : IVRChatPostActions
    {
        public List<(string PostId, string Title, string Text, string Visibility, string? ImageId)> Edits { get; } = [];

        public PostSiteOutcome Answer { get; set; } = PostSiteOutcome.Ok;

        public Task<PostSiteOutcome> EditAsync(
            string groupId, string postId, string title, string text, string visibility, IReadOnlyList<string> roleIds, string? imageId, CancellationToken ct = default)
        {
            Edits.Add((postId, title, text, visibility, imageId));
            return Task.FromResult(Answer);
        }

        public Task<PostSiteOutcome> DeleteAsync(string groupId, string postId, CancellationToken ct = default)
            => Task.FromResult(PostSiteOutcome.Ok);
    }

    private async Task SetUpAsync(bool paused = false, bool discordPostsOn = true, bool vrchatPostsOn = true)
    {
        await using var context = _db.NewContext();
        await context.Posts.ExecuteDeleteAsync(Ct);
        await context.CalendarEvents.IgnoreAutoIncludes().ExecuteDeleteAsync(Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.PostsPaused = paused;
        settings.DiscordPostsOn = discordPostsOn;
        settings.VRChatPostsOn = vrchatPostsOn;
        settings.DiscordGuildId = null;
        settings.PublicAddress = null;
        await context.SaveChangesAsync(Ct);
    }

    private async Task<int> PassAsync()
    {
        await using var context = _db.NewContext();
        var updater = new PostListUpdater(
            context,
            _clock,
            new FactWriter(context, _clock),
            new EventPartitionMaintainer(context, _clock),
            _discord,
            _vrchat,
            _window);

        return await updater.RunOnceAsync(Ct);
    }

    private CalendarEvent EventAt(string title, DateTimeOffset startsAt) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        StartsAt = startsAt,
        EndsAt = startsAt.AddHours(1),
        TimeZone = "Europe/London",
        AccessType = "members",
        State = CalendarEventStates.Scheduled,
        CreatedAt = _clock.UtcNow,
        UpdatedAt = _clock.UtcNow,
    };

    private static PostEventLine Line(CalendarEvent e) => new(e.Id, e.Title, e.StartsAt, e.TimeZone, null);

    private static string October(params PostEventLine[] lines) =>
        PostEventText.WhatsOnList(new LocalDate(2026, 10, 1), new LocalDate(2026, 10, 31), "Europe/London", lines);

    private async Task<CalendarEvent> AddEventAsync(string title, DateTimeOffset startsAt)
    {
        await using var context = _db.NewContext();
        var made = EventAt(title, startsAt);
        context.CalendarEvents.Add(made);
        await context.SaveChangesAsync(Ct);
        return made;
    }

    private async Task ChangeEventAsync(Guid id, Action<CalendarEvent> change)
    {
        await using var context = _db.NewContext();
        var found = await context.CalendarEvents.SingleAsync(e => e.Id == id, Ct);
        change(found);
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>A post that went out to Discord and VRChat (and Bluesky) with the text as it was, the list built from <paramref name="lines"/>.</summary>
    private async Task<Guid> AddSentPostAsync(
        string list, bool keepUpdated = true, bool bluesky = false, Action<PostDestination>? shape = null)
    {
        await using var context = _db.NewContext();
        var text = $"Come along!\n\n{list}\n\nSee you there.";

        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            Title = "This month",
            Text = text,
            Status = PostStatuses.Scheduled,
            SendAt = _clock.UtcNow.AddDays(-1),
            TimeZone = "UTC",
            KeepUpdated = keepUpdated,
            Version = 1,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        };

        var discord = new PostDestination
        {
            Id = Guid.CreateVersion7(),
            PostId = post.Id,
            Network = PostNetworks.Discord,
            Target = Channel,
            State = PostDestinationStates.Posted,
            ExternalId = "333333333333333333",
            SentTitle = "This month",
            SentText = PostTexts.Discord("This month", PostEventText.ForSite(PostNetworks.Discord, text), null),
            UpdatedAt = _clock.UtcNow,
        };

        var vrchat = new PostDestination
        {
            Id = Guid.CreateVersion7(),
            PostId = post.Id,
            Network = PostNetworks.VRChat,
            Target = Group,
            Options = PostTexts.WriteVRChatOptions(new VRChatPostOptions(ImageId: "file_1")),
            State = PostDestinationStates.Posted,
            ExternalId = "not_1",
            SentTitle = "This month",
            SentText = PostEventText.ForSite(PostNetworks.VRChat, text),
            UpdatedAt = _clock.UtcNow,
        };

        shape?.Invoke(discord);
        post.Destinations.Add(discord);
        post.Destinations.Add(vrchat);

        if (bluesky)
        {
            post.Destinations.Add(new PostDestination
            {
                Id = Guid.CreateVersion7(),
                PostId = post.Id,
                Network = PostNetworks.Bluesky,
                Target = "did:plc:test",
                State = PostDestinationStates.Posted,
                ExternalId = "at://did:plc:test/app.bsky.feed.post/abc",
                SentText = "This month\nCome along!",
                UpdatedAt = _clock.UtcNow,
            });
        }

        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);
        return post.Id;
    }

    private async Task<Post> PostAsync(Guid id)
    {
        await using var context = _db.NewContext();
        return await context.Posts.AsNoTracking().Include(p => p.Destinations).SingleAsync(p => p.Id == id, Ct);
    }

    [Fact]
    public async Task AnEventPublishedInTheRangeEditsTheListOnDiscordAndVRChat_NothingElseChanges()
    {
        await SetUpAsync();
        var movies = await AddEventAsync("Movie night", Friday);
        var id = await AddSentPostAsync(October(Line(movies)), bluesky: true);

        var games = await AddEventAsync("Games night", Friday.AddDays(1));

        Assert.Equal(2, await PassAsync());

        var discord = Assert.Single(_discord.Edits);
        Assert.Equal((Channel, "333333333333333333"), (discord.ChannelId, discord.MessageId));
        Assert.StartsWith("**This month**\nCome along!\n", discord.Text, StringComparison.Ordinal);
        Assert.Contains("Games night - <t:", discord.Text, StringComparison.Ordinal);
        Assert.EndsWith("\nSee you there.", discord.Text, StringComparison.Ordinal);

        var vrchat = Assert.Single(_vrchat.Edits);
        Assert.Equal(("not_1", "This month", "file_1"), (vrchat.PostId, vrchat.Title, vrchat.ImageId));
        Assert.Contains("Games night - Sat 17 Oct, 20:00 BST", vrchat.Text, StringComparison.Ordinal);
        Assert.StartsWith("Come along!\n", vrchat.Text, StringComparison.Ordinal);

        var post = await PostAsync(id);
        Assert.Equal($"Come along!\n\n{October(Line(movies), Line(games))}\n\nSee you there.", post.Text);
        Assert.Equal(_clock.UtcNow, post.ListsUpdatedAt);
        Assert.Equal(2, post.Version);
        Assert.Equal(discord.Text, post.Destinations.Single(d => d.Network == PostNetworks.Discord).SentText);
        Assert.Equal(vrchat.Text, post.Destinations.Single(d => d.Network == PostNetworks.VRChat).SentText);
        Assert.Equal("This month\nCome along!", post.Destinations.Single(d => d.Network == PostNetworks.Bluesky).SentText);

        await using var context = _db.NewContext();
        Assert.Equal(2, await context.Events.CountAsync(e => e.Type == FactType.PostEdited && e.SubjectId == id.ToString(), Ct));
    }

    [Fact]
    public async Task ACancelledEventIsTakenOutOfTheList()
    {
        await SetUpAsync();
        var movies = await AddEventAsync("Movie night", Friday);
        var games = await AddEventAsync("Games night", Friday.AddDays(1));
        await AddSentPostAsync(October(Line(movies), Line(games)));

        await ChangeEventAsync(movies.Id, e => e.State = CalendarEventStates.Cancelled);
        await PassAsync();

        Assert.DoesNotContain("Movie night", Assert.Single(_vrchat.Edits).Text, StringComparison.Ordinal);
        Assert.Contains("Games night", _vrchat.Edits[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingIsEditedWhenTheListsReadTheSame()
    {
        await SetUpAsync();
        var movies = await AddEventAsync("Movie night", Friday);
        await AddSentPostAsync(October(Line(movies)));

        Assert.Equal(0, await PassAsync());

        Assert.Empty(_discord.Edits);
        Assert.Empty(_vrchat.Edits);
    }

    [Fact]
    public async Task APostWithoutKeepUpdatedIsLeftAlone()
    {
        await SetUpAsync();
        var movies = await AddEventAsync("Movie night", Friday);
        await AddSentPostAsync(October(Line(movies)), keepUpdated: false);
        await AddEventAsync("Games night", Friday.AddDays(1));

        Assert.Equal(0, await PassAsync());
        Assert.Empty(_discord.Edits);
    }

    [Fact]
    public async Task NothingIsEditedWhilePostingIsPaused()
    {
        await SetUpAsync(paused: true);
        var movies = await AddEventAsync("Movie night", Friday);
        await AddSentPostAsync(October(Line(movies)));
        await AddEventAsync("Games night", Friday.AddDays(1));

        Assert.Equal(0, await PassAsync());
        Assert.Empty(_discord.Edits);
        Assert.Empty(_vrchat.Edits);
    }

    [Fact]
    public async Task ASiteWhosePostingSwitchIsOffIsNotEdited_AndTheOtherSiteIs()
    {
        await SetUpAsync(discordPostsOn: false);
        var movies = await AddEventAsync("Movie night", Friday);
        var id = await AddSentPostAsync(October(Line(movies)));
        var games = await AddEventAsync("Games night", Friday.AddDays(1));

        Assert.Equal(1, await PassAsync());

        Assert.Empty(_discord.Edits);
        Assert.Contains("Games night", Assert.Single(_vrchat.Edits).Text, StringComparison.Ordinal);

        // Discord still has the old list to find, so the post's own text keeps it until Discord takes the edit.
        var post = await PostAsync(id);
        Assert.Equal($"Come along!\n\n{October(Line(movies))}\n\nSee you there.", post.Text);
        Assert.DoesNotContain("Games night", post.Destinations.Single(d => d.Network == PostNetworks.Discord).SentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVRChatSwitchThatIsOffLeavesVRChatAloneAndDiscordIsStillEdited()
    {
        await SetUpAsync(vrchatPostsOn: false);
        var movies = await AddEventAsync("Movie night", Friday);
        await AddSentPostAsync(October(Line(movies)));
        await AddEventAsync("Games night", Friday.AddDays(1));

        Assert.Equal(1, await PassAsync());

        Assert.Empty(_vrchat.Edits);
        Assert.Single(_discord.Edits);
    }

    [Fact]
    public async Task ARunOfCalendarEditsMakesOneEditAWindow()
    {
        await SetUpAsync();
        var movies = await AddEventAsync("Movie night", Friday);
        var id = await AddSentPostAsync(October(Line(movies)));

        await AddEventAsync("Games night", Friday.AddDays(1));
        await PassAsync();

        _clock.Advance(TimeSpan.FromMinutes(2));
        await AddEventAsync("Quiz night", Friday.AddDays(2));
        Assert.Equal(0, await PassAsync());
        Assert.Single(_discord.Edits);

        _clock.Advance(PostListUpdater.Window);
        Assert.Equal(2, await PassAsync());
        Assert.Equal(2, _discord.Edits.Count);
        Assert.Contains("Quiz night", (await PostAsync(id)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASiteThatTurnsTheEditAwayIsNotAskedAgainUntilTheWindowIsOver_AndTheOldListIsKept()
    {
        await SetUpAsync();
        var movies = await AddEventAsync("Movie night", Friday);
        var before = October(Line(movies));
        var id = await AddSentPostAsync(before);
        await AddEventAsync("Games night", Friday.AddDays(1));
        _vrchat.Answer = PostSiteOutcome.Failed("VRChat is not taking posts right now. Try again in a few minutes.");

        Assert.Equal(1, await PassAsync());

        // Discord took it, VRChat did not: the post keeps the old list to find again.
        var post = await PostAsync(id);
        Assert.Contains(before, post.Text, StringComparison.Ordinal);
        Assert.Contains("Games night", post.Destinations.Single(d => d.Network == PostNetworks.Discord).SentText, StringComparison.Ordinal);
        Assert.DoesNotContain("Games night", post.Destinations.Single(d => d.Network == PostNetworks.VRChat).SentText, StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromMinutes(2));
        await PassAsync();
        Assert.Single(_vrchat.Edits);

        _vrchat.Answer = PostSiteOutcome.Ok;
        _clock.Advance(PostListUpdater.Window);
        Assert.Equal(1, await PassAsync());

        // Discord was not edited a second time; VRChat took it now, and the post has the new list.
        Assert.Single(_discord.Edits);
        Assert.Equal(2, _vrchat.Edits.Count);
        Assert.Contains("Games night", (await PostAsync(id)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AListEditedByHandOnASiteIsLeftThere_AndTheOtherSiteIsStillUpdated()
    {
        await SetUpAsync();
        var movies = await AddEventAsync("Movie night", Friday);
        var id = await AddSentPostAsync(
            October(Line(movies)),
            shape: d =>
            {
                d.SentText = "**This month**\nMovie night is on, come early";
                d.TextOverride = "Movie night is on, come early";
            });
        await AddEventAsync("Games night", Friday.AddDays(1));

        Assert.Equal(1, await PassAsync());

        Assert.Empty(_discord.Edits);
        Assert.Single(_vrchat.Edits);
        Assert.Equal("**This month**\nMovie night is on, come early", (await PostAsync(id)).Destinations.Single(d => d.Network == PostNetworks.Discord).SentText);
    }

    [Fact]
    public async Task AMessageGoneFromDiscordIsMarkedDeleted_AndVRChatIsStillUpdated()
    {
        await SetUpAsync();
        var movies = await AddEventAsync("Movie night", Friday);
        var id = await AddSentPostAsync(October(Line(movies)));
        await AddEventAsync("Games night", Friday.AddDays(1));
        _discord.Answer = PostSiteOutcome.Failed("That message is gone.", gone: true);

        Assert.Equal(1, await PassAsync());

        var post = await PostAsync(id);
        Assert.Equal(PostDestinationStates.Removed, post.Destinations.Single(d => d.Network == PostNetworks.Discord).State);
        Assert.Contains("Games night", post.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AListWhoseLastDayHasPassedIsNoLongerEdited()
    {
        await SetUpAsync();
        var movies = await AddEventAsync("Movie night", Friday);
        await AddSentPostAsync(October(Line(movies)));
        await AddEventAsync("Games night", Friday.AddDays(1));
        _clock.UtcNow = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(0, await PassAsync());
        Assert.Empty(_discord.Edits);
    }
}
