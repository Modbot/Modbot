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

            // Asked before anything below clears it: whether VRChat or Google may hold this date as
            // changed.
            var sentToVRChat = MayBeOnVRChat(change);
            var sentToGoogle = MayBeOnGoogle(change);

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
                change.VRChatId = null;
                change.VRChatSentFingerprint = null;
                change.VRChatFailedFingerprint = null;
                change.VRChatError = null;
                change.VRChatErrorAt = null;
                ForgetOnGoogle(change);
            }

            if (IsPlain(change, length) && !sentToVRChat && !sentToGoogle)
                calendarEvent.DateChanges.Remove(change);
        }
    }

    /// <summary>
    /// Whether VRChat may still hold this date as it was changed: something was sent for it, or its
    /// VRChat id was found. A date put back as planned keeps its row until VRChat has been sent the
    /// planned time and words; the publisher removes it then (calendar design §2.2).
    /// </summary>
    public static bool MayBeOnVRChat(CalendarDateChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.VRChatSentFingerprint is not null || change.VRChatId is not null || change.VRChatSentStartsAt is not null;
    }

    /// <summary>
    /// Whether Google Calendar may still hold this date as it was changed: something was sent for it
    /// on its own (Google Calendar design §3.5). Like VRChat, a date put back as planned keeps its
    /// row until Google has been sent the planned time and words; the Google publisher clears this
    /// then.
    /// </summary>
    public static bool MayBeOnGoogle(CalendarDateChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.GoogleSentFingerprint is not null;
    }

    /// <summary>
    /// A change that can be forgotten: it changes nothing, and neither VRChat nor Google holds it as
    /// changed. Every place that keeps a date of its own asks this before the row goes, so one
    /// place dropping the row cannot leave the other's moved copy behind for good (Google Calendar
    /// design §3.5; until 2026-10-03 VRChat removed the row as soon as it had the plain date).
    /// </summary>
    public static bool CanForget(CalendarDateChange change, TimeSpan length) =>
        IsPlain(change, length) && !MayBeOnVRChat(change) && !MayBeOnGoogle(change);

    /// <summary>VRChat holds nothing of this date of its own any more.</summary>
    public static void ForgetOnVRChat(CalendarDateChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        change.VRChatId = null;
        change.VRChatSentStartsAt = null;
        change.VRChatSentFingerprint = null;
        change.VRChatFailedFingerprint = null;
        change.VRChatError = null;
        change.VRChatErrorAt = null;
    }

    /// <summary>Google holds nothing of this date of its own any more.</summary>
    public static void ForgetOnGoogle(CalendarDateChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        change.GoogleSentFingerprint = null;
        change.GoogleFailedFingerprint = null;
        change.GoogleError = null;
        change.GoogleErrorAt = null;
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
