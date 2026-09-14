using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Analytics.Worlds;

/// <summary>
/// "Which of our worlds actually get used?" (spec 10.1).
/// </summary>
/// <remarks>
/// <para>
/// Time and visitors come from the desktop client's presence reports, which exist only while a
/// moderator's client is in the instance. A world nobody with the client visited reads as empty
/// however busy it was, and the page says so. Instances opened per world come from the audit log
/// and are complete.
/// </para>
/// <para>
/// The per-world time is computed live from facts, because a session is two facts about one
/// person and the daily totals count facts one at a time. Visitors per day per world come from
/// the daily totals and outlive a presence retention window.
/// </para>
/// </remarks>
public sealed class WorldsAnalyticsQuery(ModbotContext db)
{
    /// <summary>How many worlds get a per-day line. More than this on one chart is unreadable.</summary>
    public const int ChartedWorlds = 5;

    private readonly AnalyticsSql _sql = new(db);

    public async Task<WorldsAnalytics> RunAsync(
        DateOnly from,
        DateOnly to,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var totals = await _sql.DailyTotalsAsync(from, to,
            [DailyTotalMetrics.WorldInstances, DailyTotalMetrics.WorldVisitors], ct);

        var seen = await TimeSeenAsync(from, to, ct);

        var instancesByWorld = totals
            .Where(r => r.Metric == DailyTotalMetrics.WorldInstances)
            .GroupBy(r => r.Dimension, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Value), StringComparer.Ordinal);

        var worldIds = seen.Keys.Concat(instancesByWorld.Keys).Distinct(StringComparer.Ordinal).ToList();

        // The names, in one round trip. A world Modbot has only ever seen as an id has no row
        // here yet and keeps its id on screen, which is the truth about what is known.
        var named = await db.VRChatWorlds
            .AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId))
            .Select(w => new { w.WorldId, w.Name, w.AuthorName, w.ThumbnailImageUrl, w.Capacity })
            .ToDictionaryAsync(w => w.WorldId, w => w, StringComparer.Ordinal, ct);

        var worlds = worldIds
            .Select(id =>
            {
                var s = seen.GetValueOrDefault(id);
                var world = named.GetValueOrDefault(id);

                return new WorldSummary(
                    id,
                    world?.Name,
                    world?.AuthorName,
                    world?.ThumbnailImageUrl,
                    world?.Capacity,
                    s.Minutes,
                    s.Visitors,
                    s.Visits,
                    instancesByWorld.GetValueOrDefault(id),
                    s.LastSeenAt);
            })
            .OrderByDescending(w => w.MinutesSeen)
            .ThenByDescending(w => w.Visitors)
            .ThenByDescending(w => w.InstancesOpened)
            .ThenBy(w => w.Name ?? w.WorldId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The busiest worlds by people seen over the window get a line each; the rest are in
        // the table. Chosen by the daily totals so the chart and its own source agree.
        var charted = totals
            .Where(r => r.Metric == DailyTotalMetrics.WorldVisitors)
            .GroupBy(r => r.Dimension, StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(r => r.Value))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(ChartedWorlds)
            .Select(g => new WorldSeries(
                g.Key,
                g.OrderBy(r => r.Day).Select(r => new DayValue(r.Day, r.Value)).ToList()))
            .ToList();

        return new WorldsAnalytics(
            from,
            to,
            worlds,
            charted,
            await PresenceReportsAsync(from, to, ct),
            await AnalyticsCoverageQuery.RunAsync(db, ct),
            now);
    }

    /// <summary>
    /// Minutes seen, distinct people and arrivals per world, from presence sessions.
    /// </summary>
    /// <remarks>
    /// A person's presence in an instance is the last thing said about them there — arrival makes
    /// them present, a leave absent, repeats change nothing — so each arrival that changes state
    /// opens a session and the next state change closes it. A session nobody saw the end of
    /// closes at the last report from that instance, which is the last moment anything is known.
    /// </remarks>
    private async Task<Dictionary<string, (decimal Minutes, int Visitors, int Visits, DateTimeOffset? LastSeenAt)>> TimeSeenAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        const string Sql = """
            WITH p AS (
                SELECT e.world_id, e.instance_id, e.subject_id, e.occurred_at, e.id,
                       CASE WHEN e.type = @leave THEN 0 ELSE 1 END AS here
                FROM modbot_event e
                WHERE e.type = ANY(@presence)
                  AND e.occurred_at >= @from AND e.occurred_at < @to
                  AND e.world_id IS NOT NULL AND e.instance_id IS NOT NULL
            ),
            changes AS (
                SELECT p.*,
                       p.here - COALESCE(LAG(p.here) OVER (
                           PARTITION BY p.world_id, p.instance_id, p.subject_id
                           ORDER BY p.occurred_at, p.id), 0) AS change,
                       MAX(p.occurred_at) OVER (PARTITION BY p.world_id, p.instance_id) AS last_report
                FROM p
            ),
            sessions AS (
                SELECT world_id, instance_id, subject_id, change,
                       occurred_at AS started,
                       COALESCE(LEAD(occurred_at) OVER (
                           PARTITION BY world_id, instance_id, subject_id
                           ORDER BY occurred_at, id), last_report) AS ended
                FROM changes
                WHERE change <> 0
            )
            SELECT world_id,
                   (SUM(EXTRACT(EPOCH FROM (ended - started))) / 60.0)::numeric AS minutes,
                   COUNT(DISTINCT subject_id)::int AS visitors,
                   COUNT(*)::int AS visits,
                   MAX(ended) AS last_seen_at
            FROM sessions
            WHERE change = 1
            GROUP BY world_id
            """;

        var rows = await _sql.ReadAsync(
            Sql,
            r => (
                WorldId: r.GetString(0),
                Minutes: r.GetDecimal(1),
                Visitors: r.GetInt32(2),
                Visits: r.GetInt32(3),
                LastSeenAt: AnalyticsSql.InstantOrNull(r, 4)),
            ct,
            ("leave", FactType.InstanceLeft),
            ("presence", AnalyticsSql.PresenceTypes),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        return rows.ToDictionary(
            r => r.WorldId,
            r => (Math.Round(r.Minutes, 1), r.Visitors, r.Visits, r.LastSeenAt),
            StringComparer.Ordinal);
    }

    private async Task<long> PresenceReportsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string Sql = """
            SELECT COUNT(*) FROM modbot_event e
            WHERE e.type = ANY(@presence) AND e.occurred_at >= @from AND e.occurred_at < @to
            """;

        var rows = await _sql.ReadAsync(Sql, r => r.GetInt64(0), ct,
            ("presence", AnalyticsSql.PresenceTypes),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        return rows.Count > 0 ? rows[0] : 0;
    }
}
