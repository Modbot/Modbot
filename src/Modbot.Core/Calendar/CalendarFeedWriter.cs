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
/// event it already has instead of adding a copy.
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

    /// <param name="calendarName">Shown by calendar programs as the calendar's name.</param>
    /// <param name="events">Only live events belong in the feed; the caller picks them.</param>
    /// <param name="worldNames">World names by id, for <c>LOCATION</c>.</param>
    /// <param name="now">From <c>IModbotClock</c>. Decides how far ahead repeating events' zones are written.</param>
    public static string Write(
        string calendarName,
        IReadOnlyList<CalendarEvent> events,
        IReadOnlyDictionary<string, string> worldNames,
        DateTimeOffset now)
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
                     .Where(e => e.Zone != DateTimeZone.Utc)
                     .GroupBy(e => e.Zone.Id, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var zone = group.First().Zone;
            var from = Instant.FromDateTimeOffset(group.Min(e => e.Event.StartsAt));
            var to = group.Max(e => CoversUntil(e.Event, zone, now));

            WriteZone(output, zone, from, to);
        }

        foreach (var calendarEvent in ordered)
            WriteEvent(output, calendarEvent, worldNames);

        Line(output, "END:VCALENDAR");

        return output.ToString();
    }

    private static void WriteEvent(
        StringBuilder output, CalendarEvent calendarEvent, IReadOnlyDictionary<string, string> worldNames)
    {
        var zone = CalendarRepeat.ZoneOf(calendarEvent);

        Line(output, "BEGIN:VEVENT");
        Line(output, $"UID:{calendarEvent.Id:D}@modbot");
        Line(output, "DTSTAMP:" + Utc(calendarEvent.UpdatedAt));
        Line(output, "SEQUENCE:" + calendarEvent.Version.ToString(CultureInfo.InvariantCulture));
        Line(output, "DTSTART" + Time(calendarEvent.StartsAt, zone));
        Line(output, "DTEND" + Time(calendarEvent.EndsAt, zone));

        if (Rule(calendarEvent, zone) is { } rule)
            Line(output, "RRULE:" + rule);

        Line(output, "SUMMARY:" + Escape(calendarEvent.Title));

        if (!string.IsNullOrWhiteSpace(calendarEvent.Description))
            Line(output, "DESCRIPTION:" + Escape(calendarEvent.Description));

        if (calendarEvent.WorldId is { Length: > 0 } worldId)
        {
            var place = worldNames.TryGetValue(worldId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : worldId;
            Line(output, "LOCATION:" + Escape(place));
        }

        Line(output, "STATUS:CONFIRMED");
        Line(output, "END:VEVENT");
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

    /// <summary>The latest instant an event's zone has to be described up to.</summary>
    private static Instant CoversUntil(CalendarEvent calendarEvent, DateTimeZone zone, DateTimeOffset now)
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

    private static string Time(DateTimeOffset at, DateTimeZone zone) =>
        zone == DateTimeZone.Utc
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
