namespace Modbot.Api.Features.Analytics.Instances;

/// <summary>
/// 168 buckets, one per hour of the week, in UTC. Index = (Monday = 0 … Sunday = 6) × 24 + hour.
/// </summary>
/// <param name="Arrivals">People seen arriving in a group instance, from client presence reports.</param>
/// <param name="Opened">Group instances opened, from the audit log.</param>
public sealed record HourOfWeek(IReadOnlyList<decimal> Arrivals, IReadOnlyList<decimal> Opened);

/// <param name="Opened">Instances opened per day (daily totals).</param>
/// <param name="Closed">Instances closed per day (daily totals).</param>
/// <param name="MostOpenAtOnce">
/// The most instances open at the same moment, per day. An instance with no close on record is
/// open until the last thing Modbot saw happen in it.
/// </param>
/// <param name="MostPeopleInOne">The most people known to be in a single instance at once, per day. From presence reports.</param>
/// <param name="TypicalMinutesOpen">Median time from open to close, for instances with both on record.</param>
/// <param name="InstancesWithBothEnds">How many instances that median is over.</param>
/// <param name="InstancesOpened">Instances opened inside the window.</param>
public sealed record InstancesAnalytics(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<DayValue> Opened,
    IReadOnlyList<DayValue> Closed,
    IReadOnlyList<DayValue> MostOpenAtOnce,
    IReadOnlyList<DayValue> MostPeopleInOne,
    decimal? TypicalMinutesOpen,
    int InstancesWithBothEnds,
    int InstancesOpened,
    HourOfWeek HourOfWeek,
    AnalyticsCoverage Coverage,
    DateTimeOffset GeneratedAt);
