using Modbot.Core.Data.Entities;
using NodaTime;
using NodaTime.TimeZones;

namespace Modbot.AI.Insights;

/// <summary>
/// When a schedule says an insight is written, worked out in the schedule's own time zone
/// (AI insights design §3).
/// </summary>
/// <remarks>
/// NodaTime rather than <see cref="TimeZoneInfo"/>: Modbot builds with invariant globalization, under
/// which Windows cannot look up an IANA name such as <c>Europe/London</c>, and NodaTime carries its
/// own copy of the time zone database so the answer is the same on every machine.
/// </remarks>
public static class InsightTimes
{
    public const int MaxTimeZoneLength = 64;

    /// <summary>The time zone for an IANA name, or null when the name is not one.</summary>
    public static DateTimeZone? FindTimeZone(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxTimeZoneLength)
            return null;

        return DateTimeZoneProviders.Tzdb.GetZoneOrNull(name.Trim());
    }

    /// <summary>
    /// The zone a stored name means when it is used: UTC when none is set, or when the name is no
    /// longer in the database. Saving checks the name, so the second is rare, and writing on time in
    /// UTC beats not writing at all.
    /// </summary>
    public static DateTimeZone ZoneOrUtc(string? name) => FindTimeZone(name) ?? DateTimeZone.Utc;

    /// <summary>
    /// The most recent moment, at or before <paramref name="now"/>, that the schedule names.
    /// </summary>
    /// <param name="hour">0 to 23, in <paramref name="zone"/>.</param>
    /// <param name="weekday">0 is Sunday, as <see cref="DayOfWeek"/> counts. Ignored for a daily schedule.</param>
    /// <remarks>
    /// An hour that does not exist because the clocks went forward is taken as the hour after; an hour
    /// that happens twice because they went back is taken the first time.
    /// </remarks>
    public static DateTimeOffset LatestMoment(DateTimeOffset now, string every, int hour, int weekday, DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        hour = Math.Clamp(hour, 0, 23);
        weekday = Math.Clamp(weekday, 0, 6);

        var instant = Instant.FromDateTimeOffset(now);
        var date = instant.InZone(zone).Date;

        var daily = every == InsightKinds.EveryDay;

        if (!daily)
        {
            var wanted = weekday == 0 ? 7 : weekday; // NodaTime counts Monday as 1 and Sunday as 7.
            date = date.PlusDays(-(((int)date.DayOfWeek - wanted + 7) % 7));
        }

        var step = daily ? 1 : 7;
        var moment = At(date, hour, zone);

        // At most one step back: today's (or this week's) moment may still be ahead.
        while (moment > instant)
        {
            date = date.PlusDays(-step);
            moment = At(date, hour, zone);
        }

        return moment.ToDateTimeOffset();
    }

    private static Instant At(LocalDate date, int hour, DateTimeZone zone)
        => zone.ResolveLocal(date.At(new LocalTime(hour, 0)), Resolvers.LenientResolver).ToInstant();
}
