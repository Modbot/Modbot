using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Analytics.Team;

/// <summary>
/// "Who is doing the moderation work, and when is nobody covering?" (spec 10.1, 5.8).
/// </summary>
/// <remarks>
/// <para>
/// The per-moderator numbers come from the daily totals, one metric per kind of action, so a
/// year of history costs a few hundred rows. Coverage gaps come from facts, because they are a
/// question about minutes rather than days and no daily total can hold that.
/// </para>
/// <para>
/// <strong>How coverage is decided.</strong> Every presence report comes from a moderator's
/// companion, so a client reporting from an instance is proof a moderator is in it — that
/// signal needs no roster at all. A roster is still built (moderation roles from the group
/// snapshot, people who have taken moderation actions, the owner) so that a moderator who is
/// seen by <em>somebody else's</em> client also counts as cover. A gap begins at the moment the
/// last of either signal ends while people are still known to be present, and ends when a
/// moderator is seen again or the instance closes. If neither is ever seen, the gap's end is
/// unknown, and the response says so rather than guessing.
/// </para>
/// <para>
/// <strong>What it cannot see.</strong> A moderator without the client, in an instance no client
/// is in, is invisible; an instance no client ever entered has no population at all. Both limits
/// are stated on the page, and the second is reported as its own number.
/// </para>
/// </remarks>
public sealed class TeamAnalyticsQuery(ModbotContext db)
{
    /// <summary>The kinds, in page order, with the words the columns use.</summary>
    public static readonly IReadOnlyList<ActionKind> Kinds =
    [
        new(DailyTotalMetrics.ModeratorInstanceKicks, "Instance kicks"),
        new(DailyTotalMetrics.ModeratorWarns, "Warns"),
        new(DailyTotalMetrics.ModeratorBans, "Bans"),
        new(DailyTotalMetrics.ModeratorUnbans, "Unbans"),
        new(DailyTotalMetrics.ModeratorRemovals, "Removed from group"),
        new(DailyTotalMetrics.ModeratorInvites, "Invites"),
        new(DailyTotalMetrics.ModeratorApprovals, "Requests approved"),
        new(DailyTotalMetrics.ModeratorRejections, "Requests rejected"),
        new(DailyTotalMetrics.ModeratorRoleChanges, "Role changes"),
    ];

    /// <summary>The actions that mark somebody as a moderator, whatever roles they hold.</summary>
    private static readonly string[] ModerationActionTypes =
    [
        FactType.MemberBanned,
        FactType.MemberUnbanned,
        FactType.MemberKicked,
        FactType.GroupInstanceKick,
        FactType.GroupInstanceWarn,
        FactType.JoinRequestRejected,
        FactType.JoinRequestBlocked,
        FactType.RoleGranted,
        FactType.RoleRevoked,
    ];

    private static readonly IReadOnlySet<string> KindMetrics =
        Kinds.Select(k => k.Metric).ToHashSet(StringComparer.Ordinal);

    private readonly AnalyticsSql _sql = new(db);

    public async Task<TeamAnalytics> RunAsync(
        DateOnly from,
        DateOnly to,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var totals = await _sql.DailyTotalsAsync(from, to, KindMetrics, ct);
        var moderators = await ModeratorsAsync(totals, ct);

        var roster = await RosterAsync(ct);
        var gaps = await CoverageGapsAsync(from, to, roster, ct);
        var watched = await InstancesWatchedAsync(from, to, ct);
        var unwatched = await InstancesOpenedWithoutAnyWatchAsync(from, to, ct);

        var byKind = Kinds
            .Select(k =>
            {
                var points = totals.SummedPerDay(new HashSet<string>(StringComparer.Ordinal) { k.Metric });
                return new KindSeries(k.Metric, k.Label, points.Sum(p => p.Value), points);
            })
            .ToList();

        return new TeamAnalytics(
            from,
            to,
            Kinds,
            moderators,
            totals.SummedPerDay(KindMetrics),
            byKind,
            gaps,
            roster.Count,
            watched,
            unwatched,
            await AnalyticsCoverageQuery.RunAsync(db, ct),
            now);
    }

    private async Task<IReadOnlyList<ModeratorSummary>> ModeratorsAsync(
        IReadOnlyList<DailyTotalRow> totals,
        CancellationToken ct)
    {
        var perActor = totals
            .Where(r => r.Dimension.Length > 0)
            .GroupBy(r => r.Dimension, StringComparer.Ordinal)
            .ToList();

        if (perActor.Count == 0)
            return [];

        var split = perActor.ToDictionary(g => g.Key, g => AnalyticsSql.SplitDimension(g.Key), StringComparer.Ordinal);
        var names = await _sql.NamesAsync(split.Values.Select(s => s.Id).ToList(), ct);

        return perActor
            .Select(g =>
            {
                var (platform, id) = split[g.Key];
                var byKind = g
                    .GroupBy(r => r.Metric, StringComparer.Ordinal)
                    .ToDictionary(k => k.Key, k => k.Sum(r => r.Value), StringComparer.Ordinal);

                return new ModeratorSummary(
                    new Person(platform, id, names.GetValueOrDefault(id)),
                    g.Sum(r => r.Value),
                    byKind,
                    g.Max(r => r.Day));
            })
            .OrderByDescending(m => m.Total)
            .ThenBy(m => m.Who.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Everyone the moderator rule currently matches. Over the whole log, because "is this
    /// person a moderator" is a now question and not a window one.
    /// </summary>
    private async Task<IReadOnlySet<string>> RosterAsync(CancellationToken ct)
    {
        var roster = new HashSet<string>(StringComparer.Ordinal);

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var snapshot = GroupInfoSnapshot.Parse(settings?.GroupInfoSnapshot);

        if (snapshot?.OwnerId is { Length: > 0 } owner)
            roster.Add(owner);

        var moderationRoles = (snapshot?.Roles ?? [])
            .Where(ModerationRoles.IsModerationRole)
            .Select(r => r.Id)
            .ToArray();

        if (moderationRoles.Length > 0)
        {
            // Holds the role now: the latest grant of it is later than any revoke of it.
            const string RoleHolders = """
                SELECT h.subject_id
                FROM (
                    SELECT e.subject_id,
                           MAX(e.occurred_at) FILTER (WHERE e.type = @granted) AS granted,
                           MAX(e.occurred_at) FILTER (WHERE e.type = @revoked) AS revoked
                    FROM modbot_event e
                    WHERE e.type IN (@granted, @revoked)
                      AND e.subject_platform = @vrchat
                      AND e.data->>'roleId' = ANY(@roles)
                    GROUP BY e.subject_id, e.data->>'roleId'
                ) h
                WHERE h.granted IS NOT NULL AND (h.revoked IS NULL OR h.revoked < h.granted)
                """;

            foreach (var id in await _sql.ReadAsync(RoleHolders, r => r.GetString(0), ct,
                         ("granted", FactType.RoleGranted),
                         ("revoked", FactType.RoleRevoked),
                         ("vrchat", (short)FactPlatform.VRChat),
                         ("roles", moderationRoles)))
                roster.Add(id);
        }

        const string Actors = """
            SELECT DISTINCT e.actor_id
            FROM modbot_event e
            WHERE e.actor_id IS NOT NULL AND e.actor_platform = @vrchat
              AND (e.type = ANY(@actions) OR (e.type = @join AND e.actor_id <> e.subject_id))
            """;

        foreach (var id in await _sql.ReadAsync(Actors, r => r.GetString(0), ct,
                     ("vrchat", (short)FactPlatform.VRChat),
                     ("actions", ModerationActionTypes),
                     ("join", FactType.MemberJoined)))
            roster.Add(id);

        return roster;
    }

    /// <summary>
    /// The gaps, from the presence stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Done in SQL because the stream is every presence fact in the window and a busy group
    /// writes tens of thousands a day; only the handful of rows where cover ran out come back.
    /// </para>
    /// <para>
    /// Per person, presence is the last thing said about them in that instance: an arrival makes
    /// them present, a leave makes them absent, and a repeat of either changes nothing. That is
    /// what keeps two clients' reports of one arrival from counting as two people, and a leave
    /// nobody saw the arrival for from counting as minus one. The instance's population is the
    /// running sum of those changes.
    /// </para>
    /// <para>
    /// Cover is the running sum of two things: recognised moderators present (same rule as
    /// above) and clients reporting. A client's stretch in an instance ends at its last fact
    /// there, or at a leave after which the next thing that client reports is that same person
    /// arriving again -- because when the client's own user walks out it records exactly their
    /// leave and then nothing until they enter somewhere again, and on re-entry their own
    /// arrival comes first. A stretch begins at the client's first fact and after every end.
    /// At one instant a stretch's end sorts after person rows and its start before them, so the
    /// moment cover reaches zero is seen with the population already updated for whoever left.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<CoverageGap>> CoverageGapsAsync(
        DateOnly from,
        DateOnly to,
        IReadOnlySet<string> roster,
        CancellationToken ct)
    {
        const string Sql = """
            WITH p AS (
                SELECT e.world_id, e.instance_id, e.subject_id, e.occurred_at, e.id,
                       CASE WHEN e.type = @leave THEN 0 ELSE 1 END AS here,
                       e.subject_id = ANY(@mods) AS is_mod,
                       e.data->>'deviceId' AS device
                FROM modbot_event e
                WHERE e.type = ANY(@presence)
                  AND e.occurred_at >= @from AND e.occurred_at < @to
                  AND e.world_id IS NOT NULL AND e.instance_id IS NOT NULL
            ),
            changes AS (
                SELECT p.*,
                       p.here - COALESCE(LAG(p.here) OVER (
                           PARTITION BY p.world_id, p.instance_id, p.subject_id
                           ORDER BY p.occurred_at, p.id), 0) AS change
                FROM p
            ),
            people AS (
                SELECT world_id, instance_id, subject_id, occurred_at, id, change,
                       CASE WHEN is_mod THEN change ELSE 0 END AS cover_change, 0 AS kind
                FROM changes
                WHERE change <> 0
            ),
            device_rows AS (
                SELECT world_id, instance_id, device, subject_id, occurred_at, id, here,
                       LEAD(here) OVER w AS next_here,
                       LEAD(subject_id) OVER w AS next_subject
                FROM p
                WHERE device IS NOT NULL
                WINDOW w AS (PARTITION BY world_id, instance_id, device ORDER BY occurred_at, id)
            ),
            device_marks AS (
                SELECT *,
                       (next_here IS NULL
                        OR (here = 0 AND next_here = 1 AND next_subject = subject_id)) AS is_end
                FROM device_rows
            ),
            device_edges AS (
                SELECT world_id, instance_id, occurred_at, id, is_end,
                       COALESCE(LAG(is_end) OVER (
                           PARTITION BY world_id, instance_id, device ORDER BY occurred_at, id), true) AS is_start
                FROM device_marks
            ),
            stream AS (
                SELECT world_id, instance_id, subject_id, occurred_at, id, change AS people_change, cover_change, kind
                FROM people
                UNION ALL
                SELECT world_id, instance_id, NULL, occurred_at, id, 0, 1, -1 FROM device_edges WHERE is_start
                UNION ALL
                SELECT world_id, instance_id, NULL, occurred_at, id, 0, -1, 1 FROM device_edges WHERE is_end
            ),
            run AS (
                SELECT s.*,
                       SUM(people_change) OVER w AS people,
                       SUM(cover_change) OVER w AS covered,
                       LAG(subject_id) OVER w AS prev_subject,
                       LAG(occurred_at) OVER w AS prev_at,
                       LAG(people_change) OVER w AS prev_people_change
                FROM stream s
                WINDOW w AS (PARTITION BY world_id, instance_id ORDER BY occurred_at, kind, id ROWS UNBOUNDED PRECEDING)
            ),
            marked AS (
                SELECT r.*,
                       LAG(covered) OVER w AS prev_covered,
                       MIN(CASE WHEN covered > 0 THEN occurred_at END) OVER (
                           PARTITION BY world_id, instance_id
                           ORDER BY occurred_at, kind, id
                           ROWS BETWEEN 1 FOLLOWING AND UNBOUNDED FOLLOWING) AS resumed_at
                FROM run r
                WINDOW w AS (PARTITION BY world_id, instance_id ORDER BY occurred_at, kind, id)
            )
            SELECT world_id, instance_id, occurred_at, people::int, resumed_at,
                   CASE
                       WHEN kind = 0 THEN subject_id
                       WHEN prev_at = occurred_at AND prev_people_change = -1 THEN prev_subject
                   END AS leaver
            FROM marked
            WHERE covered = 0 AND COALESCE(prev_covered, 0) > 0 AND people > 0
            ORDER BY occurred_at
            """;

        var rows = await _sql.ReadAsync(
            Sql,
            r => (
                WorldId: r.GetString(0),
                InstanceId: r.GetString(1),
                StartedAt: AnalyticsSql.InstantOf(r, 2),
                People: r.GetInt32(3),
                ResumedAt: AnalyticsSql.InstantOrNull(r, 4),
                Leaver: r.IsDBNull(5) ? null : r.GetString(5)),
            ct,
            ("leave", FactType.InstanceLeft),
            ("presence", AnalyticsSql.PresenceTypes),
            ("mods", roster.ToArray()),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        if (rows.Count == 0)
            return [];

        var closes = await InstanceClosesAsync(from, to, ct);
        var names = await _sql.NamesAsync(rows.Where(r => r.Leaver is not null).Select(r => r.Leaver!).ToList(), ct);

        return rows
            .Select(r =>
            {
                var closedAt = closes.GetValueOrDefault((r.WorldId, r.InstanceId));
                var closed = closedAt is { } c && c >= r.StartedAt ? c : (DateTimeOffset?)null;

                var (endedAt, endedBy) = (r.ResumedAt, closed) switch
                {
                    (null, null) => ((DateTimeOffset?)null, "unknown"),
                    ({ } resumed, null) => (resumed, "moderator-arrived"),
                    (null, { } closedAtInstant) => (closedAtInstant, "instance-closed"),
                    ({ } resumed, { } closedAtInstant) => resumed <= closedAtInstant
                        ? (resumed, "moderator-arrived")
                        : (closedAtInstant, "instance-closed"),
                };

                return new CoverageGap(
                    r.WorldId,
                    r.InstanceId,
                    r.StartedAt,
                    endedAt,
                    endedBy,
                    r.People,
                    r.Leaver is null ? null : new Person("vrchat", r.Leaver, names.GetValueOrDefault(r.Leaver)));
            })
            .ToList();
    }

    private async Task<Dictionary<(string World, string Instance), DateTimeOffset>> InstanceClosesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        const string Sql = """
            SELECT e.world_id, e.instance_id, MAX(e.occurred_at)
            FROM modbot_event e
            WHERE e.type = @close AND e.occurred_at >= @from AND e.occurred_at < @to
              AND e.world_id IS NOT NULL AND e.instance_id IS NOT NULL
            GROUP BY 1, 2
            """;

        var rows = await _sql.ReadAsync(
            Sql,
            r => (Key: (r.GetString(0), r.GetString(1)), At: AnalyticsSql.InstantOf(r, 2)),
            ct,
            ("close", FactType.GroupInstanceClosed),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        return rows.ToDictionary(r => r.Key, r => r.At);
    }

    private async Task<int> InstancesWatchedAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string Sql = """
            SELECT COUNT(*)::int FROM (
                SELECT DISTINCT e.world_id, e.instance_id
                FROM modbot_event e
                WHERE e.type = ANY(@presence) AND e.occurred_at >= @from AND e.occurred_at < @to
                  AND e.world_id IS NOT NULL AND e.instance_id IS NOT NULL
            ) w
            """;

        var rows = await _sql.ReadAsync(Sql, r => r.GetInt32(0), ct,
            ("presence", AnalyticsSql.PresenceTypes),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        return rows.Count > 0 ? rows[0] : 0;
    }

    private async Task<int> InstancesOpenedWithoutAnyWatchAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string Sql = """
            SELECT COUNT(*)::int FROM (
                SELECT DISTINCT c.world_id, c.instance_id
                FROM modbot_event c
                WHERE c.type = @create AND c.occurred_at >= @from AND c.occurred_at < @to
                  AND c.world_id IS NOT NULL AND c.instance_id IS NOT NULL
            ) opened
            WHERE NOT EXISTS (
                SELECT 1 FROM modbot_event e
                WHERE e.type = ANY(@presence)
                  AND e.world_id = opened.world_id AND e.instance_id = opened.instance_id
                  AND e.occurred_at >= @from)
            """;

        var rows = await _sql.ReadAsync(Sql, r => r.GetInt32(0), ct,
            ("create", FactType.GroupInstanceCreated),
            ("presence", AnalyticsSql.PresenceTypes),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        return rows.Count > 0 ? rows[0] : 0;
    }
}
