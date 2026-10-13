using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Calendar;

/// <summary>An event as the form asks: the title and start it is about to save.</summary>
/// <param name="Title">The event's title.</param>
/// <param name="StartsAt">Its first start, <c>2026-10-20T19:00</c>, in <paramref name="TimeZone"/>.</param>
/// <param name="TimeZone">An IANA zone name.</param>
/// <param name="EventId">The event being edited, left out of the answer; null for a new one.</param>
public sealed record CalendarSameEventRequest(string? Title, string? StartsAt, string? TimeZone, Guid? EventId);

/// <summary>One event with the same title, starting at nearly the same time.</summary>
/// <param name="OnVRChat">
/// It was made on VRChat's group calendar, not in Modbot. VRChat's events are kept in Modbot's
/// calendar from the last time its calendar was read (page open or Refresh), so both kinds are found here.
/// </param>
public sealed record CalendarSameEventView(Guid Id, string Title, DateTimeOffset StartsAt, bool OnVRChat);

/// <param name="Matches">Soonest first. Empty when there is none, or when the title or start was not usable.</param>
public sealed record CalendarSameEventsView(IReadOnlyList<CalendarSameEventView> Matches);

/// <summary>
/// Finds events that look like the one being saved, for the warning before a save: the same title
/// and a start within <see cref="DiscordEventDuplicates.StartsWithin"/>, by the rule the Discord
/// duplicate finder uses. Reads only the events Modbot has stored, never VRChat. Changes nothing, and
/// never refuses a save.
/// </summary>
public static class CalendarSameEvents
{
    public static async Task<CalendarSameEventsView> FindAsync(
        ModbotContext db,
        string title,
        DateTimeOffset startsAt,
        Guid? ignoring,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var from = startsAt - DiscordEventDuplicates.StartsWithin;
        var to = startsAt + DiscordEventDuplicates.StartsWithin + TimeSpan.FromTicks(1);

        // Only what Modbot has stored. Events on VRChat's group calendar are rows here too, brought in
        // when the calendar page was opened or refreshed, so one look covers both without asking VRChat:
        // a save never waits on VRChat (calendar design §9). Drafts are not published anywhere, and a
        // cancelled or finished event is not going to happen.
        var candidates = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.DeletedAt == null
                && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open)
                && (ignoring == null || e.Id != ignoring)
                && e.StartsAt < to
                && (e.Repeat != CalendarRepeats.None || e.EndsAt > from))
            .ToListAsync(ct);

        var matches = new List<CalendarSameEventView>();

        foreach (var candidate in candidates)
        {
            if (!DiscordEventDuplicates.SameTitle(title, candidate.Title))
                continue;

            foreach (var occurrence in CalendarRepeat.Between(candidate, from, to))
            {
                if ((occurrence.StartsAt - startsAt).Duration() <= DiscordEventDuplicates.StartsWithin)
                {
                    matches.Add(new CalendarSameEventView(candidate.Id, candidate.Title, occurrence.StartsAt, candidate.MadeOnVRChat));
                    break;
                }
            }
        }

        return new CalendarSameEventsView([.. matches.OrderBy(m => m.StartsAt)]);
    }
}
