using System.Globalization;
using System.Text;
using Modbot.Core.Data.Entities;
using NodaTime;

namespace Modbot.Core.Calendar;

/// <summary>
/// Writes the calendar feed as iCalendar (RFC 5545) (calendar design §6).
/// </summary>
/// <remarks>
/// <para>
/// One <c>VEVENT</c> per event with its repeat as an <c>RRULE</c>, never one per occurrence: a
/// calendar program expands the rule itself, and a stable <c>UID</c> is what lets it update the
/// event it already has instead of adding a copy. A date cancelled on its own is an <c>EXDATE</c>;
/// a date moved or given its own words is one more <c>VEVENT</c> with the same <c>UID</c> and a
/// <c>RECURRENCE-ID</c>, which is how iCalendar says one date of a series differs.
/// </para>
/// <para>
/// <strong>Events stay a while after they end or are cancelled</strong> (calendar design §6, changed
/// 2026-10-01): a finished event for <see cref="KeepEndedFor"/> after its last date, a cancelled one
/// for as long after the cancel, with <c>STATUS:CANCELLED</c>. A program removes an event that leaves
/// its feed without saying anything, so before then a cancelled event simply vanished from members'
/// calendars, and a finished one took its history with it.
/// </para>
/// <para>
/// Times carry the event's <c>TZID</c>, and RFC 5545 requires a <c>VTIMEZONE</c> for every
/// <c>TZID</c> used. Each is written from the time zone database as one observance per change of
/// offset over the years the feed covers, which is valid and needs no rule of its own. A UTC event
/// uses <c>Z</c> times and needs none.
/// </para>
/// </remarks>
public static class CalendarFeedWriter
{
    private const string Crlf = "\r\n";

    /// <summary>How far past the later of now and an event's last date a zone's changes are written.</summary>
    public static readonly Period ZoneYearsAhead = Period.FromYears(2);

    /// <summary>
    /// How long a finished event stays in the feed after its last date ended, and a cancelled one
    /// after it was cancelled: long enough for every calendar program to have read the feed many
    /// times over, short enough that the feed does not grow with every event a group ever ran.
    /// </summary>
    public static readonly TimeSpan KeepEndedFor = TimeSpan.FromDays(30);

    /// <summary>
    /// Whether an event belongs in the feed at <paramref name="now"/>: scheduled or open; finished,
    /// with a date that ended in the last <see cref="KeepEndedFor"/>; or cancelled in that time.
    /// Never a draft or a deleted event.
    /// </summary>
    public static bool Belongs(CalendarEvent calendarEvent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        if (calendarEvent.DeletedAt is not null)
            return false;

        var since = now - KeepEndedFor;

        return calendarEvent.State switch
        {
            CalendarEventStates.Scheduled or CalendarEventStates.Open => true,
            CalendarEventStates.Finished => CalendarRepeat.Between(calendarEvent, since, now).Any(),
            CalendarEventStates.Cancelled => calendarEvent.CancelledAt is { } cancelled && cancelled >= since,
            _ => false,
        };
    }

    /// <param name="calendarName">Shown by calendar programs as the calendar's name.</param>
    /// <param name="events">The events that <see cref="Belongs"/> in the feed; the caller picks them.</param>
    /// <param name="worldNames">World names by id, for <c>LOCATION</c>.</param>
    /// <param name="now">From <c>IModbotClock</c>. Decides how far ahead repeating events' zones are written.</param>
    /// <param name="publicAddress">
    /// Modbot's public address. Each event's <c>URL</c> opens it on the calendar page there; without
    /// an address there is none.
    /// </param>
    public static string Write(
        string calendarName,
        IReadOnlyList<CalendarEvent> events,
        IReadOnlyDictionary<string, string> worldNames,
        DateTimeOffset now,
        string? publicAddress = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(worldNames);

        var output = new StringBuilder();

        Line(output, "BEGIN:VCALENDAR");
        Line(output, "VERSION:2.0");
        Line(output, "PRODID:-//Modbot//Calendar//EN");
        Line(output, "CALSCALE:GREGORIAN");
        Line(output, "METHOD:PUBLISH");
        Line(output, "X-WR-CALNAME:" + Escape(calendarName));

        var ordered = events.OrderBy(e => e.StartsAt).ThenBy(e => e.Id).ToList();

        foreach (var group in ordered
                     .Select(e => (Event: e, Zone: CalendarRepeat.ZoneOf(e)))
                     .Where(e => !IsUtc(e.Zone))
                     .GroupBy(e => e.Zone.Id, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var zone = group.First().Zone;
            var from = Instant.FromDateTimeOffset(group.Min(e => EarliestOf(e.Event)));
            var to = group.Max(e => CoversUntil(e.Event, zone, now));

            WriteZone(output, zone, from, to);
        }

        var address = string.IsNullOrWhiteSpace(publicAddress) ? null : publicAddress.Trim().TrimEnd('/');

        foreach (var calendarEvent in ordered)
            WriteEvent(output, calendarEvent, worldNames, address is null ? null : $"{address}/calendar?event={calendarEvent.Id:D}");

        Line(output, "END:VCALENDAR");

        return output.ToString();
    }

    private static void WriteEvent(
        StringBuilder output, CalendarEvent calendarEvent, IReadOnlyDictionary<string, string> worldNames, string? link)
    {
        var zone = CalendarRepeat.ZoneOf(calendarEvent);

        Line(output, "BEGIN:VEVENT");
        Line(output, $"UID:{calendarEvent.Id:D}@modbot");
        Line(output, "DTSTAMP:" + Utc(calendarEvent.UpdatedAt));
        Line(output, "SEQUENCE:" + calendarEvent.Version.ToString(CultureInfo.InvariantCulture));
        Line(output, "DTSTART" + Time(calendarEvent.StartsAt, zone));
        Line(output, "DTEND" + Time(calendarEvent.EndsAt, zone));

        var rule = Rule(calendarEvent, zone);

        if (rule is not null)
        {
            Line(output, "RRULE:" + rule);

            // A date cancelled on its own is taken out of the repeat (calendar design §6).
            foreach (var dropped in calendarEvent.DateChanges.Where(c => c.Cancelled).OrderBy(c => c.PlannedStartsAt))
                Line(output, "EXDATE" + Time(dropped.PlannedStartsAt, zone));
        }

        var entry = Entry(calendarEvent, worldNames);
        var cancelled = calendarEvent.State == CalendarEventStates.Cancelled;

        WriteText(output, entry.Title, entry.Notes, entry.Location, link, cancelled);
        Line(output, "END:VEVENT");

        if (rule is null)
            return;

        // A date moved, or given its own title or description, is its own VEVENT with the same UID,
        // naming the date it replaces in RECURRENCE-ID: how a calendar program learns that one date
        // of a series differs from the rest.
        foreach (var change in calendarEvent.DateChanges.Where(c => !c.Cancelled && !CalendarDates.IsPlain(c, CalendarRepeat.LengthOf(calendarEvent))).OrderBy(c => c.PlannedStartsAt))
        {
            var occurrence = CalendarRepeat.Changed(change, CalendarRepeat.LengthOf(calendarEvent));
            var description = CalendarRepeat.DescriptionOf(calendarEvent, occurrence);

            Line(output, "BEGIN:VEVENT");
            Line(output, $"UID:{calendarEvent.Id:D}@modbot");
            Line(output, "DTSTAMP:" + Utc(change.UpdatedAt > calendarEvent.UpdatedAt ? change.UpdatedAt : calendarEvent.UpdatedAt));
            Line(output, "SEQUENCE:" + calendarEvent.Version.ToString(CultureInfo.InvariantCulture));
            Line(output, "RECURRENCE-ID" + Time(change.PlannedStartsAt, zone));
            Line(output, "DTSTART" + Time(occurrence.StartsAt, zone));
            Line(output, "DTEND" + Time(occurrence.EndsAt, zone));
            WriteText(
                output,
                CalendarRepeat.TitleOf(calendarEvent, occurrence),
                string.IsNullOrWhiteSpace(description) ? null : description,
                entry.Location,
                link,
                cancelled);
            Line(output, "END:VEVENT");
        }
    }

    /// <summary>The title, description, world, link and status of one VEVENT.</summary>
    /// <remarks>
    /// The link is to the event on Modbot's calendar page, which asks for a sign-in: nothing about
    /// the group's members is in the feed, and following the link shows nobody anything they could
    /// not already see in Modbot.
    /// </remarks>
    private static void WriteText(StringBuilder output, string title, string? notes, string? location, string? link, bool cancelled)
    {
        Line(output, "SUMMARY:" + Escape(title));

        if (notes is not null)
            Line(output, "DESCRIPTION:" + Escape(notes));

        if (location is not null)
            Line(output, "LOCATION:" + Escape(location));

        if (link is not null)
            Line(output, "URL:" + link);

        // A cancelled event says so rather than leaving the feed, so a calendar program can show it
        // as cancelled instead of quietly dropping it.
        Line(output, cancelled ? "STATUS:CANCELLED" : "STATUS:CONFIRMED");
    }

    /// <summary>The earliest instant an event's zone has to be described from: its first start, or a date moved before it.</summary>
    private static DateTimeOffset EarliestOf(CalendarEvent calendarEvent)
    {
        var earliest = calendarEvent.StartsAt;

        foreach (var change in calendarEvent.DateChanges)
        {
            if (change.StartsAt is { } starts && starts < earliest)
                earliest = starts;
        }

        return earliest;
    }

    /// <summary>
    /// What a calendar program shows for an event, before it is escaped into the feed: the event
    /// form's preview of the phone calendar is drawn from this (calendar design §14).
    /// </summary>
    public static CalendarFeedEntry Entry(CalendarEvent calendarEvent, IReadOnlyDictionary<string, string> worldNames)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        ArgumentNullException.ThrowIfNull(worldNames);

        string? location = null;

        if (calendarEvent.WorldId is { Length: > 0 } worldId)
            location = worldNames.TryGetValue(worldId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : worldId;

        return new CalendarFeedEntry(
            calendarEvent.Title,
            string.IsNullOrWhiteSpace(calendarEvent.Description) ? null : calendarEvent.Description,
            location,
            Rule(calendarEvent, CalendarRepeat.ZoneOf(calendarEvent)));
    }

    /// <summary>The <c>RRULE</c> value, or null for an event that does not repeat.</summary>
    public static string? Rule(CalendarEvent calendarEvent, DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var rule = calendarEvent.Repeat switch
        {
            CalendarRepeats.Daily => "FREQ=DAILY",
            CalendarRepeats.Weekly => "FREQ=WEEKLY;BYDAY="
                + string.Join(',', CalendarRepeat.WeeklyDays(calendarEvent, zone).Select(CalendarRepeat.DayName)),
            CalendarRepeats.Monthly => "FREQ=MONTHLY",
            _ => null,
        };

        if (rule is null)
            return null;

        // UTC whatever DTSTART carries: RFC 5545 requires it when DTSTART has a TZID, and when it
        // is UTC. The last moment of the last day an occurrence may start on.
        if (calendarEvent.RepeatUntil is { } until)
            rule += ";UNTIL=" + Utc(LastMomentOf(until, zone).ToDateTimeOffset());

        return rule;
    }

    private static void WriteZone(StringBuilder output, DateTimeZone zone, Instant from, Instant to)
    {
        Line(output, "BEGIN:VTIMEZONE");
        Line(output, "TZID:" + zone.Id);

        foreach (var interval in zone.GetZoneIntervals(from, to <= from ? from + Duration.FromDays(1) : to))
        {
            var offsetTo = interval.WallOffset;
            Offset offsetFrom;
            LocalDateTime start;

            if (interval.HasStart)
            {
                offsetFrom = zone.GetZoneInterval(interval.Start - Duration.Epsilon).WallOffset;
                start = interval.Start.WithOffset(offsetFrom).LocalDateTime;
            }
            else
            {
                offsetFrom = offsetTo;
                start = new LocalDateTime(1970, 1, 1, 0, 0);
            }

            var kind = interval.Savings != Offset.Zero ? "DAYLIGHT" : "STANDARD";

            Line(output, "BEGIN:" + kind);
            Line(output, "DTSTART:" + Local(start));
            Line(output, "TZOFFSETFROM:" + OffsetText(offsetFrom));
            Line(output, "TZOFFSETTO:" + OffsetText(offsetTo));

            if (!string.IsNullOrWhiteSpace(interval.Name))
                Line(output, "TZNAME:" + Escape(interval.Name));

            Line(output, "END:" + kind);
        }

        Line(output, "END:VTIMEZONE");
    }

    /// <summary>The latest instant an event's zone has to be described up to, a date moved past its last one included.</summary>
    private static Instant CoversUntil(CalendarEvent calendarEvent, DateTimeZone zone, DateTimeOffset now)
    {
        var until = RepeatCoversUntil(calendarEvent, zone, now);

        foreach (var change in calendarEvent.DateChanges)
        {
            if (change.EndsAt is { } ends && Instant.FromDateTimeOffset(ends) > until)
                until = Instant.FromDateTimeOffset(ends);
        }

        return until;
    }

    private static Instant RepeatCoversUntil(CalendarEvent calendarEvent, DateTimeZone zone, DateTimeOffset now)
    {
        var end = Instant.FromDateTimeOffset(calendarEvent.EndsAt > now ? calendarEvent.EndsAt : now);

        if (calendarEvent.Repeat is CalendarRepeats.None or "")
            return Instant.FromDateTimeOffset(calendarEvent.EndsAt);

        if (calendarEvent.RepeatUntil is { } until)
        {
            var last = LastMomentOf(until, zone) + Duration.FromTimeSpan(calendarEvent.EndsAt - calendarEvent.StartsAt);
            return last > end ? last : end;
        }

        return end.InUtc().LocalDateTime.Plus(ZoneYearsAhead).InUtc().ToInstant();
    }

    private static Instant LastMomentOf(DateOnly day, DateTimeZone zone) =>
        zone.AtStartOfDay(new LocalDate(day.Year, day.Month, day.Day).PlusDays(1)).ToInstant() - Duration.FromSeconds(1);

    /// <summary>
    /// A zone that is always UTC. Not <c>zone == DateTimeZone.Utc</c>: zones are compared by
    /// reference, and the time zone database's own "UTC" (or "Etc/UTC") is a different object
    /// from <see cref="DateTimeZone.Utc"/>, so an event saved as "UTC" was written with a
    /// <c>TZID=UTC</c> and a VTIMEZONE it does not need.
    /// </summary>
    private static bool IsUtc(DateTimeZone zone) =>
        zone.MinOffset == Offset.Zero && zone.MaxOffset == Offset.Zero;

    private static string Time(DateTimeOffset at, DateTimeZone zone) =>
        IsUtc(zone)
            ? ":" + Utc(at)
            : $";TZID={zone.Id}:" + Local(Instant.FromDateTimeOffset(at).InZone(zone).LocalDateTime);

    private static string Utc(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string Local(LocalDateTime at) =>
        at.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    private static string OffsetText(Offset offset)
    {
        var seconds = offset.Seconds;
        var sign = seconds < 0 ? '-' : '+';
        seconds = Math.Abs(seconds);

        var text = string.Create(CultureInfo.InvariantCulture, $"{sign}{seconds / 3600:00}{seconds / 60 % 60:00}");
        return seconds % 60 == 0 ? text : text + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>RFC 5545 §3.3.11: backslash, semicolon, comma and line breaks.</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var escaped = new StringBuilder(text.Length + 8);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            switch (c)
            {
                case '\\': escaped.Append(@"\\"); break;
                case ';': escaped.Append(@"\;"); break;
                case ',': escaped.Append(@"\,"); break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    escaped.Append(@"\n");
                    break;
                case '\n': escaped.Append(@"\n"); break;
                default:
                    // Other control characters are not allowed in a value at all.
                    if (!char.IsControl(c))
                        escaped.Append(c);
                    break;
            }
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Appends one content line, folded at 75 octets (RFC 5545 §3.1): each continuation starts with
    /// a space, and a character is never split across lines.
    /// </summary>
    private static void Line(StringBuilder output, string line)
    {
        const int limit = 75;
        var used = 0;

        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;

            if (used + size > limit)
            {
                output.Append(Crlf).Append(' ');
                used = 1;
            }

            output.Append(rune.ToString());
            used += size;
        }

        output.Append(Crlf);
    }
}

/// <summary>One event as the calendar feed carries it, before escaping.</summary>
/// <param name="Title">The <c>SUMMARY</c>.</param>
/// <param name="Notes">The <c>DESCRIPTION</c>; null when there is none.</param>
/// <param name="Location">The <c>LOCATION</c>: the world's name, or its id when Modbot has no name for it.</param>
/// <param name="Repeat">The <c>RRULE</c> value; null for an event that does not repeat.</param>
public sealed record CalendarFeedEntry(string Title, string? Notes, string? Location, string? Repeat);
