using Modbot.Api.Features.Analytics;

namespace Modbot.Api.Features.Reviews;

/// <summary>
/// One person's count of being acted on, with the words for each number.
/// </summary>
/// <param name="Actions">Every action against them, all time. Unbans are shown but not counted.</param>
/// <param name="Moderators">How many different moderators have acted on them, all time.</param>
/// <param name="LastActionLabel">The last action in plain words, e.g. "Kicked from an instance".</param>
/// <param name="LastBy">Who took the last action, when VRChat named them.</param>
/// <param name="Status"><c>once</c>, <c>more-than-once</c> or <c>repeat</c>; the rule is in the response beside it.</param>
public sealed record RepeatOffenderView(
    Person Who,
    int InstanceKicks,
    int Warns,
    int Bans,
    int Unbans,
    int Removals,
    int Rejections,
    int Actions,
    int ActionsLast30Days,
    int ActionsLast90Days,
    int Moderators,
    int ModeratorsLast90Days,
    DateTimeOffset FirstActionAt,
    DateTimeOffset LastActionAt,
    string LastActionType,
    string LastActionLabel,
    Person? LastBy,
    string Status,
    DateTimeOffset ComputedAt);

/// <param name="Rule">How the status is decided, in words, at the current threshold.</param>
/// <param name="LastRunAt">When the counts were last rebuilt. Null means never.</param>
public sealed record RepeatOffenderListResponse(
    IReadOnlyList<RepeatOffenderView> People,
    int Total,
    int Offset,
    string Rule,
    DateTimeOffset? LastRunAt,
    DateTimeOffset Now);

/// <summary>The "History" block on one person's pane.</summary>
/// <param name="Known">False when nobody has ever acted on this person; <paramref name="Counts"/> is then null.</param>
public sealed record SubjectHistory(
    string SubjectId,
    bool Known,
    RepeatOffenderView? Counts,
    string Rule,
    DateTimeOffset? LastRunAt,
    DateTimeOffset Now);
