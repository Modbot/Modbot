namespace Modbot.Api.Features.Analytics.Team;

/// <param name="Metric">The daily total metric, verbatim, so a number can be traced to its row.</param>
/// <param name="Label">Plain words for the column header.</param>
public sealed record ActionKind(string Metric, string Label);

/// <param name="ByKind">Count per <see cref="ActionKind.Metric"/>, for the kinds this person has any of.</param>
/// <param name="LastActiveDay">The last day in the window this person did anything.</param>
public sealed record ModeratorSummary(
    Person Who,
    decimal Total,
    IReadOnlyDictionary<string, decimal> ByKind,
    DateOnly? LastActiveDay);

public sealed record KindSeries(string Metric, string Label, decimal Total, IReadOnlyList<DayValue> Points);

/// <summary>
/// A stretch when people were in a group instance and no moderator was.
/// </summary>
/// <param name="StartedAt">When the last moderator's presence ended.</param>
/// <param name="EndedAt">
/// When a moderator's presence resumed or the instance closed; null when neither was seen, in
/// which case nothing more is known about that instance after the gap began.
/// </param>
/// <param name="EndedBy"><c>moderator-arrived</c>, <c>instance-closed</c>, or <c>unknown</c>.</param>
/// <param name="PeopleWhenLastModeratorLeft">
/// How many people were known to be in the instance at that moment — from the last client's
/// reports, so it is a floor, not a count.
/// </param>
/// <param name="LastModerator">Who left, when a presence fact names them; null when only the client's reporting stopped.</param>
public sealed record CoverageGap(
    string WorldId,
    string InstanceId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string EndedBy,
    int PeopleWhenLastModeratorLeft,
    Person? LastModerator);

/// <param name="ModeratorsRecognised">How many people the moderator test currently matches.</param>
/// <param name="HowModeratorsAreRecognised">The rule, in words, so the page can show it beside the number.</param>
/// <param name="InstancesWatched">Instances in the window with at least one moderator presence report.</param>
/// <param name="InstancesOpenedWithoutAnyWatch">
/// Instances the audit log saw opened in the window that no client ever reported from. Nothing is
/// known about who was in them, which is itself the finding.
/// </param>
public sealed record TeamAnalytics(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<ActionKind> Kinds,
    IReadOnlyList<ModeratorSummary> Moderators,
    IReadOnlyList<DayValue> ActionsPerDay,
    IReadOnlyList<KindSeries> ActionsPerDayByKind,
    IReadOnlyList<CoverageGap> CoverageGaps,
    int ModeratorsRecognised,
    string HowModeratorsAreRecognised,
    int InstancesWatched,
    int InstancesOpenedWithoutAnyWatch,
    AnalyticsCoverage Coverage,
    DateTimeOffset GeneratedAt);
