using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// World lists design §4–§5: the scheduler picks each date's world from the event's list when the date
/// becomes the current one, locks it, goes through the whole list before repeating, and carries on
/// across restarts.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class WorldPickTests(PostgresFixture fixture) : CalendarTestBase(fixture)
{
    private static readonly string[] Games = ["wrld_prop", "wrld_murder", "wrld_among", "wrld_golf", "wrld_quiz"];

    private async Task<Guid> AddListAsync(params string[] worlds)
    {
        var list = new WorldList { Id = Guid.CreateVersion7(), Name = "Game night", CreatedAt = Clock.UtcNow, UpdatedAt = Clock.UtcNow };

        await using var context = Database.NewContext();
        context.WorldLists.Add(list);
        context.WorldListItems.AddRange(worlds.Select((w, i) => new WorldListItem { ListId = list.Id, WorldId = w, Position = i }));
        await context.SaveChangesAsync(Ct);
        return list.Id;
    }

    private async Task<CalendarEvent> EventAsync(Guid id)
    {
        await using var context = Database.NewContext();
        return await context.CalendarEvents.AsNoTracking().SingleAsync(e => e.Id == id, Ct);
    }

    private async Task<List<WorldPick>> DatePicksAsync(Guid id)
    {
        await using var context = Database.NewContext();
        return await context.WorldPicks.AsNoTracking()
            .Where(p => p.EventId == id && p.Kind == WorldPickKinds.Date)
            .OrderBy(p => p.PickedAt)
            .ToListAsync(Ct);
    }

    private Task<CalendarEvent> AddDailyEventAsync(Guid listId) =>
        AddEventAsync(TimeSpan.FromHours(1), e =>
        {
            e.Repeat = CalendarRepeats.Daily;
            e.WorldId = null;
            e.WorldListId = listId;
        });

    [Fact]
    public async Task EachDateGetsAWorld_AndTheWholeListIsPlayedBeforeAnyRepeats_AcrossRestarts()
    {
        var listId = await AddListAsync(Games);
        var e = await AddDailyEventAsync(listId);

        var picked = new List<string>();

        for (var day = 0; day < Games.Length * 2; day++)
        {
            // Each pass is a fresh context and a fresh picker, which is all a restart leaves behind.
            await ScheduleAsync();

            var now = await EventAsync(e.Id);
            Assert.NotNull(now.WorldId);
            Assert.Equal(now.OccurrenceStartsAt, now.WorldPickedFor);
            picked.Add(now.WorldId!);

            Clock.Advance(TimeSpan.FromDays(1));
        }

        Assert.Equal(Games.Order(StringComparer.Ordinal), picked.Take(Games.Length).Order(StringComparer.Ordinal));
        Assert.Equal(Games.Order(StringComparer.Ordinal), picked.Skip(Games.Length).Order(StringComparer.Ordinal));

        // The round boundary does not play the same world twice in a row.
        Assert.NotEqual(picked[Games.Length - 1], picked[Games.Length]);

        Assert.Equal(Games.Length * 2, (await FactsOfTypeAsync(FactType.CalendarWorldPicked)).Count);
    }

    [Fact]
    public async Task ADatesWorldIsLocked_PassAfterPassAndRestartAfterRestart()
    {
        var listId = await AddListAsync(Games);
        var e = await AddDailyEventAsync(listId);

        await ScheduleAsync();
        var first = (await EventAsync(e.Id)).WorldId;

        for (var i = 0; i < 6; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(15));
            await ScheduleAsync();
            Assert.Equal(first, (await EventAsync(e.Id)).WorldId);
        }

        var pick = Assert.Single(await DatePicksAsync(e.Id));
        Assert.Equal(first, pick.WorldId);
        Assert.Null(pick.PickedByUserId);
        Assert.Single(await FactsOfTypeAsync(FactType.CalendarWorldPicked));
    }

    [Fact]
    public async Task MovingTheEventTakesItsPickAlong_RatherThanSpendingAnotherWorld()
    {
        var listId = await AddListAsync(Games);
        var e = await AddDailyEventAsync(listId);

        await ScheduleAsync();
        var first = (await EventAsync(e.Id)).WorldId;

        await EditAsync(e.Id, x =>
        {
            x.StartsAt = x.StartsAt.AddMinutes(30);
            x.EndsAt = x.EndsAt.AddMinutes(30);
        });

        await ScheduleAsync();

        var moved = await EventAsync(e.Id);
        Assert.Equal(first, moved.WorldId);
        Assert.Equal(moved.OccurrenceStartsAt, moved.WorldPickedFor);

        var pick = Assert.Single(await DatePicksAsync(e.Id));
        Assert.Equal(moved.OccurrenceStartsAt, pick.OccurrenceStartsAt);
        Assert.Single(await FactsOfTypeAsync(FactType.CalendarWorldPicked));
    }

    [Fact]
    public async Task AnotherListPutsTheOldPickBack_AndPicksFromTheNewOne()
    {
        var games = await AddListAsync(Games);
        var films = await AddListAsync("wrld_cinema");
        var e = await AddDailyEventAsync(games);

        await ScheduleAsync();
        var old = (await EventAsync(e.Id)).WorldId;

        await EditAsync(e.Id, x =>
        {
            x.WorldListId = films;
            x.WorldId = null;
            x.WorldPickedFor = null;
        });

        await ScheduleAsync();
        Assert.Equal("wrld_cinema", (await EventAsync(e.Id)).WorldId);

        var picks = await DatePicksAsync(e.Id);
        Assert.Equal(2, picks.Count);
        Assert.NotNull(picks.Single(p => p.WorldId == old).PutBackAt);
        Assert.Null(picks.Single(p => p.WorldId == "wrld_cinema").PutBackAt);

        await using var context = Database.NewContext();
        var shuffle = await context.WorldListShuffles.AsNoTracking().SingleAsync(s => s.ListId == games && s.EventId == e.Id, Ct);
        Assert.DoesNotContain(old!, shuffle.Played);
    }

    [Fact]
    public async Task AnEmptyListPicksNothing_AndSaysNothing()
    {
        var listId = await AddListAsync();
        var e = await AddDailyEventAsync(listId);

        await ScheduleAsync();

        Assert.Null((await EventAsync(e.Id)).WorldId);
        Assert.Empty(await DatePicksAsync(e.Id));
        Assert.Empty(await FactsOfTypeAsync(FactType.CalendarWorldPicked));
    }

    [Fact]
    public async Task ADraftIsNeverPicked()
    {
        var listId = await AddListAsync(Games);
        var e = await AddEventAsync(TimeSpan.FromHours(1), x =>
        {
            x.State = CalendarEventStates.Draft;
            x.WorldId = null;
            x.WorldListId = listId;
        });

        await ScheduleAsync();

        Assert.Null((await EventAsync(e.Id)).WorldId);
        Assert.Empty(await DatePicksAsync(e.Id));
    }
}
