using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The pass that keeps an event's Bluesky posts in line with the event (calendar design, Bluesky
/// step): one post for each date made when its time comes, an event made inside its lead time posted
/// at once, a changed event rewritten or deleted and posted again, and a cancelled one taken down.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CalendarBlueskyPlannerTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeBlueskyPosts _bluesky = new();

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

    /// <summary>A checked Bluesky account with Posting on, and no events or posts.</summary>
    private async Task SetUpAsync(Action<Modbot.Core.Data.Entities.Settings>? change = null)
    {
        await using var context = db.NewContext();
        await context.Posts.ExecuteDeleteAsync(Ct);
        await context.CalendarDateChanges.ExecuteDeleteAsync(Ct);
        await context.CalendarEventPlaces.ExecuteDeleteAsync(Ct);
        await context.CalendarEvents.IgnoreAutoIncludes().ExecuteDeleteAsync(Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.PostsPaused = false;
        settings.BlueskyHandle = FakeBluesky.Handle;
        settings.BlueskyDid = FakeBluesky.Did;
        settings.BlueskyServer = FakeBluesky.Server;
        settings.BlueskyAppPasswordEncrypted = "sealed";
        settings.BlueskyCheckedAt = DateTimeOffset.UnixEpoch;
        settings.BlueskyProblem = null;
        settings.BlueskySignInRefused = false;
        settings.BlueskyStoppedUntil = null;
        settings.BlueskyPostingOn = true;
        change?.Invoke(settings);

        await context.SaveChangesAsync(Ct);
    }

    private async Task<CalendarBlueskyPass> PassAsync()
    {
        await using var context = db.NewContext();
        return await new CalendarBlueskyPlanner(
                context,
                _clock,
                new FactWriter(context, _clock),
                new EventPartitionMaintainer(context, _clock),
                _bluesky)
            .RunOnceAsync(Ct);
    }

    /// <summary>An event with Bluesky on that starts <paramref name="startsIn"/> from now, last saved five minutes ago.</summary>
    private async Task<CalendarEvent> AddEventAsync(TimeSpan startsIn, Action<CalendarEvent>? shape = null)
    {
        var now = _clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Description = "Bring snacks",
            StartsAt = now + startsIn,
            EndsAt = now + startsIn + TimeSpan.FromHours(2),
            TimeZone = "Europe/London",
            Visibility = "public",
            PublishToBluesky = true,
            BlueskyPostBeforeMinutes = 1440,
            State = CalendarEventStates.Scheduled,
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now.AddMinutes(-5),
        };

        shape?.Invoke(e);

        await using var context = db.NewContext();
        context.CalendarEvents.Add(e);
        await context.SaveChangesAsync(Ct);
        return e;
    }

    private async Task EditAsync(Guid id, Action<CalendarEvent> change)
    {
        await using var context = db.NewContext();
        var e = await context.CalendarEvents.SingleAsync(x => x.Id == id, Ct);
        change(e);
        e.Version++;
        e.UpdatedAt = _clock.UtcNow.AddMinutes(-5);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<List<Post>> PostsAsync(Guid eventId)
    {
        await using var context = db.NewContext();
        return await context.Posts.AsNoTracking().Include(p => p.Destinations)
            .Where(p => p.EventId == eventId)
            .OrderBy(p => p.DateStartsAt)
            .ToListAsync(Ct);
    }

    private async Task PostedAsync(Post post, string key = "3l4pqjzzyde2a")
    {
        await using var context = db.NewContext();
        var destination = await context.PostDestinations.SingleAsync(d => d.PostId == post.Id, Ct);
        destination.State = PostDestinationStates.Posted;
        destination.ClientKey = key;
        destination.PostedAt = _clock.UtcNow;
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ADateIsNotPostedBeforeItsPostTime()
    {
        await SetUpAsync();
        var e = await AddEventAsync(TimeSpan.FromDays(3));

        var pass = await PassAsync();

        Assert.Equal(new CalendarBlueskyPass(0, 0, 0), pass);
        Assert.Empty(await PostsAsync(e.Id));
    }

    [Fact]
    public async Task ADateIsPostedOnceItsPostTimeComes_AndOnlyOnce()
    {
        await SetUpAsync();
        var e = await AddEventAsync(TimeSpan.FromDays(3));

        _clock.Advance(TimeSpan.FromDays(2).Add(TimeSpan.FromMinutes(1)));

        Assert.Equal(1, (await PassAsync()).Made);
        Assert.Equal(0, (await PassAsync()).Made);

        var post = Assert.Single(await PostsAsync(e.Id));
        var destination = Assert.Single(post.Destinations);
        Assert.Equal(PostKinds.EventDate, post.Kind);
        Assert.Equal(PostStatuses.Scheduled, post.Status);
        Assert.Equal(e.StartsAt, post.DateStartsAt);
        Assert.Equal(e.StartsAt.AddDays(-1), post.SendAt);
        Assert.Equal(PostNetworks.Bluesky, destination.Network);
        Assert.Equal(PostDestinationStates.Waiting, destination.State);
        Assert.Equal(FakeBluesky.Did, destination.Target);
        Assert.Contains("Movie night", post.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEventMadeInsideItsLeadTimeIsPostedRightAway_WithTheSendTimeSetToNow()
    {
        await SetUpAsync();

        // Starts in three hours, and "one day before" went by long ago.
        var e = await AddEventAsync(TimeSpan.FromHours(3));

        var pass = await PassAsync();

        Assert.Equal(1, pass.Made);
        var post = Assert.Single(await PostsAsync(e.Id));
        Assert.Equal(_clock.UtcNow, post.SendAt);
        Assert.Equal(PostDestinationStates.Waiting, post.Destinations.Single().State);
    }

    [Fact]
    public async Task ALatePostIsNotRewrittenOnEveryPass()
    {
        await SetUpAsync();
        var e = await AddEventAsync(TimeSpan.FromHours(3));

        await PassAsync();
        var first = Assert.Single(await PostsAsync(e.Id));

        _clock.Advance(TimeSpan.FromMinutes(10));
        var second = await PassAsync();

        Assert.Equal(new CalendarBlueskyPass(0, 0, 0), second);
        var after = Assert.Single(await PostsAsync(e.Id));
        Assert.Equal(first.SendAt, after.SendAt);
        Assert.Equal(first.Version, after.Version);
    }

    [Fact]
    public async Task ADateThatHasStartedGetsNoPost()
    {
        await SetUpAsync();
        var e = await AddEventAsync(TimeSpan.FromMinutes(-30));

        var pass = await PassAsync();

        Assert.Equal(0, pass.Made);
        Assert.Empty(await PostsAsync(e.Id));
    }

    [Fact]
    public async Task AnEventSavedInTheLastThirtySecondsWaits()
    {
        await SetUpAsync();
        var e = await AddEventAsync(TimeSpan.FromHours(3), x => x.UpdatedAt = _clock.UtcNow.AddSeconds(-10));

        Assert.Equal(0, (await PassAsync()).Made);

        _clock.Advance(TimeSpan.FromSeconds(25));

        Assert.Equal(1, (await PassAsync()).Made);
        Assert.Single(await PostsAsync(e.Id));
    }

    [Fact]
    public async Task ARepeatingEventGetsOnePostForEachDateAsItsTimeComes()
    {
        await SetUpAsync();

        // Daily, the first date in three hours: its post is due at once, the next date's three hours from now.
        var e = await AddEventAsync(TimeSpan.FromHours(3), x => x.Repeat = CalendarRepeats.Daily);

        await PassAsync();
        var first = Assert.Single(await PostsAsync(e.Id));
        Assert.Equal(e.StartsAt, first.DateStartsAt);

        _clock.Advance(TimeSpan.FromHours(4));
        await PassAsync();
        await PassAsync();

        var posts = await PostsAsync(e.Id);
        Assert.Equal(2, posts.Count);
        Assert.Equal(e.StartsAt.AddDays(1), posts[1].DateStartsAt);
        Assert.Equal(e.StartsAt, posts[1].SendAt);
    }

    [Fact]
    public async Task AWaitingPostIsRewrittenWhenTheEventChanges()
    {
        await SetUpAsync();
        var e = await AddEventAsync(TimeSpan.FromHours(3));
        await PassAsync();
        var before = Assert.Single(await PostsAsync(e.Id));

        await EditAsync(e.Id, x => x.Title = "Movie night, new film");
        var pass = await PassAsync();

        Assert.Equal(1, pass.Changed);
        var after = Assert.Single(await PostsAsync(e.Id));
        Assert.Equal(before.Id, after.Id);
        Assert.Contains("new film", after.Text, StringComparison.Ordinal);
        Assert.Equal(PostDestinationStates.Waiting, after.Destinations.Single().State);
        Assert.Empty(_bluesky.Deletes);
    }

    [Fact]
    public async Task APostedPostIsDeletedAndMadeAgainWhenTheEventChanges()
    {
        await SetUpAsync();
        var e = await AddEventAsync(TimeSpan.FromHours(3));
        await PassAsync();
        await PostedAsync(Assert.Single(await PostsAsync(e.Id)), "oldkey");

        await EditAsync(e.Id, x => x.Description = "Different words");
        var pass = await PassAsync();

        Assert.Equal(1, pass.Deleted);
        Assert.Equal([(FakeBluesky.Did, "oldkey")], _bluesky.Deletes);

        var destination = Assert.Single(Assert.Single(await PostsAsync(e.Id)).Destinations);
        Assert.Equal(PostDestinationStates.Waiting, destination.State);
        Assert.Null(destination.ClientKey);
    }

    [Fact]
    public async Task APostedPostWhoseEventHasNotChangedIsLeftAlone()
    {
        await SetUpAsync();
        var e = await AddEventAsync(TimeSpan.FromHours(3));
        await PassAsync();
        await PostedAsync(Assert.Single(await PostsAsync(e.Id)));

        var pass = await PassAsync();

        Assert.Equal(new CalendarBlueskyPass(0, 0, 0), pass);
        Assert.Empty(_bluesky.Deletes);
    }

    [Fact]
    public async Task ACancelledEventTakesItsPostedPostDown_AndPostsNoMore()
    {
        await SetUpAsync();
        var e = await AddEventAsync(TimeSpan.FromHours(3));
        await PassAsync();
        await PostedAsync(Assert.Single(await PostsAsync(e.Id)), "gonekey");

        await EditAsync(e.Id, x => x.State = CalendarEventStates.Cancelled);
        var pass = await PassAsync();

        Assert.Equal(1, pass.Deleted);
        Assert.Equal([(FakeBluesky.Did, "gonekey")], _bluesky.Deletes);
        Assert.Equal(PostDestinationStates.Removed, Assert.Single(Assert.Single(await PostsAsync(e.Id)).Destinations).State);

        Assert.Equal(new CalendarBlueskyPass(0, 0, 0), await PassAsync());
    }

    [Fact]
    public async Task ACancelledEventSkipsAPostThatWasNotSentYet()
    {
        await SetUpAsync();
        var e = await AddEventAsync(TimeSpan.FromHours(3));
        await PassAsync();

        await EditAsync(e.Id, x => x.State = CalendarEventStates.Cancelled);
        await PassAsync();

        var post = Assert.Single(await PostsAsync(e.Id));
        Assert.Equal(PostDestinationStates.Skipped, post.Destinations.Single().State);
        Assert.Equal(PostStatuses.Cancelled, post.Status);
        Assert.Empty(_bluesky.Deletes);
    }

    [Fact]
    public async Task TurningBlueskyOffOnTheEventTakesItsPostDown()
    {
        await SetUpAsync();
        var e = await AddEventAsync(TimeSpan.FromHours(3));
        await PassAsync();
        await PostedAsync(Assert.Single(await PostsAsync(e.Id)), "offkey");

        await EditAsync(e.Id, x => x.PublishToBluesky = false);
        await PassAsync();

        Assert.Equal([(FakeBluesky.Did, "offkey")], _bluesky.Deletes);
    }

    [Fact]
    public async Task ADeleteBlueskyRefusesStopsTheOthersInTheSamePass()
    {
        await SetUpAsync();
        var first = await AddEventAsync(TimeSpan.FromHours(3));
        var second = await AddEventAsync(TimeSpan.FromHours(4));
        await PassAsync();
        await PostedAsync(Assert.Single(await PostsAsync(first.Id)), "key-one");
        await PostedAsync(Assert.Single(await PostsAsync(second.Id)), "key-two");

        await EditAsync(first.Id, x => x.State = CalendarEventStates.Cancelled);
        await EditAsync(second.Id, x => x.State = CalendarEventStates.Cancelled);
        _bluesky.Answer = PostSiteOutcome.Failed("Bluesky is busy.");

        var pass = await PassAsync();

        Assert.Single(_bluesky.Deletes);
        Assert.Equal(1, pass.Deleted);
    }

    [Fact]
    public async Task NothingHappensWhileThePostingSwitchIsOff()
    {
        await SetUpAsync(s => s.BlueskyPostingOn = false);
        var e = await AddEventAsync(TimeSpan.FromHours(3));

        Assert.Equal(new CalendarBlueskyPass(0, 0, 0), await PassAsync());
        Assert.Empty(await PostsAsync(e.Id));
    }

    [Fact]
    public async Task NothingHappensWhileAllPostingIsPaused()
    {
        await SetUpAsync(s => s.PostsPaused = true);
        var e = await AddEventAsync(TimeSpan.FromHours(3));

        Assert.Equal(new CalendarBlueskyPass(0, 0, 0), await PassAsync());
        Assert.Empty(await PostsAsync(e.Id));
    }

    [Fact]
    public async Task AnEventWithBlueskyOffGetsNoPost()
    {
        await SetUpAsync();
        var e = await AddEventAsync(TimeSpan.FromHours(3), x => x.PublishToBluesky = false);

        Assert.Equal(new CalendarBlueskyPass(0, 0, 0), await PassAsync());
        Assert.Empty(await PostsAsync(e.Id));
    }
}
