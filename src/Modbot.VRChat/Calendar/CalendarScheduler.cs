using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.VRChat.Calendar;

/// <summary>
/// Moves every live event along its timeline: opens occurrences, moves repeating events on to their
/// next occurrence, and finishes events with none left (calendar design §2.1, §9).
/// </summary>
/// <remarks>
/// Asks VRChat nothing, so it runs first in every pass and whatever state the gate is in.
/// </remarks>
public sealed class CalendarScheduler
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly CalendarFacts _facts;
    private readonly Random? _random;

    /// <param name="random">The shuffle's randomness; tests pass a seeded one. Null uses the shared one.</param>
    public CalendarScheduler(ModbotContext db, IModbotClock clock, CalendarFacts facts, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);

        _db = db;
        _clock = clock;
        _facts = facts;
        _random = random;
    }

    /// <returns>How many events changed state or occurrence.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        var live = await _db.CalendarEvents
            .Where(e => e.DeletedAt == null
                && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open))
            .ToListAsync(ct).ConfigureAwait(false);

        var steps = new List<(CalendarEvent Event, CalendarStep Step, DateTimeOffset? Occurrence)>();

        foreach (var calendarEvent in live)
        {
            var step = CalendarTimeline.Advance(calendarEvent, now);
            if (step != CalendarStep.None)
                steps.Add((calendarEvent, step, calendarEvent.OccurrenceStartsAt));
        }

        var picked = 0;

        if (steps.Count > 0)
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // An event that picks its world from a list gets it as soon as a date becomes its current
        // one (world lists design §5), in the same pass that moved it there, before the opener runs.
        foreach (var calendarEvent in live.Where(WorldPicker.NeedsDatePick))
        {
            if (await PickWorldAsync(calendarEvent, ct).ConfigureAwait(false))
                picked++;
        }

        if (steps.Count == 0)
            return picked;

        foreach (var (calendarEvent, step, occurrence) in steps)
        {
            var type = step switch
            {
                CalendarStep.Opened => FactType.PlannedEventOpened,
                CalendarStep.Finished => FactType.PlannedEventFinished,
                _ => null,
            };

            if (type is null)
                continue;

            await _facts.RecordAsync(
                type,
                calendarEvent,
                new JsonObject { ["occurrenceStartsAt"] = occurrence?.ToString("O") },
                ct: ct).ConfigureAwait(false);
        }

        return steps.Count + picked;
    }

    /// <summary>Picks the current date's world from the event's list, and records it when a new one was picked.</summary>
    /// <returns>True when the event's world changed.</returns>
    private async Task<bool> PickWorldAsync(CalendarEvent calendarEvent, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var result = await new WorldPicker(_db, _clock, _random).PickForDateAsync(calendarEvent, by: null, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (result is { Outcome: WorldPickOutcome.Picked, Pick: { } pick })
        {
            await _facts.RecordAsync(
                FactType.CalendarWorldPicked,
                calendarEvent,
                await WorldPicker.FactDataAsync(_db, calendarEvent, result, again: false, ct).ConfigureAwait(false),
                worldId: pick.WorldId,
                ct: ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return result?.Outcome is WorldPickOutcome.Picked or WorldPickOutcome.Moved;
    }
}
