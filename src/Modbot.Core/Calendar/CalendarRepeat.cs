using Modbot.Core.Data.Entities;
using NodaTime;

namespace Modbot.Core.Calendar;

/// <summary>One occurrence of an event: when it starts and when it ends.</summary>
/// <param name="PlannedStartsAt">
/// When the event's repeat says this date starts. The same as <paramref name="StartsAt"/> unless the
/// date was moved on its own; it is what the date is known by everywhere (calendar design §2.2).
/// </param>
/// <param name="Change">The date's own change, when it has one: a move, its own title or description.</param>
public readonly record struct CalendarOccurrence(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset PlannedStartsAt,
    CalendarDateChange? Change = null)
{
    /// <summary>A date as the repeat has it, with nothing changed.</summary>
    public CalendarOccurrence(DateTimeOffset startsAt, DateTimeOffset endsAt)
        : this(startsAt, endsAt, startsAt)
    {
    }
}

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
    /// starts before <paramref name="to"/>, earliest first -- with the dates changed on their own
    /// (calendar design §2.2): a cancelled date is left out, a moved one is where it was moved to.
    /// </summary>
    public static IEnumerable<CalendarOccurrence> Between(
        CalendarEvent calendarEvent, DateTimeOffset from, DateTimeOffset? to = null)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        if (calendarEvent.DateChanges.Count == 0)
            return PlannedBetween(calendarEvent, from, to);

        return WithChanges(calendarEvent, from, to);
    }

    private static IEnumerable<CalendarOccurrence> WithChanges(
        CalendarEvent calendarEvent, DateTimeOffset from, DateTimeOffset? to)
    {
        var length = LengthOf(calendarEvent);
        var changes = new Dictionary<DateTimeOffset, CalendarDateChange>();

        foreach (var change in calendarEvent.DateChanges)
            changes.TryAdd(change.PlannedStartsAt, change);

        // Every date with a change of its own that still happens, at its own time. They are merged
        // into the repeat's dates by start, so the whole list stays earliest first however far a
        // date was moved.
        var changed = changes.Values
            .Where(c => !c.Cancelled)
            .Select(c => Changed(c, length))
            .OrderBy(o => o.StartsAt)
            .ThenBy(o => o.PlannedStartsAt)
            .ToList();

        var next = 0;

        foreach (var planned in PlannedBetween(calendarEvent, from, to))
        {
            while (next < changed.Count && changed[next].StartsAt <= planned.StartsAt)
            {
                var occurrence = changed[next++];
                if (Inside(occurrence, from, to))
                    yield return occurrence;
            }

            if (changes.ContainsKey(planned.PlannedStartsAt))
                continue;

            yield return planned;
        }

        while (next < changed.Count)
        {
            var occurrence = changed[next++];
            if (Inside(occurrence, from, to))
                yield return occurrence;
        }
    }

    /// <summary>A changed date as it happens: its own times, or the planned ones it kept.</summary>
    public static CalendarOccurrence Changed(CalendarDateChange change, TimeSpan length)
    {
        ArgumentNullException.ThrowIfNull(change);

        var starts = change.StartsAt ?? change.PlannedStartsAt;
        var ends = change.EndsAt is { } end && end > starts ? end : starts + length;
        return new CalendarOccurrence(starts, ends, change.PlannedStartsAt, change);
    }

    private static bool Inside(CalendarOccurrence occurrence, DateTimeOffset from, DateTimeOffset? to) =>
        occurrence.EndsAt > from && (to is not { } end || occurrence.StartsAt < end);

    /// <summary>How long every date lasts unless it was changed: the first one's length.</summary>
    public static TimeSpan LengthOf(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var length = calendarEvent.EndsAt - calendarEvent.StartsAt;
        return length < TimeSpan.Zero ? TimeSpan.Zero : length;
    }

    /// <summary>
    /// The dates the repeat gives, from <paramref name="from"/> to <paramref name="to"/> as
    /// <see cref="Between"/> counts them, with no date's own change applied.
    /// </summary>
    public static IEnumerable<CalendarOccurrence> PlannedBetween(
        CalendarEvent calendarEvent, DateTimeOffset from, DateTimeOffset? to = null)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var zone = ZoneOf(calendarEvent);
        var length = LengthOf(calendarEvent);

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

    /// <summary>
    /// The occurrence that starts at <paramref name="startsAt"/>, as the event's
    /// <see cref="CalendarEvent.OccurrenceStartsAt"/> names it; null when no date starts then.
    /// </summary>
    public static CalendarOccurrence? StartingAt(CalendarEvent calendarEvent, DateTimeOffset startsAt)
    {
        foreach (var occurrence in Between(calendarEvent, startsAt - TimeSpan.FromTicks(1)))
        {
            if (occurrence.StartsAt == startsAt)
                return occurrence;

            if (occurrence.StartsAt > startsAt)
                break;
        }

        return null;
    }

    /// <summary>
    /// The occurrence the event is dealing with now (<see cref="CalendarEvent.OccurrenceStartsAt"/>),
    /// with its own change when it has one, or the first start when it has none yet.
    /// </summary>
    public static CalendarOccurrence Current(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var starts = calendarEvent.OccurrenceStartsAt ?? calendarEvent.StartsAt;
        return StartingAt(calendarEvent, starts)
            ?? new CalendarOccurrence(starts, starts + LengthOf(calendarEvent));
    }

    /// <summary>
    /// The date the repeat starts at <paramref name="plannedStartsAt"/>, as it now happens; null when
    /// it was cancelled, or the repeat has no date then.
    /// </summary>
    public static CalendarOccurrence? ForDate(CalendarEvent calendarEvent, DateTimeOffset plannedStartsAt)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        if (calendarEvent.DateChanges.FirstOrDefault(c => c.PlannedStartsAt == plannedStartsAt) is { } change)
            return change.Cancelled ? null : Changed(change, LengthOf(calendarEvent));

        return IsPlannedDate(calendarEvent, plannedStartsAt)
            ? new CalendarOccurrence(plannedStartsAt, plannedStartsAt + LengthOf(calendarEvent))
            : null;
    }

    /// <summary>Whether the event's repeat has a date starting at <paramref name="plannedStartsAt"/>.</summary>
    public static bool IsPlannedDate(CalendarEvent calendarEvent, DateTimeOffset plannedStartsAt)
    {
        foreach (var occurrence in PlannedBetween(calendarEvent, plannedStartsAt - TimeSpan.FromTicks(1)))
        {
            if (occurrence.StartsAt == plannedStartsAt)
                return true;

            if (occurrence.StartsAt > plannedStartsAt)
                return false;
        }

        return false;
    }

    /// <summary>The dates cancelled on their own whose planned time falls in the range, earliest first.</summary>
    public static IEnumerable<CalendarOccurrence> CancelledBetween(
        CalendarEvent calendarEvent, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var length = LengthOf(calendarEvent);

        return calendarEvent.DateChanges
            .Where(c => c.Cancelled && c.PlannedStartsAt + length > from && c.PlannedStartsAt < to)
            .OrderBy(c => c.PlannedStartsAt)
            .Select(c => new CalendarOccurrence(c.PlannedStartsAt, c.PlannedStartsAt + length, c.PlannedStartsAt, c));
    }

    /// <summary>The title a date goes out with: its own, or the event's.</summary>
    public static string TitleOf(CalendarEvent calendarEvent, CalendarOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return occurrence.Change?.Title is { Length: > 0 } own ? own : calendarEvent.Title;
    }

    /// <summary>The description a date goes out with: its own, or the event's.</summary>
    public static string DescriptionOf(CalendarEvent calendarEvent, CalendarOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return occurrence.Change?.Description is { Length: > 0 } own ? own : calendarEvent.Description;
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
