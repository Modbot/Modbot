using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Core.Calendar;

/// <summary>What a pick did.</summary>
public enum WorldPickOutcome
{
    /// <summary>A world was picked and recorded.</summary>
    Picked = 0,

    /// <summary>The date already had its world; nothing new was picked.</summary>
    Kept = 1,

    /// <summary>The event was moved, and the world picked for its old date went with it.</summary>
    Moved = 2,

    /// <summary>The list is empty, or no world in it fits.</summary>
    NoneFits = 3,
}

/// <summary>What a pick did, and what it needs to be told as a fact.</summary>
/// <param name="Pick">The pick made, kept or moved. Null when nothing fits.</param>
/// <param name="PutBack">The world that was put back to make room for it, if any.</param>
public sealed record WorldPickResult(WorldPickOutcome Outcome, WorldPick? Pick, string? PutBack = null)
{
    public static WorldPickResult NoneFits { get; } = new(WorldPickOutcome.NoneFits, null);
}

/// <summary>
/// Picks worlds from a world list for an event: the world for a date, and the next game during it
/// (world lists design §4–§6).
/// </summary>
/// <remarks>
/// <para>
/// Every pick runs inside the caller's transaction and starts by taking an advisory lock for the
/// event, so the scheduler and a person, or two people, never read the same shuffle and play the
/// same world. The caller saves, records the fact and commits.
/// </para>
/// <para>
/// Times come from <see cref="IModbotClock"/>; nothing here asks VRChat anything.
/// </para>
/// </remarks>
public sealed class WorldPicker
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly Random _random;

    public WorldPicker(ModbotContext db, IModbotClock clock, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _random = random ?? Random.Shared;
    }

    /// <summary>Whether an event still needs a world picked for its current date.</summary>
    public static bool NeedsDatePick(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return calendarEvent.WorldListId is not null
            && calendarEvent.OccurrenceStartsAt is { } date
            && calendarEvent.WorldPickedFor != date
            && CalendarEventStates.IsLive(calendarEvent.State)
            && calendarEvent.DeletedAt is null;
    }

    /// <summary>
    /// Makes sure the event's current date has its world from the list, and that the event says it.
    /// A date that already has one keeps it; an event moved since its date was picked takes that pick
    /// with it (world lists design §5).
    /// </summary>
    /// <returns>Null when the event does not pick from a list or has no current date.</returns>
    public async Task<WorldPickResult?> PickForDateAsync(CalendarEvent calendarEvent, Guid? by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        await LockAsync(calendarEvent, ct).ConfigureAwait(false);

        if (calendarEvent.WorldListId is not { } listId || calendarEvent.OccurrenceStartsAt is not { } date)
            return null;

        var now = _clock.UtcNow;

        var standing = await _db.WorldPicks
            .Where(p => p.EventId == calendarEvent.Id && p.Kind == WorldPickKinds.Date && p.PutBackAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        var here = standing.FirstOrDefault(p => p.OccurrenceStartsAt == date);

        if (here is not null && here.ListId == listId)
            return Settle(calendarEvent, here, date, WorldPickOutcome.Kept);

        string? putBack = null;

        if (here is not null)
        {
            // The event was changed to another list: its old pick goes back where it came from.
            await PutBackAsync(here, now, ct).ConfigureAwait(false);
            putBack = here.WorldId;
        }
        else
        {
            // A pick for a date that has not started and is no longer the current one belongs to a
            // date the event was moved away from. It moves with the event rather than spending a
            // second world.
            var moved = standing
                .Where(p => p.ListId == listId && p.OccurrenceStartsAt > now)
                .OrderByDescending(p => p.PickedAt)
                .FirstOrDefault();

            if (moved is not null)
            {
                moved.OccurrenceStartsAt = date;
                return Settle(calendarEvent, moved, date, WorldPickOutcome.Moved);
            }
        }

        var items = await ItemsAsync(listId, ct).ConfigureAwait(false);
        var shuffle = await ShuffleAsync(listId, calendarEvent.Id, ct).ConfigureAwait(false);

        WorldShuffle.Bring(shuffle, [.. items.Select(i => i.WorldId)], _random);

        if (WorldShuffle.Next(shuffle, _ => true) is not { } world)
            return putBack is null ? WorldPickResult.NoneFits : new WorldPickResult(WorldPickOutcome.NoneFits, null, putBack);

        var pick = Record(shuffle, calendarEvent, date, WorldPickKinds.Date, listId, world, people: null, by, now);
        return Settle(calendarEvent, pick, date, WorldPickOutcome.Picked) with { PutBack = putBack };
    }

    /// <summary>
    /// Puts the current date's world back and takes the next one in the order (world lists design
    /// §5). The caller checks the date has not opened.
    /// </summary>
    public async Task<WorldPickResult> PickAgainAsync(CalendarEvent calendarEvent, Guid? by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        await LockAsync(calendarEvent, ct).ConfigureAwait(false);

        if (calendarEvent.WorldListId is not { } listId || calendarEvent.OccurrenceStartsAt is not { } date)
            return WorldPickResult.NoneFits;

        var now = _clock.UtcNow;

        var here = await _db.WorldPicks
            .FirstOrDefaultAsync(
                p => p.EventId == calendarEvent.Id && p.Kind == WorldPickKinds.Date && p.PutBackAt == null && p.OccurrenceStartsAt == date,
                ct).ConfigureAwait(false);

        var items = await ItemsAsync(listId, ct).ConfigureAwait(false);
        var shuffle = await ShuffleAsync(listId, calendarEvent.Id, ct).ConfigureAwait(false);
        var after = here is not null && here.ListId == listId ? here.WorldId : null;

        if (after is not null)
            WorldShuffle.PutBack(shuffle, after);

        WorldShuffle.Bring(shuffle, [.. items.Select(i => i.WorldId)], _random);

        if (WorldShuffle.Next(shuffle, _ => true, after) is not { } world)
        {
            // Nothing else to pick: the date keeps its world, still played.
            if (after is not null)
                WorldShuffle.Play(shuffle, after);

            return WorldPickResult.NoneFits;
        }

        if (here is not null)
        {
            here.PutBackAt = now;

            // A pick from another list goes back to that list's own shuffle.
            if (after is null)
                await PutBackAsync(here, now, ct).ConfigureAwait(false);

            // The old row has to stop standing before the new one is written, or the unique index on
            // a date's standing pick refuses the new row.
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var pick = Record(shuffle, calendarEvent, date, WorldPickKinds.Date, listId, world, people: null, by, now);
        return Settle(calendarEvent, pick, date, WorldPickOutcome.Picked) with { PutBack = here?.WorldId };
    }

    /// <summary>
    /// The next game during an event (world lists design §6): the first world not played this round
    /// whose players take <paramref name="people"/>. With <paramref name="instead"/>, that world is put
    /// back and the search starts after it.
    /// </summary>
    public async Task<WorldPickResult> NextGameAsync(
        CalendarEvent calendarEvent, int? people, string? instead, Guid? by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        await LockAsync(calendarEvent, ct).ConfigureAwait(false);

        if (calendarEvent.WorldListId is not { } listId || calendarEvent.OccurrenceStartsAt is not { } date)
            return WorldPickResult.NoneFits;

        var now = _clock.UtcNow;
        var items = await ItemsAsync(listId, ct).ConfigureAwait(false);
        var shuffle = await ShuffleAsync(listId, calendarEvent.Id, ct).ConfigureAwait(false);

        WorldPick? replaced = null;
        if (instead is not null)
        {
            replaced = await _db.WorldPicks
                .Where(p => p.EventId == calendarEvent.Id
                    && p.OccurrenceStartsAt == date
                    && p.Kind == WorldPickKinds.Game
                    && p.PutBackAt == null
                    && p.ListId == listId
                    && p.WorldId == instead)
                .OrderByDescending(p => p.PickedAt)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            if (replaced is not null)
                WorldShuffle.PutBack(shuffle, replaced.WorldId);
        }

        WorldShuffle.Bring(shuffle, [.. items.Select(i => i.WorldId)], _random);

        var ranges = items.ToDictionary(i => i.WorldId, StringComparer.Ordinal);
        bool Fits(string world) =>
            !ranges.TryGetValue(world, out var item) || WorldShuffle.Fits(item.MinPlayers, item.MaxPlayers, people);

        if (WorldShuffle.Next(shuffle, Fits, instead) is not { } world)
        {
            // The world on screen stays the next game.
            if (replaced is not null)
                WorldShuffle.Play(shuffle, replaced.WorldId);

            return WorldPickResult.NoneFits;
        }

        if (replaced is not null)
            replaced.PutBackAt = now;

        var pick = Record(shuffle, calendarEvent, date, WorldPickKinds.Game, listId, world, people, by, now);
        return new WorldPickResult(WorldPickOutcome.Picked, pick, replaced?.WorldId);
    }

    /// <summary>The worlds picked for a date that still stand, newest first: the date's own and its games.</summary>
    public static IQueryable<WorldPick> StandingFor(ModbotContext db, Guid eventId, DateTimeOffset date)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.WorldPicks
            .Where(p => p.EventId == eventId && p.OccurrenceStartsAt == date && p.PutBackAt == null)
            .OrderByDescending(p => p.PickedAt);
    }

    /// <summary>The payload of a <c>modbot.calendar.world.pick</c> fact (world lists design §7), with the names to show.</summary>
    /// <param name="again">A person asked for another world in place of the one picked.</param>
    public static async Task<JsonObject> FactDataAsync(
        ModbotContext db, CalendarEvent calendarEvent, WorldPickResult result, bool again, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(calendarEvent);
        ArgumentNullException.ThrowIfNull(result);

        var pick = result.Pick ?? throw new ArgumentException("Only a pick that was made is a fact.", nameof(result));

        var listName = await db.WorldLists.AsNoTracking()
            .Where(l => l.Id == pick.ListId)
            .Select(l => l.Name)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        string[] ids = result.PutBack is null ? [pick.WorldId] : [pick.WorldId, result.PutBack];
        var names = await db.VRChatWorlds.AsNoTracking()
            .Where(w => ids.Contains(w.WorldId) && w.Name != null)
            .ToDictionaryAsync(w => w.WorldId, w => w.Name!, StringComparer.Ordinal, ct).ConfigureAwait(false);

        var data = new JsonObject
        {
            ["title"] = calendarEvent.Title,
            ["kind"] = pick.Kind,
            ["listId"] = pick.ListId.ToString(),
            ["list"] = listName,
            ["worldId"] = pick.WorldId,
            ["worldName"] = names.GetValueOrDefault(pick.WorldId),
            ["occurrenceStartsAt"] = pick.OccurrenceStartsAt.ToString("O", CultureInfo.InvariantCulture),
        };

        if (pick.People is { } people)
            data["people"] = people;

        if (result.PutBack is { } putBack)
        {
            data["putBack"] = putBack;
            data["putBackName"] = names.GetValueOrDefault(putBack);
        }

        if (again)
            data["again"] = true;

        return data;
    }

    private async Task LockAsync(CalendarEvent calendarEvent, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A world pick runs inside the caller's transaction, so its lock lasts until the commit.");

        var key = $"world-pick:{calendarEvent.Id}";
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({key})::bigint)", ct).ConfigureAwait(false);

        // What the event says now, not what this context read before the lock: another pick, or the
        // edit that changed the list, may have been saved in between. The caller has saved its own
        // changes to the event before asking, so nothing of its is lost.
        if (_db.Entry(calendarEvent).State == EntityState.Unchanged)
            await _db.Entry(calendarEvent).ReloadAsync(ct).ConfigureAwait(false);
    }

    private Task<List<WorldListItem>> ItemsAsync(Guid listId, CancellationToken ct) =>
        _db.WorldListItems
            .Where(i => i.ListId == listId)
            .OrderBy(i => i.Position)
            .ToListAsync(ct);

    private async Task<WorldListShuffle> ShuffleAsync(Guid listId, Guid eventId, CancellationToken ct)
    {
        var shuffle = await _db.WorldListShuffles.FindAsync([listId, eventId], ct).ConfigureAwait(false);
        if (shuffle is not null)
            return shuffle;

        shuffle = new WorldListShuffle { ListId = listId, EventId = eventId, UpdatedAt = _clock.UtcNow };

        // A list deleted since the event was saved has no shuffle to make; the caller finds no items.
        if (await _db.WorldLists.AnyAsync(l => l.Id == listId, ct).ConfigureAwait(false))
            _db.WorldListShuffles.Add(shuffle);

        return shuffle;
    }

    /// <summary>Marks a pick replaced and its world not played, in the shuffle of the list it came from.</summary>
    private async Task PutBackAsync(WorldPick pick, DateTimeOffset now, CancellationToken ct)
    {
        pick.PutBackAt = now;

        var shuffle = await _db.WorldListShuffles.FindAsync([pick.ListId, pick.EventId], ct).ConfigureAwait(false);
        if (shuffle is not null)
        {
            WorldShuffle.PutBack(shuffle, pick.WorldId);
            shuffle.UpdatedAt = now;
        }

        // Saved now: the unique index on a date's standing pick must see this one gone before the
        // next is written.
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private WorldPick Record(
        WorldListShuffle shuffle,
        CalendarEvent calendarEvent,
        DateTimeOffset date,
        string kind,
        Guid listId,
        string world,
        int? people,
        Guid? by,
        DateTimeOffset now)
    {
        WorldShuffle.Play(shuffle, world);
        shuffle.UpdatedAt = now;

        var pick = new WorldPick
        {
            Id = Guid.CreateVersion7(),
            EventId = calendarEvent.Id,
            OccurrenceStartsAt = date,
            Kind = kind,
            ListId = listId,
            WorldId = world,
            People = people,
            PickedAt = now,
            PickedByUserId = by,
        };

        _db.WorldPicks.Add(pick);
        return pick;
    }

    private static WorldPickResult Settle(CalendarEvent calendarEvent, WorldPick pick, DateTimeOffset date, WorldPickOutcome outcome)
    {
        calendarEvent.WorldId = pick.WorldId;
        calendarEvent.WorldPickedFor = date;
        return new WorldPickResult(outcome, pick);
    }
}
