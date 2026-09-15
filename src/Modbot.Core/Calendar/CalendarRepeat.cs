using Modbot.Core.Data.Entities;
using NodaTime;

namespace Modbot.Core.Calendar;

/// <summary>One occurrence of an event: when it starts and when it ends.</summary>
public readonly record struct CalendarOccurrence(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>
/// Works out when an event happens from the rule it is stored as (calendar design §2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Repeats are counted in the event's own time zone.</strong> A weekly 20:00 event in
/// <c>America/New_York</c> is at 20:00 there in July and in December, which is a different UTC time
/// in each. Every occurrence lasts as long as the first one, measured in elapsed time.
/// </para>
/// <para>
/// NodaTime rather than <see cref="TimeZoneInfo"/>: Modbot builds with invariant globalization,
/// under which Windows cannot read IANA names, and NodaTime carries its own copy of the database.
/// </para>
/// <para>
/// A local time that does not exist (the hour skipped when clocks go forward) moves forward past
/// the gap; one that happens twice takes the earlier. That is NodaTime's lenient rule, and it is
/// what calendar programs do with the same feed.
/// </para>
/// </remarks>
public static class CalendarRepeat
{
    /// <summary>
    /// How many days or months a rule is walked before it is given up on. Fifty years of a daily
    /// event -- far past anything real, and short enough that a bad row cannot hang a pass.
    /// </summary>
    public const int MaxSteps = 20_000;

    private static readonly IReadOnlyDictionary<string, IsoDayOfWeek> DayNames =
        new Dictionary<string, IsoDayOfWeek>(StringComparer.Ordinal)
        {
            ["MO"] = IsoDayOfWeek.Monday,
            ["TU"] = IsoDayOfWeek.Tuesday,
            ["WE"] = IsoDayOfWeek.Wednesday,
            ["TH"] = IsoDayOfWeek.Thursday,
            ["FR"] = IsoDayOfWeek.Friday,
            ["SA"] = IsoDayOfWeek.Saturday,
            ["SU"] = IsoDayOfWeek.Sunday,
        };

    /// <summary>The zone for an IANA name, or null when the database does not know it.</summary>
    public static DateTimeZone? FindZone(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Length > 64
            ? null
            : DateTimeZoneProviders.Tzdb.GetZoneOrNull(name.Trim());

    /// <summary>The event's zone, falling back to UTC for a name that has since left the database.</summary>
    public static DateTimeZone ZoneOf(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return FindZone(calendarEvent.TimeZone) ?? DateTimeZone.Utc;
    }

    /// <summary>The two-letter name of a day, as iCalendar and VRChat spell it.</summary>
    public static string DayName(IsoDayOfWeek day) => DayNames.First(d => d.Value == day).Key;

    /// <summary>Whether this is one of <see cref="CalendarRepeats.Days"/>.</summary>
    public static bool IsDayName(string value) => DayNames.ContainsKey(value);

    /// <summary>
    /// Every occurrence that is still going on at <paramref name="from"/> or starts after it, and
    /// starts before <paramref name="to"/>, earliest first.
    /// </summary>
    public static IEnumerable<CalendarOccurrence> Between(
        CalendarEvent calendarEvent, DateTimeOffset from, DateTimeOffset? to = null)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var zone = ZoneOf(calendarEvent);
        var length = calendarEvent.EndsAt - calendarEvent.StartsAt;

        if (length < TimeSpan.Zero)
            length = TimeSpan.Zero;

        foreach (var local in LocalStarts(calendarEvent, zone))
        {
            var start = zone.AtLeniently(local).ToInstant().ToDateTimeOffset();

            if (to is { } end && start >= end)
                yield break;

            if (start + length > from)
                yield return new CalendarOccurrence(start, start + length);
        }
    }

    /// <summary>The occurrence running at <paramref name="now"/>, or the next one; null when none are left.</summary>
    public static CalendarOccurrence? Next(CalendarEvent calendarEvent, DateTimeOffset now)
    {
        foreach (var occurrence in Between(calendarEvent, now))
            return occurrence;

        return null;
    }

    /// <summary>When an occurrence counts as open: its start, or earlier by the minutes its instance opens early.</summary>
    public static DateTimeOffset OpensAt(CalendarEvent calendarEvent, CalendarOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return calendarEvent.AutoOpen && calendarEvent.OpenMinutesBefore > 0
            ? occurrence.StartsAt - TimeSpan.FromMinutes(calendarEvent.OpenMinutesBefore)
            : occurrence.StartsAt;
    }

    /// <summary>The weekly days an event repeats on, in Monday-first order, never empty.</summary>
    public static IReadOnlyList<IsoDayOfWeek> WeeklyDays(CalendarEvent calendarEvent, DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var days = calendarEvent.RepeatDays
            .Where(DayNames.ContainsKey)
            .Select(d => DayNames[d])
            .ToHashSet();

        // The first start is always an occurrence, so its own day is always one of them.
        days.Add(FirstLocal(calendarEvent, zone).DayOfWeek);

        return [.. days.Order()];
    }

    /// <summary>The first start as a wall-clock time in the event's zone.</summary>
    public static LocalDateTime FirstLocal(CalendarEvent calendarEvent, DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return Instant.FromDateTimeOffset(calendarEvent.StartsAt).InZone(zone).LocalDateTime;
    }

    private static IEnumerable<LocalDateTime> LocalStarts(CalendarEvent calendarEvent, DateTimeZone zone)
    {
        var first = FirstLocal(calendarEvent, zone);
        LocalDate? until = calendarEvent.RepeatUntil is { } u ? new LocalDate(u.Year, u.Month, u.Day) : null;

        switch (calendarEvent.Repeat)
        {
            case CalendarRepeats.Daily:
                for (var i = 0; i < MaxSteps; i++)
                {
                    var start = first.PlusDays(i);
                    if (start.Date > until)
                        yield break;

                    yield return start;
                }

                yield break;

            case CalendarRepeats.Weekly:
                var days = WeeklyDays(calendarEvent, zone).ToHashSet();

                for (var i = 0; i < MaxSteps; i++)
                {
                    var date = first.Date.PlusDays(i);
                    if (date > until)
                        yield break;

                    if (days.Contains(date.DayOfWeek))
                        yield return date + first.TimeOfDay;
                }

                yield break;

            case CalendarRepeats.Monthly:
                for (var i = 0; i < MaxSteps; i++)
                {
                    // The same day of the month, and months without that day are skipped rather than
                    // moved to their last day -- what FREQ=MONTHLY means in iCalendar.
                    var month = new LocalDate(first.Year, first.Month, 1).PlusMonths(i);
                    if (first.Day > month.Calendar.GetDaysInMonth(month.Year, month.Month))
                        continue;

                    var date = new LocalDate(month.Year, month.Month, first.Day);
                    if (date > until)
                        yield break;

                    yield return date + first.TimeOfDay;
                }

                yield break;

            default:
                yield return first;
                yield break;
        }
    }
}
