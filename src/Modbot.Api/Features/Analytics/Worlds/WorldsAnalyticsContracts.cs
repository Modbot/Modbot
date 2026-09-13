namespace Modbot.Api.Features.Analytics.Worlds;

/// <param name="WorldId">Opaque. World names are not in any fact yet, so the page shows the id.</param>
/// <param name="MinutesSeen">
/// Minutes people were seen in this world inside the window, summed across people. A person
/// counts from when a client first saw them until it saw them leave — or until the last report
/// from that instance, when nobody saw them leave. Time nobody was watching is not in here.
/// </param>
/// <param name="Visitors">Distinct people seen in the window.</param>
/// <param name="Visits">Arrivals seen: a person entering, or already there when a client arrived.</param>
/// <param name="InstancesOpened">Group instances opened in this world in the window, from the audit log.</param>
public sealed record WorldSummary(
    string WorldId,
    decimal MinutesSeen,
    int Visitors,
    int Visits,
    decimal InstancesOpened,
    DateTimeOffset? LastSeenAt);

public sealed record WorldSeries(string WorldId, IReadOnlyList<DayValue> Points);

/// <param name="Worlds">Every world with anything recorded in the window, most time first.</param>
/// <param name="VisitorsPerDay">Distinct people per day for the busiest worlds, from daily totals.</param>
/// <param name="PresenceReports">How many presence facts the window holds — the page says "thin" below a handful.</param>
public sealed record WorldsAnalytics(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<WorldSummary> Worlds,
    IReadOnlyList<WorldSeries> VisitorsPerDay,
    long PresenceReports,
    AnalyticsCoverage Coverage,
    DateTimeOffset GeneratedAt);
