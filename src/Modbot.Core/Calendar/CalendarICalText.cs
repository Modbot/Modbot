using System.Globalization;
using NodaTime;

namespace Modbot.Core.Calendar;

/// <summary>
/// How a time is written in iCalendar (RFC 5545), shared by the calendar feed and Google Calendar so
/// the two cannot drift apart (Google Calendar design §2). Google takes a repeat as the same
/// <c>RRULE</c> and <c>EXDATE</c> lines the feed writes.
/// </summary>
public static class CalendarICalText
{
    /// <summary>
    /// A zone that is always UTC. Not <c>zone == DateTimeZone.Utc</c>: zones are compared by
    /// reference, and the time zone database's own "UTC" (or "Etc/UTC") is a different object
    /// from <see cref="DateTimeZone.Utc"/>, so an event saved as "UTC" was written with a
    /// <c>TZID=UTC</c> and a VTIMEZONE it does not need.
    /// </summary>
    public static bool IsUtc(DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return zone.MinOffset == Offset.Zero && zone.MaxOffset == Offset.Zero;
    }

    /// <summary>
    /// The value part of a time property, after its name: <c>:20261003T200000Z</c> in UTC, or
    /// <c>;TZID=Europe/London:20261003T200000</c> as wall-clock time in any other zone.
    /// </summary>
    public static string Time(DateTimeOffset at, DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return IsUtc(zone)
            ? ":" + Utc(at)
            : $";TZID={zone.Id}:" + Local(Instant.FromDateTimeOffset(at).InZone(zone).LocalDateTime);
    }

    /// <summary>
    /// One <c>EXDATE</c> line for a date cancelled on its own, at its planned start: the feed's
    /// choice (calendar design §6), a cancelled date is taken out of the repeat itself.
    /// </summary>
    public static string ExDate(DateTimeOffset plannedStartsAt, DateTimeZone zone) =>
        "EXDATE" + Time(plannedStartsAt, zone);

    /// <summary><c>20261003T190000Z</c>.</summary>
    public static string Utc(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary><c>20261003T200000</c>, a wall-clock time with no zone.</summary>
    public static string Local(LocalDateTime at) =>
        at.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
}
