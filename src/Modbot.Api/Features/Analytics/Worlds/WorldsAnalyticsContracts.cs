namespace Modbot.Api.Features.Analytics.Worlds;

/// <param name="WorldId">VRChat's id. Opaque, and still shown when nothing else is known yet.</param>
/// <param name="Name">
/// What the world is called, from <c>vrchat_world</c>, or null when Modbot has only ever seen the
/// id. Null is an ordinary state rather than a fault: a world is named by the sweep shortly after
/// it is first seen, and a private or deleted world never gets a name at all.
/// </param>
/// <param name="AuthorName">Who made it, as the world page reported it.</param>
/// <param name="ThumbnailImageUrl">The world's picture, for the row.</param>
/// <param name="Capacity">
/// How many the world holds, as VRChat's page said. Never treated as a limit Modbot enforces --
/// exemptions raise real capacity above it (foundation section 3.1).
/// </param>
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
    string? Name,
    string? AuthorName,
    string? ThumbnailImageUrl,
    int? Capacity,
    decimal MinutesSeen,
    int Visitors,
    int Visits,
    decimal InstancesOpened,
    DateTimeOffset? LastSeenAt);

public sealed record WorldSeries(string WorldId, IReadOnlyList<DayValue> Points);

/// <param name="Worlds">Every world with anything recorded in the window, most time first.</param>
/// <param name="VisitorsPerDay">Distinct people per day for the busiest worlds, from daily totals.</param>
/// <param name="PresenceReports">How many presence facts the window holds — the page says "thin" below a handful.</param>
/// <param name="Today">The window's last day when it is today by the server's clock, so not over yet.</param>
/// <param name="DaysWithoutPresenceReports">
/// Days a group instance was open and no companion reported from any of them, so who was there is
/// not known. A day nothing was open is not in here.
/// </param>
public sealed record WorldsAnalytics(
    DateOnly From,
    DateOnly To,
    DateOnly? Today,
    IReadOnlyList<WorldSummary> Worlds,
    IReadOnlyList<WorldSeries> VisitorsPerDay,
    long PresenceReports,
    IReadOnlyList<DateOnly> DaysWithoutPresenceReports,
    AnalyticsCoverage Coverage,
    DateTimeOffset GeneratedAt);
