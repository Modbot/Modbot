using System.Globalization;
using System.Text.Json.Nodes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Reviews;

/// <summary>
/// One pattern a check found, before it becomes a review.
/// </summary>
/// <param name="About">The person, or the UTC day -- the key a review is idempotent on.</param>
/// <param name="WindowEnd">The last instant the evidence covers. A closed review reaching this far already covers it.</param>
/// <param name="Summary">The pattern in a sentence, numbers included.</param>
/// <param name="Evidence">Every number and the fact ids behind them.</param>
public sealed record Finding(
    string Signal,
    FactPlatform ModeratorPlatform,
    string ModeratorId,
    string About,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    string Summary,
    JsonObject Evidence);

/// <summary>
/// The two checks spec 5.8.5 lists that can be computed today, over facts alone.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Same person</strong> (bullets one and two): one moderator acting on one person again
/// and again, across different instances or days, where no other moderator has. The "no other
/// moderator" part is what separates a grudge from a persistent troll everybody is removing, so
/// it is not a separate signal but the condition that lets this one fire at a low threshold; when
/// others have acted too, the bar is much higher.
/// </para>
/// <para>
/// <strong>Far above the team</strong> (bullet four): one moderator's day is many times the next
/// busiest moderator's and the team's usual. Both comparisons, because either alone misfires: a
/// raid night makes everyone busy (the next busiest is high too), and a one-person team has no
/// next busiest at all (the usual is what is left).
/// </para>
/// <para>
/// The third bullet -- unclassified actions relative to peers -- needs the classification M4
/// captures, and is not here.
/// </para>
/// <para>
/// Every count excludes a moderator acting on themselves, and every check reads facts only up to
/// the caller's <c>now</c> (spec 4.4).
/// </para>
/// </remarks>
internal static class PatternChecks
{
    /// <summary>How many fact ids a review keeps. Enough to show the working; not the whole log.</summary>
    private const int MaxFactIds = 200;

    public static async Task<IReadOnlyList<Finding>> SamePersonAsync(
        ModbotContext db,
        IReadOnlyCollection<string>? actorIds,
        DateTimeOffset now,
        ReviewThresholds thresholds,
        CancellationToken ct)
    {
        if (actorIds is { Count: 0 })
            return [];

        var windowStart = now.AddDays(-thresholds.SamePersonDays);
        var actorFilter = actorIds is null ? string.Empty : "AND e.actor_id = ANY(@actors)";

        // A "place" is the instance where the fact carries one, else the world, else the UTC day.
        // Two kicks in one session are one place; the check wants a pattern across sessions.
        //
        // Facts up to the newest closed review's window_end for the same pair are left out, so a
        // pattern that was reviewed and closed is not counted again -- only what happened since.
        var sql = $"""
            WITH closed AS (
                SELECT r.moderator_id, r.about AS subject_id, MAX(r.window_end) AS through
                FROM modbot_review r
                WHERE r.state = @closedState AND r.signal = @signal AND r.moderator_platform = @vrchat
                GROUP BY r.moderator_id, r.about
            ),
            acts AS (
                SELECT e.actor_id, e.subject_id, e.id, e.type, e.occurred_at,
                       COALESCE(e.world_id || ':' || e.instance_id, e.world_id,
                                'day:' || ((e.occurred_at AT TIME ZONE 'UTC')::date)::text) AS place
                FROM modbot_event e
                LEFT JOIN closed c ON c.moderator_id = e.actor_id AND c.subject_id = e.subject_id
                WHERE e.type = ANY(@types)
                  AND e.actor_platform = @vrchat AND e.actor_id IS NOT NULL
                  AND e.subject_platform = @vrchat
                  AND e.actor_id <> e.subject_id
                  AND e.occurred_at > @windowStart AND e.occurred_at <= @now
                  AND (c.through IS NULL OR e.occurred_at > c.through)
                  -- One decision, one action. A ban that also kicked the person out of the
                  -- instance they were standing in must not read as a moderator acting twice
                  -- (spec 5.3.2) -- this check is the one where that would be an accusation.
                  AND NOT EXISTS (SELECT 1 FROM modbot_linked_fact l WHERE l.fact_id = e.id)
                  {actorFilter}
            ),
            pairs AS (
                SELECT actor_id, subject_id,
                       COUNT(*)::int AS n,
                       COUNT(DISTINCT place)::int AS places,
                       COUNT(*) FILTER (WHERE type = @kick)::int AS kicks,
                       COUNT(*) FILTER (WHERE type = @warn)::int AS warns,
                       COUNT(*) FILTER (WHERE type = @ban)::int AS bans,
                       COUNT(*) FILTER (WHERE type = @removal)::int AS removals,
                       COUNT(*) FILTER (WHERE type IN (@reject, @block))::int AS rejections,
                       MIN(occurred_at) AS first_at,
                       MAX(occurred_at) AS last_at,
                       (array_agg(id ORDER BY occurred_at, id))[1:{MaxFactIds}] AS fact_ids
                FROM acts
                GROUP BY actor_id, subject_id
                HAVING COUNT(*) >= @minActions AND COUNT(DISTINCT place) >= 2
            )
            SELECT p.actor_id, p.subject_id, p.n, p.places, p.kicks, p.warns, p.bans, p.removals, p.rejections,
                   p.first_at, p.last_at, p.fact_ids,
                   (SELECT COUNT(DISTINCT o.actor_id)::int
                    FROM modbot_event o
                    WHERE o.type = ANY(@types) AND o.subject_platform = @vrchat AND o.subject_id = p.subject_id
                      AND o.actor_id IS NOT NULL AND o.actor_id <> p.actor_id AND o.actor_id <> o.subject_id
                      AND o.occurred_at <= @now) AS others
            FROM pairs p
            """;

        var parameters = new List<(string, object?)>
        {
            ("closedState", (short)ReviewState.Closed),
            ("signal", ReviewSignal.SamePerson),
            ("vrchat", (short)FactPlatform.VRChat),
            ("types", ActionsOnPeople.Types),
            ("windowStart", windowStart),
            ("now", now),
            ("minActions", thresholds.SamePersonActions),
            ("kick", FactType.GroupInstanceKick),
            ("warn", FactType.GroupInstanceWarn),
            ("ban", FactType.MemberBanned),
            ("removal", FactType.MemberKicked),
            ("reject", FactType.JoinRequestRejected),
            ("block", FactType.JoinRequestBlocked),
        };

        if (actorIds is not null)
            parameters.Add(("actors", actorIds.Distinct(StringComparer.Ordinal).ToArray()));

        var rows = await ReviewSql.ReadAsync(
            db,
            sql,
            r => (
                Actor: r.GetString(0),
                Subject: r.GetString(1),
                N: r.GetInt32(2),
                Places: r.GetInt32(3),
                Kicks: r.GetInt32(4),
                Warns: r.GetInt32(5),
                Bans: r.GetInt32(6),
                Removals: r.GetInt32(7),
                Rejections: r.GetInt32(8),
                FirstAt: ReviewSql.InstantOf(r, 9),
                LastAt: ReviewSql.InstantOf(r, 10),
                FactIds: ReviewSql.LongsOf(r, 11),
                Others: r.GetInt32(12)),
            ct,
            parameters.ToArray());

        var findings = new List<Finding>();

        foreach (var row in rows)
        {
            var bar = row.Others == 0 ? thresholds.SamePersonActions : thresholds.SamePersonActionsWhenOthersActed;
            if (row.N < bar)
                continue;

            var kinds = ActionsOnPeople.Describe(row.Kicks, row.Warns, row.Bans, row.Removals, row.Rejections);
            var othersSentence = row.Others switch
            {
                0 => "No other moderator has acted on this person.",
                1 => "One other moderator has acted on this person.",
                var n => $"{n} other moderators have acted on this person.",
            };

            var summary =
                $"Acted on the same person {row.N} times in the last {thresholds.SamePersonDays} days, "
                + $"across {row.Places} different instances or days ({kinds}). {othersSentence}";

            var evidence = new JsonObject
            {
                ["subjectId"] = row.Subject,
                ["actions"] = row.N,
                ["places"] = row.Places,
                ["byKind"] = ByKind(row.Kicks, row.Warns, row.Bans, row.Removals, row.Rejections),
                ["otherModerators"] = row.Others,
                ["windowDays"] = thresholds.SamePersonDays,
                ["firstAt"] = row.FirstAt.ToString("O", CultureInfo.InvariantCulture),
                ["lastAt"] = row.LastAt.ToString("O", CultureInfo.InvariantCulture),
                ["threshold"] = new JsonObject
                {
                    ["actions"] = bar,
                    ["actionsWhenNobodyElseActed"] = thresholds.SamePersonActions,
                    ["actionsWhenOthersActed"] = thresholds.SamePersonActionsWhenOthersActed,
                    ["places"] = 2,
                },
                ["factIds"] = new JsonArray(row.FactIds.Select(id => (JsonNode)id).ToArray()),
            };

            findings.Add(new Finding(
                ReviewSignal.SamePerson,
                FactPlatform.VRChat,
                row.Actor,
                row.Subject,
                row.FirstAt,
                row.LastAt,
                summary,
                evidence));
        }

        return findings;
    }

    public static async Task<IReadOnlyList<Finding>> FarAboveTeamAsync(
        ModbotContext db,
        IReadOnlyCollection<DateOnly> days,
        TeamBaseline team,
        DateTimeOffset now,
        ReviewThresholds thresholds,
        CancellationToken ct)
    {
        if (days.Count == 0 || team.Days < thresholds.FarAboveTeamMinTeamDays)
            return [];

        var ordered = days.Distinct().Order().ToArray();

        const string Sql = """
            SELECT (e.occurred_at AT TIME ZONE 'UTC')::date AS day, e.actor_id,
                   COUNT(*)::int AS n,
                   COUNT(*) FILTER (WHERE e.type = @kick)::int AS kicks,
                   COUNT(*) FILTER (WHERE e.type = @warn)::int AS warns,
                   COUNT(*) FILTER (WHERE e.type = @ban)::int AS bans,
                   COUNT(*) FILTER (WHERE e.type = @removal)::int AS removals,
                   COUNT(*) FILTER (WHERE e.type IN (@reject, @block))::int AS rejections,
                   MIN(e.occurred_at) AS first_at,
                   MAX(e.occurred_at) AS last_at,
                   (array_agg(e.id ORDER BY e.occurred_at, e.id))[1:200] AS fact_ids
            FROM modbot_event e
            WHERE e.type = ANY(@types)
              AND e.actor_platform = @vrchat AND e.actor_id IS NOT NULL
              AND e.actor_id <> e.subject_id
              AND e.occurred_at >= @from AND e.occurred_at < @to AND e.occurred_at <= @now
              AND (e.occurred_at AT TIME ZONE 'UTC')::date = ANY(@days)
            GROUP BY 1, 2
            """;

        var rows = await ReviewSql.ReadAsync(
            db,
            Sql,
            r => (
                Day: DateOnly.FromDateTime(r.GetDateTime(0)),
                Actor: r.GetString(1),
                N: r.GetInt32(2),
                Kicks: r.GetInt32(3),
                Warns: r.GetInt32(4),
                Bans: r.GetInt32(5),
                Removals: r.GetInt32(6),
                Rejections: r.GetInt32(7),
                FirstAt: ReviewSql.InstantOf(r, 8),
                LastAt: ReviewSql.InstantOf(r, 9),
                FactIds: ReviewSql.LongsOf(r, 10)),
            ct,
            ("types", ActionsOnPeople.Types),
            ("vrchat", (short)FactPlatform.VRChat),
            ("from", ReviewSql.DayStart(ordered[0])),
            ("to", ReviewSql.DayStart(ordered[^1].AddDays(1))),
            ("now", now),
            ("days", ordered),
            ("kick", FactType.GroupInstanceKick),
            ("warn", FactType.GroupInstanceWarn),
            ("ban", FactType.MemberBanned),
            ("removal", FactType.MemberKicked),
            ("reject", FactType.JoinRequestRejected),
            ("block", FactType.JoinRequestBlocked));

        var findings = new List<Finding>();

        foreach (var day in rows.GroupBy(r => r.Day))
        {
            var byCount = day.OrderByDescending(r => r.N).ThenBy(r => r.Actor, StringComparer.Ordinal).ToList();
            var top = byCount[0];
            var next = byCount.Count > 1 ? byCount[1] : default;

            // The bar: the multiplier times whichever is larger, the next busiest moderator that
            // day or the team's usual. Never below the multiplier itself, so a team whose usual
            // rounds to nothing still needs a real number of actions.
            var reference = Math.Max(Math.Max(next.N, team.UsualPerModeratorDay), 1m);
            var bar = thresholds.FarAboveTeamMultiplier * reference;

            if (top.N < thresholds.FarAboveTeamMinActions || top.N < bar)
                continue;

            team.ByModerator.TryGetValue(top.Actor, out var own);

            var kinds = ActionsOnPeople.Describe(top.Kicks, top.Warns, top.Bans, top.Removals, top.Rejections);
            var dayText = day.Key.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            var nextSentence = byCount.Count > 1
                ? $"The next busiest moderator did {next.N} that day."
                : "Nobody else on the team did anything that day.";
            var ownSentence = own is null
                ? "They have no usual yet."
                : $"Their usual is {own.ActionsPerActiveDay:0.#} a day.";

            var summary =
                $"{top.N} actions on {dayText} ({kinds}). {nextSentence} "
                + $"The team's usual is {team.UsualPerModeratorDay:0.#} a day per moderator. {ownSentence}";

            var evidence = new JsonObject
            {
                ["day"] = day.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["actions"] = top.N,
                ["byKind"] = ByKind(top.Kicks, top.Warns, top.Bans, top.Removals, top.Rejections),
                ["nextBusiest"] = byCount.Count > 1
                    ? new JsonObject { ["moderatorId"] = next.Actor, ["actions"] = next.N }
                    : null,
                ["teamUsualPerDay"] = team.UsualPerModeratorDay,
                ["teamDays"] = team.Days,
                ["ownUsualPerDay"] = own?.ActionsPerActiveDay,
                ["ownActiveDays"] = own?.ActiveDays,
                ["firstAt"] = top.FirstAt.ToString("O", CultureInfo.InvariantCulture),
                ["lastAt"] = top.LastAt.ToString("O", CultureInfo.InvariantCulture),
                ["threshold"] = new JsonObject
                {
                    ["minActions"] = thresholds.FarAboveTeamMinActions,
                    ["multiplier"] = thresholds.FarAboveTeamMultiplier,
                    ["bar"] = Math.Round(bar, 1),
                },
                ["factIds"] = new JsonArray(top.FactIds.Select(id => (JsonNode)id).ToArray()),
            };

            findings.Add(new Finding(
                ReviewSignal.FarAboveTeam,
                FactPlatform.VRChat,
                top.Actor,
                day.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ReviewSql.DayStart(day.Key),
                ReviewSql.DayStart(day.Key.AddDays(1)),
                summary,
                evidence));
        }

        return findings;
    }

    private static JsonObject ByKind(int kicks, int warns, int bans, int removals, int rejections) => new()
    {
        ["instanceKicks"] = kicks,
        ["warns"] = warns,
        ["bans"] = bans,
        ["removals"] = removals,
        ["rejections"] = rejections,
    };
}
