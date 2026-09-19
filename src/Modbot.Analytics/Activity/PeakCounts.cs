namespace Modbot.Analytics.Activity;

/// <summary>
/// The highest a count reached, and the moment it reached it.
/// </summary>
/// <remarks>
/// A peak with no time on it is half an answer: "forty-eight people at once" is a boast, "forty-eight
/// people at once on Saturday at 20:15" is something a moderator can roster against. Every peak in
/// Modbot therefore carries its instant, and nothing here is reported without one.
/// </remarks>
/// <param name="Value">How high the count got.</param>
/// <param name="At">
/// The first moment it was that high. Where a count sits at its highest for a stretch, or reaches the
/// same height twice, the earliest moment wins — see <see cref="Peaks"/> for why the rule matters.
/// </param>
public sealed record PeakCount(int Value, DateTimeOffset At);

/// <summary>
/// The day the group's instances held the most people-time, and how busy it was.
/// </summary>
/// <param name="Day">A UTC day, like every other day boundary in analytics.</param>
/// <param name="PeopleMinutes">
/// People added up over the minutes they were counted — one person for an hour and sixty people for
/// a minute are both sixty. This is what decides which day was busiest, rather than the peak, because
/// a day with one short rush is not a busy day.
/// </param>
/// <param name="MostPeopleAtOnce">The highest the day got, so a busy day and a peaky one can be told apart.</param>
public sealed record BusiestDay(DateOnly Day, decimal PeopleMinutes, int MostPeopleAtOnce);

/// <summary>
/// The single clock hour the group's instances held the most people-time.
/// </summary>
/// <param name="StartedAt">The hour's first moment, in UTC. One real hour on one real date, not "Saturdays at 8".</param>
/// <param name="PeopleMinutes">People added up over the minutes of that hour.</param>
/// <param name="MostPeopleAtOnce">The highest that hour got.</param>
public sealed record BusiestHour(DateTimeOffset StartedAt, decimal PeopleMinutes, int MostPeopleAtOnce);

/// <summary>
/// One UTC day of instance activity, as the database aggregates it.
/// </summary>
/// <remarks>
/// The shape the day rows come back in, and the input to every peak on the Instances page. Kept in
/// <c>Modbot.Analytics</c> rather than beside the query so the picking rules can be exercised without
/// a database: which day was busiest is arithmetic, and arithmetic does not need Postgres to prove.
/// </remarks>
/// <param name="MostPeopleAt">The first moment the day was as full as it got.</param>
/// <param name="MostInstancesAt">The first moment as many instances were being counted as ever were that day.</param>
/// <param name="BestHourStartedAt">The first moment of the day's own busiest hour.</param>
/// <param name="BestHourPeopleMinutes">That hour's people-minutes.</param>
/// <param name="BestHourMostPeopleAtOnce">The highest that hour got.</param>
public sealed record ActivityDay(
    DateOnly Day,
    int MostPeopleAtOnce,
    DateTimeOffset MostPeopleAt,
    int MostInstancesAtOnce,
    DateTimeOffset MostInstancesAt,
    decimal PeopleMinutes,
    DateTimeOffset BestHourStartedAt,
    decimal BestHourPeopleMinutes,
    int BestHourMostPeopleAtOnce);
