namespace Modbot.Api.Features.Analytics;

/// <param name="Day">A UTC day. The fact log partitions on UTC months and one system with two
/// day boundaries is a bug farm, so the charts use the same boundary the data does.</param>
public sealed record DayValue(DateOnly Day, decimal Value);

/// <summary>
/// What each part of an analytics response was computed from, and how far back each source
/// reaches.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.5: daily totals are never aged out, while facts can be where an operator has configured
/// a retention window. Spec 5.10.2 draws the consequence — a chart may legitimately cover a longer
/// period than the fact log does, and a UI that showed both under one date picker would imply
/// they were the same range.
/// </para>
/// <para>
/// The gap is not only about retention. Even with nothing pruned, the daily totals and the fact
/// log start at different places for a fresh deployment, because the audit-log catch-up walks
/// history backwards while the daily totals job only ever folds forward from what it has seen.
/// </para>
/// </remarks>
/// <param name="DailyTotalsUpdatedAt">
/// When the daily totals job last finished. Anything served from daily totals is as fresh as
/// this, and the page says so rather than letting a fifteen-minute-old number pass as live.
/// </param>
public sealed record AnalyticsCoverage(
    DateOnly? DailyTotalsFirstDay,
    DateOnly? DailyTotalsLastDay,
    DateTimeOffset? DailyTotalsUpdatedAt,
    DateOnly? FactFirstDay,
    DateOnly? FactLastDay,
    bool RetentionConfigured,
    int ModerationFactRetentionDays,
    int PresenceFactRetentionDays);

/// <param name="Platform">The platform half of a daily total dimension, e.g. <c>vrchat</c>.</param>
/// <param name="Id">Opaque. Never parsed, never validated (spec 3.1.1).</param>
/// <param name="Name">
/// The display name last recorded beside this id, or null. Never substituted with the id dressed
/// up as a name — an unnamed person shows as an id, which is what is actually known.
/// </param>
public sealed record Person(string Platform, string Id, string? Name);
