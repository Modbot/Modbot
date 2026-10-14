using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Bluesky;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Google;
using Modbot.Core.Posts;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The weekly line-up (Settings → Bluesky, Weekly line-up): once a week, at the time set, one ordinary
/// Bluesky post lists the next seven days' public dates. A week with none makes no post, a cancelled
/// date and a members-only event are left out, a repeating event gives each of its dates, a time zone
/// moves the times, and the same week never gets a second post.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CalendarBlueskyLineupPassTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Wednesday noon in UTC. The line-up is set for Wednesday 12:00 unless a test says otherwise.
    private static readonly DateTimeOffset Wednesday = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private const string ThatWeek = "2026-10-12";

    private readonly PostgresFixture _db;
    private readonly FakeClock _clock = new(Wednesday);

    public CalendarBlueskyLineupPassTests(PostgresFixture db) => _db = db;

    /// <summary>A checked Bluesky account with Posting on and the line-up on for Wednesday 12:00 UTC, and no events or posts.</summary>
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
        settings.BlueskyCheckedAt = Wednesday.AddHours(-1);
        settings.BlueskyProblem = null;
        settings.BlueskyPostingOn = true;
        settings.BlueskyStoppedUntil = null;
        settings.ManagedGroupName = "Our group";
        settings.GoogleCalendarId = null;
        settings.GooglePublic = null;
        settings.BlueskyLineupOn = true;
        settings.BlueskyLineupDay = 3;
        settings.BlueskyLineupHour = 12;
        settings.BlueskyLineupTimeZone = null;
        settings.BlueskyLineupHandledThrough = null;
        change?.Invoke(settings);

        await context.SaveChangesAsync(Ct);
    }

    /// <summary>A public one-off event starting <paramref name="startsIn"/> from now, or a weekly or daily one when <paramref name="repeat"/> says so.</summary>
    private async Task<Guid> AddEventAsync(
        string title, TimeSpan startsIn, Action<CalendarEvent>? change = null, string repeat = CalendarRepeats.None)
    {
        var startsAt = Wednesday + startsIn;

        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            StartsAt = startsAt,
            EndsAt = startsAt.AddHours(2),
            OccurrenceStartsAt = startsAt,
            TimeZone = "UTC",
            Repeat = repeat,
            Visibility = "public",
            AccessType = "public",
            PublishToVRChat = true,
            PublishToBluesky = false,
            State = CalendarEventStates.Scheduled,
            CreatedAt = Wednesday.AddDays(-30),
            UpdatedAt = Wednesday.AddDays(-30),
        };

        change?.Invoke(e);

        await using var context = _db.NewContext();
        context.CalendarEvents.Add(e);
        await context.SaveChangesAsync(Ct);

        return e.Id;
    }

    private async Task<bool> PassAsync()
    {
        await using var context = _db.NewContext();
        return await new CalendarBlueskyLineupPass(context, _clock).RunOnceAsync(Ct);
    }

    private async Task<List<Post>> LineupsAsync()
    {
        await using var context = _db.NewContext();
        return await context.Posts.AsNoTracking().Include(p => p.Destinations)
            .Where(p => p.Kind == PostKinds.Lineup)
            .OrderBy(p => p.SendAt)
            .ToListAsync(Ct);
    }

    private async Task<Core.Data.Entities.Settings> SettingsAsync()
    {
        await using var context = _db.NewContext();
        return await context.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct);
    }

    [Fact]
    public async Task AWeekWithNoDatesMakesNoPost_AndIsNotLookedAtAgain()
    {
        await SetUpAsync();
        await AddEventAsync("Next week", TimeSpan.FromDays(8));

        Assert.False(await PassAsync());
        Assert.Empty(await LineupsAsync());
        Assert.Equal(Wednesday, (await SettingsAsync()).BlueskyLineupHandledThrough);

        // A date added since is not this week's line-up: the time was dealt with.
        await AddEventAsync("Added late", TimeSpan.FromDays(1));
        Assert.False(await PassAsync());
        Assert.Empty(await LineupsAsync());
    }

    [Fact]
    public async Task AFewDatesMakeOneOrdinaryBlueskyPost_AtTheChosenTime()
    {
        await SetUpAsync();
        await AddEventAsync("Karaoke", TimeSpan.FromDays(5).Add(TimeSpan.FromHours(8)));          // Monday 20:00
        await AddEventAsync("Movie night", TimeSpan.FromDays(1).Add(TimeSpan.FromHours(9)));      // Thursday 21:00

        Assert.True(await PassAsync());

        var post = Assert.Single(await LineupsAsync());
        Assert.Equal(PostStatuses.Scheduled, post.Status);
        Assert.Equal(Wednesday, post.SendAt);
        Assert.Equal(ThatWeek, post.ExternalKey);
        Assert.Null(post.EventId);
        Assert.Equal("This week at Our group", post.Title);

        var destination = Assert.Single(post.Destinations);
        Assert.Equal(PostNetworks.Bluesky, destination.Network);
        Assert.Equal(PostDestinationStates.Waiting, destination.State);
        Assert.Equal(FakeBluesky.Did, destination.Target);
        Assert.Equal("This week at Our group\nThu 21:00 Movie night\nMon 20:00 Karaoke", destination.TextOverride);
        Assert.Equal(destination.TextOverride, PostTexts.Bluesky(post, destination));
    }

    [Fact]
    public async Task ManyDatesListAsManyAsFit_ThenSayHowManyMore()
    {
        await SetUpAsync();

        for (var i = 0; i < 20; i++)
            await AddEventAsync("Dance night " + i, TimeSpan.FromHours(1 + i * 7));

        Assert.True(await PassAsync());

        var text = Assert.Single(Assert.Single(await LineupsAsync()).Destinations).TextOverride!;
        Assert.True(BlueskyText.Fits(text));
        Assert.Matches(@"\n\+\d+ more$", text);
    }

    [Fact]
    public async Task OnlyDatesEveryoneCanSeeGoIn_WhetherOrNotTheEventPostsToBluesky_AndCancelledDatesAreLeftOut()
    {
        await SetUpAsync();
        await AddEventAsync("Public without chip", TimeSpan.FromDays(1));
        await AddEventAsync("Public with chip", TimeSpan.FromDays(2), e => e.PublishToBluesky = true);
        await AddEventAsync("Members only", TimeSpan.FromDays(1), e => e.Visibility = "group");
        await AddEventAsync("Needs a role", TimeSpan.FromDays(1), e => e.VRChatRoleIds = ["grol_1"]);
        await AddEventAsync("Cancelled event", TimeSpan.FromDays(1), e => e.State = CalendarEventStates.Cancelled);
        await AddEventAsync("Deleted event", TimeSpan.FromDays(1), e => e.DeletedAt = Wednesday.AddDays(-1));

        var daily = await AddEventAsync("Daily social", TimeSpan.FromDays(3), repeat: CalendarRepeats.Daily);
        await using (var context = _db.NewContext())
        {
            context.CalendarDateChanges.Add(new CalendarDateChange
            {
                Id = Guid.CreateVersion7(),
                EventId = daily,
                PlannedStartsAt = Wednesday.AddDays(4),
                Cancelled = true,
                CreatedAt = Wednesday,
                UpdatedAt = Wednesday,
            });
            await context.SaveChangesAsync(Ct);
        }

        Assert.True(await PassAsync());

        var text = Assert.Single(Assert.Single(await LineupsAsync()).Destinations).TextOverride!;
        Assert.Contains("Public without chip", text);
        Assert.Contains("Public with chip", text);
        Assert.DoesNotContain("Members only", text);
        Assert.DoesNotContain("Needs a role", text);
        Assert.DoesNotContain("Cancelled event", text);
        Assert.DoesNotContain("Deleted event", text);

        // From Saturday up to next Wednesday's noon is four dates, and Sunday's is cancelled.
        var dailyLines = text.Split('\n').Count(l => l.EndsWith("Daily social", StringComparison.Ordinal));
        Assert.Equal(3, dailyLines);
        Assert.DoesNotContain("Sun 12:00 Daily social", text);
    }

    [Fact]
    public async Task TheTimesAreInTheLineupsTimeZone()
    {
        // Wednesday 12:00 in Tokyo is 03:00 UTC.
        await SetUpAsync(s => s.BlueskyLineupTimeZone = "Asia/Tokyo");
        _clock.UtcNow = new DateTimeOffset(2026, 10, 14, 3, 0, 0, TimeSpan.Zero);

        await AddEventAsync("Karaoke", TimeSpan.FromHours(1));   // 13:00 UTC is Wed 22:00 in Tokyo

        Assert.True(await PassAsync());

        var post = Assert.Single(await LineupsAsync());
        Assert.Equal(_clock.UtcNow, post.SendAt);
        Assert.Equal("Asia/Tokyo", post.TimeZone);
        Assert.Equal("This week at Our group\nWed 22:00 Karaoke", post.Destinations.Single().TextOverride);
    }

    [Fact]
    public async Task ThePublicCalendarPageIsTheLastLine_WhenTheGroupHasOne()
    {
        await SetUpAsync(s =>
        {
            s.GoogleCalendarId = "group@group.calendar.google.com";
            s.GoogleCalendarTimeZone = "Europe/London";
            s.GooglePublic = GooglePublic.All;
        });
        await AddEventAsync("Karaoke", TimeSpan.FromDays(1));

        Assert.True(await PassAsync());

        var text = Assert.Single(Assert.Single(await LineupsAsync()).Destinations).TextOverride!;
        var link = GoogleCalendarIds.PublicPageLink("group@group.calendar.google.com", "Europe/London");
        Assert.EndsWith("\n" + link, text);
        Assert.Equal(link, BlueskyText.CardLink(text));
    }

    [Fact]
    public async Task TheSameWeekNeverGetsASecondPost_AndALaterChangeLeavesThePostAlone()
    {
        await SetUpAsync();
        var id = await AddEventAsync("Karaoke", TimeSpan.FromDays(1));

        Assert.True(await PassAsync());
        var text = Assert.Single(await LineupsAsync()).Destinations.Single().TextOverride;

        Assert.False(await PassAsync());

        // The event is cancelled afterwards: the post is a snapshot of that week.
        await using (var context = _db.NewContext())
        {
            var e = await context.CalendarEvents.SingleAsync(x => x.Id == id, Ct);
            e.State = CalendarEventStates.Cancelled;
            await context.SaveChangesAsync(Ct);
        }

        Assert.False(await PassAsync());
        Assert.Equal(text, Assert.Single(await LineupsAsync()).Destinations.Single().TextOverride);

        // The time changed within the week, and claimed again: still the one post.
        await using (var context = _db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.BlueskyLineupHour = 11;
            settings.BlueskyLineupHandledThrough = null;
            await context.SaveChangesAsync(Ct);
        }

        Assert.False(await PassAsync());
        Assert.Single(await LineupsAsync());
    }

    [Fact]
    public async Task TheNextWeekGetsItsOwnPost()
    {
        await SetUpAsync();
        await AddEventAsync("Karaoke", TimeSpan.FromDays(1), repeat: CalendarRepeats.Daily);

        Assert.True(await PassAsync());

        _clock.Advance(TimeSpan.FromDays(7));
        Assert.True(await PassAsync());

        var posts = await LineupsAsync();
        Assert.Equal(["2026-10-12", "2026-10-19"], posts.Select(p => p.ExternalKey));
        Assert.Equal(Wednesday.AddDays(7), posts[1].SendAt);
    }

    [Fact]
    public async Task NothingIsMadeBeforeTheTimeComes_OrWhenItWentByMoreThanAnHourAgo_OrWhenItWasDealtWithAlready()
    {
        // Thursday 12:00 is still ahead on a Wednesday: the latest time is last Thursday's, six days ago.
        await SetUpAsync(s => s.BlueskyLineupDay = 4);
        await AddEventAsync("Karaoke", TimeSpan.FromDays(1));

        Assert.False(await PassAsync());
        Assert.Empty(await LineupsAsync());

        // An hour and a minute late.
        await SetUpAsync();
        await AddEventAsync("Karaoke", TimeSpan.FromDays(1));
        _clock.UtcNow = Wednesday.AddMinutes(61);
        Assert.False(await PassAsync());
        Assert.Empty(await LineupsAsync());

        // Turning it on at 12:30 dealt with 12:00.
        await SetUpAsync(s => s.BlueskyLineupHandledThrough = Wednesday);
        await AddEventAsync("Karaoke", TimeSpan.FromDays(1));
        _clock.UtcNow = Wednesday.AddMinutes(30);
        Assert.False(await PassAsync());
        Assert.Empty(await LineupsAsync());
    }

    [Fact]
    public async Task NothingIsMadeWhileTheLineupIsOff_PostingIsOff_OrPostingIsPaused()
    {
        foreach (var change in new Action<Core.Data.Entities.Settings>[]
        {
            s => s.BlueskyLineupOn = false,
            s => s.BlueskyPostingOn = false,
            s => s.PostsPaused = true,
        })
        {
            await SetUpAsync(change);
            await AddEventAsync("Karaoke", TimeSpan.FromDays(1));

            Assert.False(await PassAsync());
            Assert.Empty(await LineupsAsync());

            // Not claimed either: when posting comes back within the hour, the week still gets its post.
            Assert.Null((await SettingsAsync()).BlueskyLineupHandledThrough);
        }
    }
}
