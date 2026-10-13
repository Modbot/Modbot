using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.VRChat.Calendar;
using Serilog;

namespace Modbot.Api.Features.Calendar;

/// <summary>An event as the form asks: the title and start it is about to save.</summary>
/// <param name="Title">The event's title.</param>
/// <param name="StartsAt">Its first start, <c>2026-10-20T19:00</c>, in <paramref name="TimeZone"/>.</param>
/// <param name="TimeZone">An IANA zone name.</param>
/// <param name="EventId">The event being edited, left out of the answer; null for a new one.</param>
public sealed record CalendarSameEventRequest(string? Title, string? StartsAt, string? TimeZone, Guid? EventId);

/// <summary>One event with the same title, starting at nearly the same time.</summary>
/// <param name="OnVRChat">
/// It was made on VRChat's group calendar, not in Modbot. VRChat's events are brought into Modbot's
/// calendar when its calendar is read, so both kinds are found here.
/// </param>
public sealed record CalendarSameEventView(Guid Id, string Title, DateTimeOffset StartsAt, bool OnVRChat);

/// <param name="Matches">Soonest first. Empty when there is none, or when the title or start was not usable.</param>
/// <param name="VRChatRead">
/// VRChat's calendar was read, or was read in the last five minutes. False when VRChat could not be
/// asked (no managed group, no sign-in, a rate limit); the answer then covers Modbot's own events only.
/// </param>
public sealed record CalendarSameEventsView(IReadOnlyList<CalendarSameEventView> Matches, bool VRChatRead);

/// <summary>
/// Finds events that look like the one being saved, for the warning before a save: the same title
/// and a start within <see cref="DiscordEventDuplicates.StartsWithin"/>, by the rule the Discord
/// duplicate finder uses. Changes nothing, and never refuses a save.
/// </summary>
public static class CalendarSameEvents
{
    /// <summary>
    /// VRChat lists an event in the month it has by its own reckoning, which is not exactly the UTC
    /// month, so the months around the start are read as well when it is this near their edge.
    /// </summary>
    private static readonly TimeSpan MonthMargin = TimeSpan.FromHours(14);

    public static async Task<CalendarSameEventsView> FindAsync(
        ModbotContext db,
        CalendarVRChatReader reader,
        string title,
        DateTimeOffset startsAt,
        Guid? ignoring,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(reader);

        var vrChatRead = await ReadVRChatAsync(reader, startsAt, ct);

        var from = startsAt - DiscordEventDuplicates.StartsWithin;
        var to = startsAt + DiscordEventDuplicates.StartsWithin + TimeSpan.FromTicks(1);

        // Events brought in from VRChat are rows here too, so one look covers both. Drafts are not
        // published anywhere, and a cancelled or finished event is not going to happen.
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

        return new CalendarSameEventsView([.. matches.OrderBy(m => m.StartsAt)], vrChatRead);
    }

    /// <summary>
    /// Brings VRChat's calendar around the start up to date, the way opening the calendar page does.
    /// False, and nothing said, when VRChat cannot be asked: the save must not wait on it.
    /// </summary>
    private static async Task<bool> ReadVRChatAsync(CalendarVRChatReader reader, DateTimeOffset startsAt, CancellationToken ct)
    {
        try
        {
            var result = await reader.ReadAsync(startsAt - MonthMargin, startsAt + MonthMargin, refresh: false, ct);
            return result.Outcome is CalendarReadOutcome.Read or CalendarReadOutcome.Remembered;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Logger.ForContext(LogArea.Name, LogArea.Sync).Warning(ex, "Could not read VRChat's calendar for the duplicate check");
            return false;
        }
    }
}
