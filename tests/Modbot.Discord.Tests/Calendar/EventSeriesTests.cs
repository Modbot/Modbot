using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// An event's series: the interested counts kept once per date, set beside who came, and the
/// averages for one month that the monthly post uses.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventSeriesTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Nov = new(2025, 11, 7, 20, 0, 0, TimeSpan.Zero);

    private static async Task<CalendarEvent> AddEventAsync(TestServices services, string title = "Movie night")
    {
        var now = services.Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            StartsAt = Nov,
            EndsAt = Nov.AddHours(2),
            TimeZone = "UTC",
            Repeat = CalendarRepeats.Weekly,
            State = CalendarEventStates.Scheduled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);
        await context.SaveChangesAsync(Ct);
        return e;
    }

    [Fact]
    public async Task EachSideIsWrittenOncePerDate_AndTheTwoSidesShareOneRow()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var e = await AddEventAsync(services);
        var now = services.Clock.UtcNow;

        await using var context = services.Database.NewContext();
        var store = new CalendarInterestStore(context);

        Assert.True(await store.RecordDiscordAsync(e.Id, Nov, 12, now, Ct));
        Assert.False(await store.RecordDiscordAsync(e.Id, Nov, 30, now.AddHours(1), Ct));
        Assert.True(await store.RecordVRChatAsync(e.Id, Nov, 5, now, Ct));
        Assert.False(await store.RecordVRChatAsync(e.Id, Nov, 9, now.AddHours(1), Ct));

        await using var read = services.Database.NewContext();
        var row = await read.CalendarInterests.SingleAsync(i => i.EventId == e.Id, Ct);
        Assert.Equal(12, row.DiscordInterested);
        Assert.Equal(5, row.VRChatInterested);
    }

    [Fact]
    public async Task ADateWithNoCountFromDiscord_IsWrittenAsNone_AndIsNotWrittenAgain()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var e = await AddEventAsync(services);
        var now = services.Clock.UtcNow;

        await using var context = services.Database.NewContext();
        var store = new CalendarInterestStore(context);

        Assert.True(await store.RecordDiscordAsync(e.Id, Nov, null, now, Ct));
        Assert.False(await store.RecordDiscordAsync(e.Id, Nov, 8, now, Ct));

        await using var read = services.Database.NewContext();
        var row = await read.CalendarInterests.SingleAsync(i => i.EventId == e.Id, Ct);
        Assert.Null(row.DiscordInterested);
        Assert.NotNull(row.DiscordTakenAt);
    }

    [Fact]
    public async Task TheHistoryListsDatesEarliestFirst_WithADashWhereACountWasNeverRead()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var e = await AddEventAsync(services);
        var now = services.Clock.UtcNow;

        await using var context = services.Database.NewContext();
        var store = new CalendarInterestStore(context);
        await store.RecordDiscordAsync(e.Id, Nov.AddDays(7), 20, now, Ct);
        await store.RecordVRChatAsync(e.Id, Nov.AddDays(7), 6, now, Ct);
        await store.RecordDiscordAsync(e.Id, Nov, 10, now, Ct);

        var dates = await new EventSeriesHistory(context).ForEventAsync(e.Id, ct: Ct);

        Assert.Equal([Nov, Nov.AddDays(7)], dates.Select(d => d.StartsAt));
        Assert.Equal(10, dates[0].DiscordInterested);
        Assert.Null(dates[0].VRChatInterested);
        Assert.Null(dates[0].Came);
        Assert.Equal(20, dates[1].DiscordInterested);
        Assert.Equal(6, dates[1].VRChatInterested);
    }

    [Fact]
    public async Task ACountReadMoreThanTenMinutesAfterTheStart_IsMarkedLate()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var e = await AddEventAsync(services);

        await using var context = services.Database.NewContext();
        var store = new CalendarInterestStore(context);
        await store.RecordDiscordAsync(e.Id, Nov, 10, Nov.AddMinutes(5), Ct);
        await store.RecordVRChatAsync(e.Id, Nov, 4, Nov.AddMinutes(45), Ct);
        await store.RecordDiscordAsync(e.Id, Nov.AddDays(7), null, Nov.AddDays(7).AddHours(1), Ct);

        var dates = await new EventSeriesHistory(context).ForEventAsync(e.Id, ct: Ct);

        Assert.False(dates[0].DiscordLate);
        Assert.True(dates[0].VRChatLate);

        // No count was read, so there is nothing to call late.
        Assert.False(dates[1].DiscordLate);
    }

    [Fact]
    public async Task TheHistoryKeepsOnlyTheLatestDates_WhenThereAreMoreThanTheLimit()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var e = await AddEventAsync(services);
        var now = services.Clock.UtcNow;

        await using var context = services.Database.NewContext();
        var store = new CalendarInterestStore(context);

        for (var week = 0; week < 5; week++)
            await store.RecordDiscordAsync(e.Id, Nov.AddDays(7 * week), week, now, Ct);

        var dates = await new EventSeriesHistory(context).ForEventAsync(e.Id, limit: 3, ct: Ct);

        Assert.Equal([2, 3, 4], dates.Select(d => d.DiscordInterested));
    }

    [Fact]
    public void TheAverageIsOverTheDatesThatHaveTheFigure_ToOneDecimal()
    {
        Assert.Equal(10.3m, EventSeriesHistory.Average([10, null, 11, 10]));
        Assert.Null(EventSeriesHistory.Average([null, null]));
        Assert.Null(EventSeriesHistory.Average([]));
    }

    [Fact]
    public async Task TheMonthSummaryAveragesEachEventOverItsDatesInThatMonth_MostDatesFirst()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var movies = await AddEventAsync(services, "Movie night");
        var quiz = await AddEventAsync(services, "Quiz");
        var now = services.Clock.UtcNow;

        await using var context = services.Database.NewContext();
        var store = new CalendarInterestStore(context);

        await store.RecordDiscordAsync(movies.Id, Nov, 10, now, Ct);
        await store.RecordDiscordAsync(movies.Id, Nov.AddDays(7), 20, now, Ct);
        await store.RecordVRChatAsync(movies.Id, Nov.AddDays(7), 4, now, Ct);
        await store.RecordDiscordAsync(quiz.Id, Nov, 8, now, Ct);

        // A date in another month is not counted.
        await store.RecordDiscordAsync(movies.Id, Nov.AddDays(30), 99, now, Ct);

        var lines = await new EventMonthSummary(context).ForMonthAsync(new DateOnly(2025, 11, 1), Ct);

        Assert.Equal(["Movie night", "Quiz"], lines.Select(l => l.Title));

        var first = lines[0];
        Assert.Equal(2, first.Dates);
        Assert.Equal(15m, first.DiscordInterested);
        Assert.Equal(4m, first.VRChatInterested);
        Assert.Null(first.Came);

        Assert.Equal(1, lines[1].Dates);
        Assert.Equal(8m, lines[1].DiscordInterested);
        Assert.Null(lines[1].VRChatInterested);
    }

    [Fact]
    public async Task AMonthWithNoCountsAndNoOpenedInstancesHasNoLines()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await AddEventAsync(services);

        await using var context = services.Database.NewContext();

        Assert.Empty(await new EventMonthSummary(context).ForMonthAsync(new DateOnly(2025, 11, 1), Ct));
    }
}
