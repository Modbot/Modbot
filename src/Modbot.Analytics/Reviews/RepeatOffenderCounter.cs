using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Reviews;

/// <summary>
/// Rebuilds <c>modbot_repeat_offender</c> rows from the fact log (spec 5.8.4).
/// </summary>
/// <remarks>
/// <para>
/// Delete-then-insert per person rather than upsert, for the reason the daily totals do it: a
/// person whose facts have gone (a purge, spec 5.5) loses their row instead of keeping a stale one.
/// One statement does the counting, in the database, because a busy group has tens of thousands of
/// these facts and the answer per person is a dozen numbers.
/// </para>
/// <para>
/// Everything time-relative is measured from the <c>now</c> the caller passes, never from the
/// database clock (spec 4.4). The 30- and 90-day windows are fixed; the one configurable number is
/// how many actions in 30 days make somebody a repeat offender.
/// </para>
/// </remarks>
internal static class RepeatOffenderCounter
{
    /// <summary>
    /// Recomputes the rows for <paramref name="subjectIds"/>, or for everybody when it is null.
    /// Returns how many rows were written.
    /// </summary>
    public static async Task<int> RecomputeAsync(
        ModbotContext db,
        IReadOnlyCollection<string>? subjectIds,
        DateTimeOffset now,
        ReviewThresholds thresholds,
        CancellationToken ct)
    {
        if (subjectIds is { Count: 0 })
            return 0;

        var filter = subjectIds is null ? string.Empty : "AND e.subject_id = ANY(@subjects)";
        var deleteFilter = subjectIds is null ? string.Empty : "WHERE subject_id = ANY(@subjects)";

        var parameters = new List<(string, object?)>
        {
            ("types", ActionsOnPeople.Types.Append(FactType.MemberUnbanned).ToArray()),
            ("counted", thresholds.CountedTypes.ToArray()),
            ("kick", FactType.GroupInstanceKick),
            ("warn", FactType.GroupInstanceWarn),
            ("ban", FactType.MemberBanned),
            ("unban", FactType.MemberUnbanned),
            ("removal", FactType.MemberKicked),
            ("reject", FactType.JoinRequestRejected),
            ("block", FactType.JoinRequestBlocked),
            ("since30", now.AddDays(-30)),
            ("since90", now.AddDays(-90)),
            ("now", now),
            ("repeatActions", thresholds.RepeatOffenderActionsIn30Days),
            ("repeat", RepeatOffenderStatus.Repeat),
            ("moreThanOnce", RepeatOffenderStatus.MoreThanOnce),
            ("once", RepeatOffenderStatus.Once),
        };

        if (subjectIds is not null)
            parameters.Add(("subjects", subjectIds.Distinct(StringComparer.Ordinal).ToArray()));

        await ReviewSql.ExecuteAsync(
            db,
            $"DELETE FROM modbot_repeat_offender {deleteFilter}",
            ct,
            subjectIds is null ? [] : [("subjects", subjectIds.Distinct(StringComparer.Ordinal).ToArray())]);

        // "Next change": the earliest action still inside a window is the next to fall out of
        // it, thirty or ninety days after it happened. Facts with no actor still count as actions
        // (VRChat does not always name one) but cannot count as anybody's.
        //
        // Facts that are a second record of a decision already counted are left out here, once,
        // rather than in each column (spec 5.3.2). A ban that also kicked the person out of the
        // instance they were in is one decision by one moderator; counting it as a ban and a kick
        // would put a person over the operator's threshold at half the decisions they set, and
        // would make the columns on the page not add up to the actions beside them.
        //
        // Which kinds count towards the status is the operator's to set; which kinds are shown in
        // their own column is not, because those columns are the record of what happened.
        var sql = $"""
            WITH acts AS (
                SELECT e.subject_platform, e.subject_id, e.type, e.actor_id, e.occurred_at, e.id,
                       (e.type = ANY(@counted)) AS is_action
                FROM modbot_event e
                WHERE e.type = ANY(@types)
                  AND NOT EXISTS (SELECT 1 FROM modbot_linked_fact l WHERE l.fact_id = e.id)
                  {filter}
            ),
            per AS (
                SELECT subject_platform, subject_id,
                       COUNT(*) FILTER (WHERE type = @kick)::int AS instance_kicks,
                       COUNT(*) FILTER (WHERE type = @warn)::int AS warns,
                       COUNT(*) FILTER (WHERE type = @ban)::int AS bans,
                       COUNT(*) FILTER (WHERE type = @unban)::int AS unbans,
                       COUNT(*) FILTER (WHERE type = @removal)::int AS removals,
                       COUNT(*) FILTER (WHERE type IN (@reject, @block))::int AS rejections,
                       COUNT(*) FILTER (WHERE is_action)::int AS actions,
                       COUNT(*) FILTER (WHERE is_action AND occurred_at > @since30)::int AS actions_30,
                       COUNT(*) FILTER (WHERE is_action AND occurred_at > @since90)::int AS actions_90,
                       COUNT(DISTINCT actor_id) FILTER (WHERE is_action AND actor_id IS NOT NULL)::int AS moderators,
                       COUNT(DISTINCT actor_id) FILTER (WHERE is_action AND actor_id IS NOT NULL AND occurred_at > @since90)::int AS moderators_90,
                       MIN(occurred_at) FILTER (WHERE is_action) AS first_at,
                       MAX(occurred_at) FILTER (WHERE is_action) AS last_at,
                       (array_agg(type ORDER BY occurred_at DESC, id DESC) FILTER (WHERE is_action))[1] AS last_type,
                       (array_agg(actor_id ORDER BY occurred_at DESC, id DESC) FILTER (WHERE is_action))[1] AS last_actor,
                       MIN(occurred_at) FILTER (WHERE is_action AND occurred_at > @since30) + interval '30 days' AS next_30,
                       MIN(occurred_at) FILTER (WHERE is_action AND occurred_at > @since90) + interval '90 days' AS next_90
                FROM acts
                GROUP BY subject_platform, subject_id
            )
            INSERT INTO modbot_repeat_offender (
                subject_platform, subject_id, instance_kicks, warns, bans, unbans, removals, rejections,
                actions, actions_last_30_days, actions_last_90_days, moderators, moderators_last_90_days,
                first_action_at, last_action_at, last_action_type, last_actor_id, status, counts_change_at, computed_at)
            SELECT subject_platform, subject_id, instance_kicks, warns, bans, unbans, removals, rejections,
                   actions, actions_30, actions_90, moderators, moderators_90,
                   first_at, last_at, last_type, last_actor,
                   CASE WHEN actions_30 >= @repeatActions THEN @repeat
                        WHEN actions >= 2 THEN @moreThanOnce
                        ELSE @once END,
                   LEAST(next_30, next_90),
                   @now
            FROM per
            WHERE actions > 0
            """;

        return await ReviewSql.ExecuteAsync(db, sql, ct, parameters.ToArray());
    }

    /// <summary>
    /// The people whose row has to be recomputed: anybody with a new fact since
    /// <paramref name="observedSince"/>, plus anybody whose windowed counts are due to change on
    /// their own.
    /// </summary>
    public static async Task<IReadOnlyList<string>> SubjectsDueAsync(
        ModbotContext db,
        DateTimeOffset? observedSince,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var observed = observedSince is null ? string.Empty : "AND e.observed_at > @since";

        var sql = $"""
            SELECT DISTINCT e.subject_id
            FROM modbot_event e
            WHERE e.type = ANY(@types) {observed}
            UNION
            SELECT r.subject_id
            FROM modbot_repeat_offender r
            WHERE r.counts_change_at IS NOT NULL AND r.counts_change_at <= @now
            """;

        var parameters = new List<(string, object?)>
        {
            ("types", ActionsOnPeople.Types.Append(FactType.MemberUnbanned).ToArray()),
            ("now", now),
        };

        if (observedSince is not null)
            parameters.Add(("since", observedSince.Value));

        return await ReviewSql.ReadAsync(db, sql, r => r.GetString(0), ct, parameters.ToArray());
    }
}
