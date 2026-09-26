using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Api.Features.Places;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Analytics.Instances;

/// <summary>
/// "When is the community actually active?" (spec 10.1).
/// </summary>
/// <remarks>
/// <para>
/// Opened and closed per day come from the daily totals. Everything about an instance's life —
/// how long it stayed open, how many were open at once — is computed live from the audit log's
/// instance facts, keyed on <c>(world_id, instance_id)</c> because an instance id is unique only
/// within its world (spec 5.3). Population comes from presence reports and so only covers
/// instances a moderator's client was in.
/// </para>
/// <para>
/// <strong>An instance with no close on record is open until the last thing seen in it.</strong>
/// VRChat logs a close only when a moderator closes the instance; one that emptied out on its
/// own has no close. In the live sample forty percent of opened instances never got one, so
/// treating them as open until now would be plainly wrong, and inventing a fixed lifetime would
/// be a guess dressed as a measurement. The last kick, warn or presence report in the instance is
/// the last moment anything is known, and that is where its line stops.
/// </para>
/// <para>
/// The hour-of-week buckets are in UTC, like every day boundary here. The page shifts them to
/// the viewer's time zone, because "Tuesdays at 8pm" is only useful in the reader's own clock.
/// </para>
/// </remarks>
public sealed class InstancesAnalyticsQuery(ModbotContext db)
{
    private static readonly string[] InstanceTypes =
    [
        FactType.GroupInstanceCreated,
        FactType.GroupInstanceClosed,
        FactType.GroupInstanceUpdated,
        FactType.GroupInstanceAnnouncement,
        FactType.GroupInstanceKick,
        FactType.GroupInstanceWarn,
        FactType.InstanceJoined,
        FactType.InstancePresenceObserved,
        FactType.InstanceLeft,
    ];

    private readonly AnalyticsSql _sql = new(db);

    public async Task<InstancesAnalytics> RunAsync(
        DateOnly from,
        DateOnly to,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var totals = await _sql.DailyTotalsAsync(from, to,
            [DailyTotalMetrics.InstancesOpened, DailyTotalMetrics.InstancesClosed], ct);

        var lives = await LifetimesAsync(from, to, ct);

        var withBothEnds = lives
            .Where(l => l.ClosedAt is not null)
            .Select(l => (decimal)(l.ClosedAt!.Value - l.OpenedAt).TotalMinutes)
            .ToList();

        decimal? typical = withBothEnds.Count == 0 ? null : Median(withBothEnds);

        var missing = new MissingDaysQuery(db);

        return new InstancesAnalytics(
            from,
            to,
            MissingDays.Today(to, now),
            totals.Series(DailyTotalMetrics.InstancesOpened),
            totals.Series(DailyTotalMetrics.InstancesClosed),
            MostOpenAtOnce(lives, from, to),
            await MostPeopleInOneAsync(from, to, ct),
            typical,
            TypicalMinutesOpenPerDay(lives),
            withBothEnds.Count,
            lives.Count(l => l.OpenedAt >= AnalyticsSql.DayStart(from)),
            await InstancesAsync(openOnly: true, from, to, now, ct),
            await InstancesAsync(openOnly: false, from, to, now, ct),
            await HourOfWeekAsync(from, to, ct),
            await new InstancePeaksQuery(db).RunAsync(from, to, ct),
            await new PresenceCounts(db).ReportsAsync(from, to, ct),
            await missing.AuditLogAsync(from, to, ct),
            await missing.HeadCountsAsync(from, to, ct),
            await missing.PresenceReportsAsync(from, to, ct),
            await AnalyticsCoverageQuery.RunAsync(db, ct),
            now);
    }

    /// <summary>
    /// The median time open, day by day, over the instances that both opened and closed that day.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window's single median says whether evenings are long; the line says whether they are
    /// getting longer, which is the question anybody who looks at the number twice actually has.
    /// </para>
    /// <para>
    /// Counted on the day an instance closed, and only where both ends are on record — the same
    /// rule the window's median uses, so the line and the number beside it cannot disagree. An
    /// instance that ran past midnight lands on the day it finished.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<DayValue> TypicalMinutesOpenPerDay(IReadOnlyList<Lifetime> lives) => lives
        .Where(l => l.ClosedAt is not null)
        .GroupBy(l => AnalyticsSql.DayOf(l.ClosedAt!.Value))
        .OrderBy(g => g.Key)
        .Select(g => new DayValue(g.Key, Median(g.Select(l => (decimal)(l.ClosedAt!.Value - l.OpenedAt).TotalMinutes))))
        .ToList();

    /// <summary>The middle value, or the mean of the middle two. The list is never empty here.</summary>
    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.Order().ToList();

        return Math.Round(
            sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2,
            1);
    }

    /// <summary>How many instances the "recent" list carries. Enough to read, not a log.</summary>
    public const int RecentInstances = 25;

    /// <summary>
    /// The instances themselves, from <c>vrchat_instance</c> and named from <c>vrchat_world</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not from the fact log, and that is the point. The fact log keys an instance on
    /// <c>(world_id, instance_id)</c>, and VRChat hands the same instance number out again after
    /// an instance closes -- so the fact log cannot tell last Tuesday's instance from tonight's, while
    /// this table gives every instance an id of its own and can.
    /// </para>
    /// <para>
    /// Open instances ignore the window entirely. An instance that opened before the range a moderator
    /// happens to be looking at is still open now, and leaving it out of "open right now" to
    /// honour a date filter would answer a question nobody asked.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<InstanceRow>> InstancesAsync(
        bool openOnly,
        DateOnly from,
        DateOnly to,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var instances = db.VRChatInstances.AsNoTracking();

        instances = openOnly
            ? instances.Where(i => i.ClosedAt == null).OrderByDescending(i => i.HeadCount ?? i.LastUserCount).ThenByDescending(i => i.OpenedAt)
            : instances
                .Where(i => i.OpenedAt >= AnalyticsSql.DayStart(from) && i.OpenedAt < AnalyticsSql.DayEnd(to))
                .OrderByDescending(i => i.OpenedAt);

        // The row shape itself is built in one place (InstanceRows), because the world popup and a
        // person's own instances list the same row and all three must agree about how long an instance has
        // been open.
        return await InstanceRows.ReadAsync(db, instances.Take(RecentInstances), now, ct);
    }

    private sealed record Lifetime(string WorldId, string InstanceId, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, DateTimeOffset EndsAt);

    /// <summary>
    /// Every instance opened in the window (or the day before it, so one that straddles the
    /// window's start still counts as open), with when it closed or was last seen.
    /// </summary>
    private async Task<IReadOnlyList<Lifetime>> LifetimesAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string Sql = """
            SELECT e.world_id, e.instance_id,
                   MIN(e.occurred_at) FILTER (WHERE e.type = @create) AS opened,
                   MAX(e.occurred_at) FILTER (WHERE e.type = @close) AS closed,
                   MAX(e.occurred_at) AS last_seen
            FROM modbot_event e
            WHERE e.type = ANY(@types)
              AND e.occurred_at >= @from AND e.occurred_at < @to
              AND e.world_id IS NOT NULL AND e.instance_id IS NOT NULL
            GROUP BY 1, 2
            HAVING MIN(e.occurred_at) FILTER (WHERE e.type = @create) IS NOT NULL
            """;

        var rows = await _sql.ReadAsync(
            Sql,
            r => (
                WorldId: r.GetString(0),
                InstanceId: r.GetString(1),
                Opened: AnalyticsSql.InstantOf(r, 2),
                Closed: AnalyticsSql.InstantOrNull(r, 3),
                LastSeen: AnalyticsSql.InstantOf(r, 4)),
            ct,
            ("create", FactType.GroupInstanceCreated),
            ("close", FactType.GroupInstanceClosed),
            ("types", InstanceTypes),
            ("from", AnalyticsSql.DayStart(from.AddDays(-1))),
            ("to", AnalyticsSql.DayEnd(to)));

        return rows
            .Select(r =>
            {
                // A close before the open belongs to an earlier instance that reused the id.
                var closed = r.Closed is { } c && c >= r.Opened ? c : (DateTimeOffset?)null;
                var ends = closed ?? (r.LastSeen > r.Opened ? r.LastSeen : r.Opened);
                return new Lifetime(r.WorldId, r.InstanceId, r.Opened, closed, ends);
            })
            .ToList();
    }

    /// <summary>
    /// For each day in the window, the most instances open at one moment.
    /// </summary>
    /// <remarks>
    /// A sweep over open and end times rather than a count per day: two instances that were open
    /// on the same day but never at the same time are one at a time, not two.
    /// </remarks>
    private static IReadOnlyList<DayValue> MostOpenAtOnce(IReadOnlyList<Lifetime> lives, DateOnly from, DateOnly to)
    {
        if (lives.Count == 0)
            return [];

        var result = new List<DayValue>();

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var dayStart = AnalyticsSql.DayStart(day);
            var dayEnd = AnalyticsSql.DayEnd(day);

            var events = new List<(DateTimeOffset At, int Change)>();
            foreach (var life in lives)
            {
                if (life.OpenedAt >= dayEnd || life.EndsAt < dayStart)
                    continue;

                events.Add((life.OpenedAt < dayStart ? dayStart : life.OpenedAt, +1));
                events.Add((life.EndsAt, -1));
            }

            if (events.Count == 0)
                continue;

            // Opens before ends at the same instant: an instance opened as another closed
            // overlapped it for that instant, which is what "at once" means.
            var open = 0;
            var most = 0;
            foreach (var (_, change) in events.OrderBy(e => e.At).ThenBy(e => e.Change == -1 ? 1 : 0))
            {
                open += change;
                most = Math.Max(most, open);
            }

            result.Add(new DayValue(day, most));
        }

        return result;
    }

    /// <summary>
    /// The largest population known in any one instance, per day, from presence reports.
    /// </summary>
    private async Task<IReadOnlyList<DayValue>> MostPeopleInOneAsync(DateOnly from, DateOnly to, CancellationToken ct)
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
                           ORDER BY p.occurred_at, p.id), 0) AS change
                FROM p
            ),
            population AS (
                SELECT occurred_at,
                       SUM(change) OVER (
                           PARTITION BY world_id, instance_id
                           ORDER BY occurred_at, id ROWS UNBOUNDED PRECEDING) AS people
                FROM changes
                WHERE change <> 0
            )
            SELECT (occurred_at AT TIME ZONE 'UTC')::date AS day, MAX(people)::numeric
            FROM population
            GROUP BY 1
            ORDER BY 1
            """;

        return await _sql.ReadAsync(
            Sql,
            r => new DayValue(AnalyticsSql.DayOf(r, 0), r.GetDecimal(1)),
            ct,
            ("leave", FactType.InstanceLeft),
            ("presence", AnalyticsSql.PresenceTypes),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));
    }

    private async Task<HourOfWeek> HourOfWeekAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string Sql = """
            SELECT ((EXTRACT(ISODOW FROM (e.occurred_at AT TIME ZONE 'UTC'))::int - 1) * 24
                    + EXTRACT(HOUR FROM (e.occurred_at AT TIME ZONE 'UTC'))::int) AS bucket,
                   COUNT(*) FILTER (WHERE e.type = ANY(@arrivals))::numeric AS arrivals,
                   COUNT(*) FILTER (WHERE e.type = @create)::numeric AS opened
            FROM modbot_event e
            WHERE (e.type = ANY(@arrivals) OR e.type = @create)
              AND e.occurred_at >= @from AND e.occurred_at < @to
            GROUP BY 1
            """;

        var rows = await _sql.ReadAsync(
            Sql,
            r => (Bucket: r.GetInt32(0), Arrivals: r.GetDecimal(1), Opened: r.GetDecimal(2)),
            ct,
            ("arrivals", AnalyticsSql.ArrivalTypes),
            ("create", FactType.GroupInstanceCreated),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        var arrivals = new decimal[168];
        var opened = new decimal[168];

        foreach (var row in rows)
        {
            if (row.Bucket is < 0 or >= 168)
                continue;

            arrivals[row.Bucket] = row.Arrivals;
            opened[row.Bucket] = row.Opened;
        }

        return new HourOfWeek(arrivals, opened);
    }
}
