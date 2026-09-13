namespace Modbot.Api.Features.Metrics;

/// <param name="Day">A UTC day. The fact log partitions on UTC months and one system with two
/// day boundaries is a bug farm, so the charts use the same boundary the data does.</param>
public sealed record DayValue(DateOnly Day, decimal Value);

/// <param name="Metric">The rollup metric name, verbatim, so a chart can be traced to its row.</param>
/// <param name="Label">What to title it.</param>
/// <param name="Note">
/// What the series does <em>not</em> say, where that is not obvious from the label. Carried with
/// the data rather than written into the SPA, because a caveat that lives beside the chart code
/// gets separated from the number the first time somebody reuses the endpoint.
/// </param>
public sealed record MetricSeries(
    string Metric,
    string Label,
    string? Note,
    IReadOnlyList<DayValue> Points);

/// <param name="Dimension">The raw rollup dimension, e.g. <c>vrchat:usr_…</c>.</param>
/// <param name="Name">
/// The display name last recorded for this actor, or null. Never substituted with the id dressed
/// up as a name — an unnamed moderator shows as an id, which is what is actually known.
/// </param>
public sealed record ModeratorActivity(
    string Dimension,
    string Platform,
    string ActorId,
    string? Name,
    decimal Actions);

/// <param name="Type">The <c>FactType</c> name.</param>
public sealed record ActionTypeSeries(
    string Type,
    string Label,
    decimal Total,
    IReadOnlyList<DayValue> Points);

/// <summary>
/// What each half of this response was computed from, and how far back each one reaches.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.5: rollups are never aged out, while facts can be where an operator has configured a
/// retention window. Spec 5.10.2 draws the consequence — a chart may legitimately cover a longer
/// period than the fact log does, and a UI that showed both under one date picker would imply
/// they were the same range.
/// </para>
/// <para>
/// The gap is not only about retention. Even with nothing pruned, the rollup series and the fact
/// log start at different places for a fresh deployment, because the audit-log catch-up walks
/// history backwards while the rollup job only ever folds forward from what it has seen.
/// </para>
/// </remarks>
public sealed record MetricsCoverage(
    DateOnly? RollupFirstDay,
    DateOnly? RollupLastDay,
    DateOnly? FactFirstDay,
    DateOnly? FactLastDay,
    bool RetentionConfigured,
    int ModerationFactRetentionDays,
    int PresenceFactRetentionDays);

/// <param name="MemberCount">
/// Observed headcounts, from the group-info sync. Real numbers VRChat reported, not a running
/// total Modbot accumulated — see the note on <c>members.net</c> in the metrics endpoint.
/// </param>
public sealed record MetricsResponse(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<MetricSeries> Series,
    IReadOnlyList<DayValue> MemberCount,
    IReadOnlyList<ModeratorActivity> Moderators,
    IReadOnlyList<ActionTypeSeries> ActionsByType,
    MetricsCoverage Coverage,
    DateTimeOffset GeneratedAt);
