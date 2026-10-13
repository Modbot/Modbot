namespace Modbot.Api.Features.Analytics.Regulars;

/// <summary>One week of the return-rate chart.</summary>
/// <param name="WeekStart">The Monday (UTC) the week begins on.</param>
/// <param name="Visitors">Distinct people who joined any of the group's instances that week.</param>
/// <param name="CameBack">
/// Of those, how many joined a group instance again in the next four weeks. Null while those four
/// weeks are not over yet: a count taken part-way would read low and then rise, and a rate that
/// moves after it is drawn is not one anybody can compare with the weeks before it.
/// </param>
public sealed record WeekReturn(DateOnly WeekStart, int Visitors, int? CameBack);

/// <summary>One regular.</summary>
/// <param name="UserId">The VRChat user id; opaque, shown when no name is known.</param>
/// <param name="Name">The latest display name any fact recorded, or null.</param>
/// <param name="Weeks">How many of the weeks they joined an instance in.</param>
/// <param name="Days">On how many different days in those weeks.</param>
/// <param name="LastSeenAt">The most recent join in those weeks.</param>
public sealed record Regular(string UserId, string? Name, int Weeks, int Days, DateTimeOffset LastSeenAt);

/// <summary>
/// Who comes back to the group's instances, and who comes every week.
/// </summary>
/// <param name="From">The first day of the window asked for.</param>
/// <param name="To">The last day of the window asked for.</param>
/// <param name="Weeks">Every week the window touches, oldest first, whole weeks Monday to Sunday (UTC).</param>
/// <param name="ReturnWeeks">How many weeks after a visit still count as coming back. Four.</param>
/// <param name="RegularWeeks">How many of the latest whole weeks "regular" looks at. Four.</param>
/// <param name="RegularNeeds">How many of those weeks a regular joined in. Three.</param>
/// <param name="RegularsFrom">The first day of the weeks the regulars were counted over.</param>
/// <param name="RegularsTo">The last day of those weeks.</param>
/// <param name="RegularsTotal">How many people are regulars; <paramref name="Regulars"/> may hold fewer.</param>
/// <param name="Regulars">The regulars, the most days first.</param>
/// <param name="PresenceReports">How many presence facts the window holds; the page says so when there are few.</param>
public sealed record RegularsAnalytics(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<WeekReturn> Weeks,
    int ReturnWeeks,
    int RegularWeeks,
    int RegularNeeds,
    DateOnly RegularsFrom,
    DateOnly RegularsTo,
    int RegularsTotal,
    IReadOnlyList<Regular> Regulars,
    long PresenceReports,
    AnalyticsCoverage Coverage,
    DateTimeOffset GeneratedAt);
