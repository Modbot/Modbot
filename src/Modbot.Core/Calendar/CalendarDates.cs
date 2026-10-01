using Modbot.Core.Data.Entities;
using NodaTime;

namespace Modbot.Core.Calendar;

/// <summary>
/// Keeps the dates of a repeating event that were cancelled or changed on their own in line with the
/// event's repeat (calendar design §2.2).
/// </summary>
public static class CalendarDates
{
    /// <summary>
    /// After the event's times, time zone or repeat were changed for every date: each date's own
    /// change moves to the event's date on the same day, and is dropped when the repeat has no date
    /// that day any more.
    /// </summary>
    /// <remarks>
    /// By the day rather than the exact time, so moving the whole series an hour later keeps the
    /// dates cancelled in it cancelled. A date given its own times keeps them; one that only had its
    /// own title or description follows the series to its new time. A change that has been matched
    /// to a different date is sent to VRChat again (§3.1). A date that is over is left as it was: it
    /// is what happened, and Past events counts it as it ran.
    /// </remarks>
    /// <param name="zoneBefore">The event's time zone before the change, which the days were counted in.</param>
    public static void Rematch(CalendarEvent calendarEvent, DateTimeZone zoneBefore, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        ArgumentNullException.ThrowIfNull(zoneBefore);

        if (calendarEvent.DateChanges.Count == 0)
            return;

        var zone = CalendarRepeat.ZoneOf(calendarEvent);
        var length = CalendarRepeat.LengthOf(calendarEvent);
        var taken = new HashSet<DateTimeOffset>();

        foreach (var change in calendarEvent.DateChanges.OrderBy(c => c.PlannedStartsAt).ToList())
        {
            if (change.PlannedStartsAt + length <= now && (change.EndsAt is not { } ends || ends <= now))
            {
                taken.Add(change.PlannedStartsAt);
                continue;
            }

            var day = Instant.FromDateTimeOffset(change.PlannedStartsAt).InZone(zoneBefore).Date;
            var planned = calendarEvent.Repeat == CalendarRepeats.None ? null : DateOn(calendarEvent, zone, day);

            if (planned is not { } starts || !taken.Add(starts))
            {
                calendarEvent.DateChanges.Remove(change);
                continue;
            }

            if (starts != change.PlannedStartsAt)
            {
                change.PlannedStartsAt = starts;
                change.VRChatSentFingerprint = null;
                change.VRChatFailedFingerprint = null;
                change.VRChatError = null;
                change.VRChatErrorAt = null;
            }

            if (IsPlain(change, length))
                calendarEvent.DateChanges.Remove(change);
        }
    }

    /// <summary>A change that changes nothing: the planned time and the event's own words.</summary>
    public static bool IsPlain(CalendarDateChange change, TimeSpan length)
    {
        ArgumentNullException.ThrowIfNull(change);

        var starts = change.StartsAt ?? change.PlannedStartsAt;
        var ends = change.EndsAt ?? starts + length;

        return !change.Cancelled
            && starts == change.PlannedStartsAt
            && ends == change.PlannedStartsAt + length
            && change.Title is null
            && change.Description is null;
    }

    /// <summary>The start of the event's date on <paramref name="day"/> in its zone, or null when it has none that day.</summary>
    private static DateTimeOffset? DateOn(CalendarEvent calendarEvent, DateTimeZone zone, LocalDate day)
    {
        var from = zone.AtStartOfDay(day).ToDateTimeOffset();
        var to = zone.AtStartOfDay(day.PlusDays(1)).ToDateTimeOffset();

        foreach (var occurrence in CalendarRepeat.PlannedBetween(calendarEvent, from - TimeSpan.FromTicks(1), to))
        {
            if (Instant.FromDateTimeOffset(occurrence.StartsAt).InZone(zone).Date == day)
                return occurrence.StartsAt;
        }

        return null;
    }
}
