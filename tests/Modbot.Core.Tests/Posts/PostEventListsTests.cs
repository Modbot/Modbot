using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.Core.Tests.Data;
using Modbot.TestSupport;
using NodaTime;

namespace Modbot.Core.Tests.Posts;

/// <summary>
/// Which calendar events a post can list, and the one link each gets (posts design §5.3), against
/// real PostgreSQL. Each test has a database of its own: the Discord server and the public address
/// are on the one settings row.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PostEventListsTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CalendarEvent EventAt(string title, DateTimeOffset startsAt, string state = CalendarEventStates.Scheduled, string accessType = "members") => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        StartsAt = startsAt,
        EndsAt = startsAt.AddHours(1),
        TimeZone = "Europe/London",
        AccessType = accessType,
        State = state,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private async Task WithDatabaseAsync(Func<ModbotContext, Task> test)
    {
        var connectionString = await fixture.CreateMigratedDatabaseAsync(Ct);

        try
        {
            var options = new DbContextOptionsBuilder<ModbotContext>().UseNpgsql(connectionString).Options;
            await using var db = new ModbotContext(options);
            await test(db);
        }
        finally
        {
            await TestDatabases.DropAsync(fixture.ConnectionString, connectionString);
        }
    }

    private static async Task SetUpAsync(ModbotContext db, string? guildId, string? publicAddress)
    {
        var settings = await db.Settings.FirstOrDefaultAsync(s => s.Id == 1, Ct);

        if (settings is null)
        {
            settings = new Settings { Id = 1 };
            db.Settings.Add(settings);
        }

        settings.DiscordGuildId = guildId;
        settings.PublicAddress = publicAddress;
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public Task OnlyPublishedEventsThatAreNotCancelledAreListed() => WithDatabaseAsync(async db =>
    {
        var start = new DateTimeOffset(2026, 10, 18, 19, 0, 0, TimeSpan.Zero);
        db.CalendarEvents.AddRange(
            EventAt("Scheduled", start),
            EventAt("Open", start.AddHours(1), CalendarEventStates.Open),
            EventAt("Draft", start.AddHours(2), CalendarEventStates.Draft),
            EventAt("Cancelled", start.AddHours(3), CalendarEventStates.Cancelled));
        var deleted = EventAt("Deleted", start.AddHours(4));
        deleted.DeletedAt = Now;
        db.CalendarEvents.Add(deleted);
        await db.SaveChangesAsync(Ct);

        var (from, to) = PostEventLists.Range(new LocalDate(2026, 10, 1), new LocalDate(2026, 10, 31), DateTimeZone.Utc);
        var lines = await PostEventLists.InRangeAsync(db, from, to, Ct);

        Assert.Equal(["Scheduled", "Open"], lines.Select(l => l.Title));
    });

    [Fact]
    public Task AMonthWithNoEventsListsNothing() => WithDatabaseAsync(async db =>
    {
        db.CalendarEvents.Add(EventAt("October", new DateTimeOffset(2026, 10, 18, 19, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync(Ct);

        var (from, to) = PostEventLists.Range(new LocalDate(2026, 11, 1), new LocalDate(2026, 11, 30), DateTimeZone.Utc);

        Assert.Empty(await PostEventLists.InRangeAsync(db, from, to, Ct));
    });

    [Fact]
    public Task TheRangeCountsTheLastDayInTheZoneItIsPickedIn() => WithDatabaseAsync(async db =>
    {
        // 02:00 UTC on 1 November is 22:00 on 31 October in New York: inside October there, not in UTC.
        var lateEvening = new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero);
        db.CalendarEvents.Add(EventAt("Late", lateEvening));
        await db.SaveChangesAsync(Ct);

        var newYork = DateTimeZoneProviders.Tzdb["America/New_York"];
        var (from, to) = PostEventLists.Range(new LocalDate(2026, 10, 1), new LocalDate(2026, 10, 31), newYork);
        Assert.Single(await PostEventLists.InRangeAsync(db, from, to, Ct));

        var (utcFrom, utcTo) = PostEventLists.Range(new LocalDate(2026, 10, 1), new LocalDate(2026, 10, 31), DateTimeZone.Utc);
        Assert.Empty(await PostEventLists.InRangeAsync(db, utcFrom, utcTo, Ct));
    });

    [Fact]
    public Task ADiscordEventGivesItsDiscordLinkAndOtherwiseAPublicEventGivesTheJoinLink() => WithDatabaseAsync(async db =>
    {
        await SetUpAsync(db, "111", "https://modbot.example");

        var start = new DateTimeOffset(2026, 10, 18, 19, 0, 0, TimeSpan.Zero);
        var onDiscord = EventAt("On Discord", start, accessType: "public");
        var vrchatOnly = EventAt("VRChat only", start.AddHours(1), accessType: "public");
        var membersOnly = EventAt("Members only", start.AddHours(2));
        db.CalendarEvents.AddRange(onDiscord, vrchatOnly, membersOnly);
        db.CalendarEventPlaces.Add(new CalendarEventPlace
        {
            EventId = onDiscord.Id,
            Place = CalendarPlaces.DiscordEvent,
            State = CalendarPlaceStates.Published,
            ExternalId = "222",
            OccurrenceStartsAt = start,
        });
        await db.SaveChangesAsync(Ct);

        var (from, to) = PostEventLists.Range(new LocalDate(2026, 10, 1), new LocalDate(2026, 10, 31), DateTimeZone.Utc);
        var lines = await PostEventLists.InRangeAsync(db, from, to, Ct);

        Assert.Equal("https://discord.com/events/111/222", lines.Single(l => l.Title == "On Discord").Link);
        Assert.Equal($"https://modbot.example/api/calendar/join/{vrchatOnly.Id:D}", lines.Single(l => l.Title == "VRChat only").Link);
        Assert.Null(lines.Single(l => l.Title == "Members only").Link);
    });

    [Fact]
    public Task ADiscordEventMadeForAnotherDateOfARepeatingEventIsNotItsLink() => WithDatabaseAsync(async db =>
    {
        await SetUpAsync(db, "111", null);

        var first = new DateTimeOffset(2026, 10, 11, 19, 0, 0, TimeSpan.Zero);
        var weekly = EventAt("Weekly", first);
        weekly.Repeat = CalendarRepeats.Weekly;
        weekly.RepeatDays = ["SU"];
        db.CalendarEvents.Add(weekly);
        db.CalendarEventPlaces.Add(new CalendarEventPlace
        {
            EventId = weekly.Id,
            Place = CalendarPlaces.DiscordEvent,
            State = CalendarPlaceStates.Published,
            ExternalId = "222",
            OccurrenceStartsAt = first,
        });
        await db.SaveChangesAsync(Ct);

        var (from, to) = PostEventLists.Range(new LocalDate(2026, 10, 12), new LocalDate(2026, 10, 25), DateTimeZone.Utc);
        var lines = await PostEventLists.InRangeAsync(db, from, to, Ct);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Null(l.Link));
    });

    [Fact]
    public Task AddEventOffersTheNextDateOfEachEventSoonestFirst() => WithDatabaseAsync(async db =>
    {
        var weekly = EventAt("Weekly", new DateTimeOffset(2026, 10, 4, 19, 0, 0, TimeSpan.Zero));
        weekly.Repeat = CalendarRepeats.Weekly;
        weekly.RepeatDays = ["SU"];
        db.CalendarEvents.AddRange(
            weekly,
            EventAt("Once", new DateTimeOffset(2026, 10, 14, 19, 0, 0, TimeSpan.Zero)),
            EventAt("Over", new DateTimeOffset(2026, 10, 1, 19, 0, 0, TimeSpan.Zero), CalendarEventStates.Finished));
        await db.SaveChangesAsync(Ct);

        var choices = await PostEventLists.UpcomingAsync(db, Now, Ct);

        Assert.Equal(["Once", "Weekly"], choices.Select(c => c.Title));
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 19, 0, 0, TimeSpan.Zero), choices[1].StartsAt);
    });

    [Fact]
    public Task AnEventNotPublishedHasNoNextDate() => WithDatabaseAsync(async db =>
    {
        var draft = EventAt("Draft", new DateTimeOffset(2026, 10, 18, 19, 0, 0, TimeSpan.Zero), CalendarEventStates.Draft);
        db.CalendarEvents.Add(draft);
        await db.SaveChangesAsync(Ct);

        Assert.Null(await PostEventLists.NextOfAsync(db, draft.Id, Now, Ct));
    });
}
