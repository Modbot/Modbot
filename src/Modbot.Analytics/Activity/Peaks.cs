namespace Modbot.Analytics.Activity;

/// <summary>
/// Picks the highest and the busiest out of a window's day rows.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A tie is always broken towards the earliest.</strong> Two evenings that both reached
/// forty-eight people are a real and ordinary outcome, and a page that showed whichever one the
/// database happened to return first would move between two loads with no new data behind it. The
/// rule is the same for every peak here: the biggest value wins, and among equals the earliest
/// moment wins. It is stated once, tested once, and used everywhere.
/// </para>
/// <para>
/// <strong>An empty window answers null, never zero and never an exception.</strong> "Nothing was
/// recorded" and "the peak was nought" are different statements, and a range with no data has only
/// the first to make.
/// </para>
/// <para>
/// All of this is arithmetic over a handful of rows, so it lives here rather than in SQL: the
/// database does the heavy per-hour folding, and the choice between a few hundred day rows is made
/// where it can be read and tested.
/// </para>
/// </remarks>
public static class Peaks
{
    /// <summary>The most people the group's instances held at one moment, and when.</summary>
    public static PeakCount? MostPeople(IEnumerable<ActivityDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);

        var best = Best(days, d => d.MostPeopleAtOnce, d => d.MostPeopleAt);

        return best is null ? null : new PeakCount(best.MostPeopleAtOnce, best.MostPeopleAt);
    }

    /// <summary>The most instances counted at one moment, and when.</summary>
    public static PeakCount? MostInstances(IEnumerable<ActivityDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);

        var best = Best(days, d => d.MostInstancesAtOnce, d => d.MostInstancesAt);

        return best is null ? null : new PeakCount(best.MostInstancesAtOnce, best.MostInstancesAt);
    }

    /// <summary>
    /// The day with the most people-minutes.
    /// </summary>
    /// <remarks>
    /// Busiest is people-minutes and not the peak, because the two answer different questions and
    /// only one of them is "when should we be staffed". A Tuesday that touched forty for five
    /// minutes and a Saturday that held twenty for six hours have peaks of forty and twenty, and
    /// the Saturday is the busy day.
    /// </remarks>
    public static BusiestDay? Busiest(IEnumerable<ActivityDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);

        ActivityDay? best = null;

        foreach (var day in days)
        {
            if (day.PeopleMinutes <= 0)
                continue;

            if (best is null
                || day.PeopleMinutes > best.PeopleMinutes
                || (day.PeopleMinutes == best.PeopleMinutes && day.Day < best.Day))
                best = day;
        }

        return best is null ? null : new BusiestDay(best.Day, best.PeopleMinutes, best.MostPeopleAtOnce);
    }

    /// <summary>
    /// The single clock hour with the most people-minutes.
    /// </summary>
    /// <remarks>
    /// Every day row carries its own busiest hour, so the busiest hour of the window is the best of
    /// those: the largest of a set of per-day largests is the largest overall. That is what keeps
    /// this an answer over a few hundred rows instead of one over every hour in the range.
    /// </remarks>
    public static BusiestHour? BusiestHour(IEnumerable<ActivityDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);

        ActivityDay? best = null;

        foreach (var day in days)
        {
            if (day.BestHourPeopleMinutes <= 0)
                continue;

            if (best is null
                || day.BestHourPeopleMinutes > best.BestHourPeopleMinutes
                || (day.BestHourPeopleMinutes == best.BestHourPeopleMinutes
                    && day.BestHourStartedAt < best.BestHourStartedAt))
                best = day;
        }

        return best is null
            ? null
            : new BusiestHour(best.BestHourStartedAt, best.BestHourPeopleMinutes, best.BestHourMostPeopleAtOnce);
    }

    /// <summary>
    /// The day whose value is highest, earliest first among equals; null when nothing reached one.
    /// </summary>
    /// <remarks>
    /// A value of nought is not a peak. An instance that was counted all evening and never held
    /// anybody is a real record of an empty evening, and reporting "0 people at 19:04" as the peak
    /// of the week would be a number that is true and says nothing.
    /// </remarks>
    private static ActivityDay? Best(
        IEnumerable<ActivityDay> days,
        Func<ActivityDay, int> value,
        Func<ActivityDay, DateTimeOffset> at)
    {
        ActivityDay? best = null;

        foreach (var day in days)
        {
            if (value(day) <= 0)
                continue;

            if (best is null
                || value(day) > value(best)
                || (value(day) == value(best) && at(day) < at(best)))
                best = day;
        }

        return best;
    }
}
