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

    public CalendarScheduler(ModbotContext db, IModbotClock clock, CalendarFacts facts)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);

        _db = db;
        _clock = clock;
        _facts = facts;
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

        if (steps.Count == 0)
            return 0;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

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

        return steps.Count;
    }
}
