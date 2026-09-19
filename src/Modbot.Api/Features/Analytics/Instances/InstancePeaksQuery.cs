using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Activity;
using Modbot.Core.Data;

namespace Modbot.Api.Features.Analytics.Instances;

/// <summary>
/// The peaks on the Instances page: how full it ever got, when, and how much of the window Modbot
/// was actually counting.
/// </summary>
/// <remarks>
/// <para>
/// Three reads, all bounded by the window. The day rows fold every change inside the window into at
/// most one row per UTC day, through an hour bucket so a stretch that spans midnight is credited to
/// both days it belongs to. The busiest instance is one ordered read of the same index range. The
/// coverage figures come from the instance table, bounded the same way the seed is.
/// </para>
/// <para>
/// Choosing the window's peak out of the day rows is done in <see cref="Peaks"/> rather than in SQL:
/// it is arithmetic over a few hundred rows, the tie rule matters, and a rule worth stating is worth
/// testing without a database.
/// </para>
/// </remarks>
public sealed class InstancePeaksQuery(ModbotContext db)
{
    private readonly AnalyticsSql _sql = new(db);

    public async Task<InstancePeaks> RunAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var group = settings?.ManagedGroupId;

        var windowDays = to.DayNumber - from.DayNumber + 1;

        // No group set up yet: nothing has ever been counted, and saying so is the whole answer.
        if (string.IsNullOrWhiteSpace(group))
            return InstancePeaks.Empty(windowDays);

        var days = await DaysAsync(group, from, to, ct);
        var coverage = await CoverageAsync(group, from, to, windowDays, days, ct);

        return new InstancePeaks(
            Peaks.MostPeople(days),
            Peaks.MostInstances(days),
            Peaks.Busiest(days),
            Peaks.BusiestHour(days),
            await BusiestInstanceAsync(group, from, to, ct),
            days.Select(d => new DayValue(d.Day, d.MostPeopleAtOnce)).ToList(),
            days.Select(d => new DayValue(d.Day, Math.Round(d.PeopleMinutes, 1))).ToList(),
            coverage);
    }

    /// <summary>
    /// One row per UTC day: how full it got, how many instances were counted, and its people-minutes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The staircase is cut at every hour boundary before anything is added up. Without that, a
    /// stretch running from 23:40 to 00:20 would be credited whole to one of the two days, and an
    /// instance that sat at twelve people from nine in the evening until two in the morning would
    /// put five hours of people-minutes on the wrong side of midnight. Cutting at hours rather than
    /// at days costs a handful more rows and gives the busiest hour for free.
    /// </para>
    /// <para>
    /// Each day carries its own busiest hour, so <see cref="Peaks.BusiestHour"/> can pick the
    /// window's busiest out of the day rows: the largest of a set of per-day largests is the largest
    /// overall, and the page never has to carry an hour row for every hour in the range.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ActivityDay>> DaysAsync(
        string group,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        var sql = $"""
            {InstanceActivitySql.Running}
            , spans AS (
                SELECT r.at_utc AS s,
                       (LEAST(COALESCE(LEAD(r.at) OVER (ORDER BY r.at, r.id), @to), @to) AT TIME ZONE 'UTC') AS e,
                       r.people, r.instances
                FROM running r
            ),
            parts AS (
                SELECT sp.people, sp.instances, h AS hour,
                       GREATEST(sp.s, h) AS s,
                       LEAST(sp.e, h + interval '1 hour') AS e
                FROM spans sp
                CROSS JOIN LATERAL generate_series(
                    date_trunc('hour', sp.s),
                    date_trunc('hour', sp.e - interval '1 microsecond'),
                    interval '1 hour') AS h
                WHERE sp.e > sp.s
            ),
            per_hour AS (
                SELECT p.hour,
                       MAX(p.people)::int AS most_people,
                       MAX(p.instances)::int AS most_instances,
                       (array_agg(p.s ORDER BY p.people DESC, p.s))[1] AS most_people_at,
                       (array_agg(p.s ORDER BY p.instances DESC, p.s))[1] AS most_instances_at,
                       SUM(p.people * EXTRACT(EPOCH FROM (p.e - p.s)) / 60.0)::numeric AS people_minutes
                FROM parts p
                GROUP BY p.hour
            )
            SELECT (per_hour.hour)::date AS day,
                   MAX(most_people)::int,
                   (array_agg(most_people_at ORDER BY most_people DESC, most_people_at))[1],
                   MAX(most_instances)::int,
                   (array_agg(most_instances_at ORDER BY most_instances DESC, most_instances_at))[1],
                   COALESCE(SUM(people_minutes), 0)::numeric,
                   (array_agg(per_hour.hour ORDER BY people_minutes DESC, per_hour.hour))[1],
                   COALESCE(MAX(people_minutes), 0)::numeric,
                   (array_agg(most_people ORDER BY people_minutes DESC, per_hour.hour))[1]::int
            FROM per_hour
            GROUP BY 1
            ORDER BY 1
            """;

        return await _sql.ReadAsync(
            sql,
            r => new ActivityDay(
                AnalyticsSql.DayOf(r, 0),
                r.GetInt32(1),
                Utc(r, 2),
                r.GetInt32(3),
                Utc(r, 4),
                r.GetDecimal(5),
                Utc(r, 6),
                r.GetDecimal(7),
                r.GetInt32(8)),
            ct,
            Parameters(group, from, to));
    }

    /// <summary>
    /// The single instance that held the most people at once, and when.
    /// </summary>
    /// <remarks>
    /// The reading itself, not the instance's stored <c>peak_user_count</c>: that one covers the
    /// instance's whole life, which may reach outside the window a moderator asked about, and it
    /// carries no time. A tie goes to the earliest reading, then to the instance's own id, so the
    /// same window always names the same instance.
    /// </remarks>
    private async Task<BusiestInstance?> BusiestInstanceAsync(
        string group,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        const string Sql = """
            SELECT h.instance_id, h.head_count, h.counted_at
            FROM instance_head_count h
            JOIN vrchat_instance i ON i.id = h.instance_id AND i.group_id = @group
            WHERE h.counted_at >= @from AND h.counted_at < @to AND h.head_count > 0
            ORDER BY h.head_count DESC, h.counted_at, h.instance_id
            LIMIT 1
            """;

        var rows = await _sql.ReadAsync(
            Sql,
            r => (Id: r.GetGuid(0), People: r.GetInt32(1), At: AnalyticsSql.InstantOf(r, 2)),
            ct,
            ("group", group),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        if (rows.Count == 0)
            return null;

        var best = rows[0];

        var place = await db.VRChatInstances.AsNoTracking()
            .Where(i => i.Id == best.Id)
            .Select(i => new
            {
                i.WorldId,
                i.VRChatInstanceId,
                i.OpenedAt,
                Name = db.VRChatWorlds.Where(w => w.WorldId == i.WorldId).Select(w => w.Name).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        return place is null
            ? null
            : new BusiestInstance(
                best.Id, place.WorldId, place.Name, place.VRChatInstanceId, place.OpenedAt, best.People, best.At);
    }

    /// <summary>
    /// How much of the window's instance time Modbot had a count for.
    /// </summary>
    /// <remarks>
    /// The denominator is the minutes the group's instances were open inside the window, not the
    /// window itself — see <see cref="InstanceCoverage"/> for why. An instance's counted stretch
    /// begins at its first head count ever, because from that moment the sync had it and kept it up
    /// to date; before it there is nothing, and after the instance ends there is nothing to count.
    /// </remarks>
    private async Task<InstanceCoverage> CoverageAsync(
        string group,
        DateOnly from,
        DateOnly to,
        int windowDays,
        IReadOnlyList<ActivityDay> days,
        CancellationToken ct)
    {
        const string Sql = """
            WITH lives AS (
                SELECT i.id,
                       GREATEST(i.opened_at, @from) AS s,
                       LEAST(COALESCE(i.closed_at, i.last_seen_at), @to) AS e
                FROM vrchat_instance i
                WHERE i.group_id = @group
                  AND i.opened_at >= @seedFrom AND i.opened_at < @to
                  AND COALESCE(i.closed_at, i.last_seen_at) >= @from
            ),
            counted AS (
                SELECT l.s, l.e, f.first_at
                FROM lives l
                LEFT JOIN LATERAL (
                    SELECT MIN(h.counted_at) AS first_at
                    FROM instance_head_count h
                    WHERE h.instance_id = l.id
                ) f ON true
            )
            SELECT COUNT(*)::int,
                   COUNT(*) FILTER (WHERE first_at IS NOT NULL AND first_at < e)::int,
                   COALESCE(SUM(EXTRACT(EPOCH FROM (e - s)) / 60.0)
                            FILTER (WHERE e > s), 0)::numeric,
                   COALESCE(SUM(EXTRACT(EPOCH FROM (e - GREATEST(s, first_at))) / 60.0)
                            FILTER (WHERE first_at IS NOT NULL AND e > GREATEST(s, first_at)), 0)::numeric
            FROM counted
            """;

        var rows = await _sql.ReadAsync(
            Sql,
            r => new InstanceCoverage(
                windowDays,
                days.Count(d => d.MostInstancesAtOnce > 0),
                r.GetInt32(0),
                r.GetInt32(1),
                Math.Round(r.GetDecimal(2), 1),
                Math.Round(r.GetDecimal(3), 1)),
            ct,
            Parameters(group, from, to));

        return rows.Count > 0 ? rows[0] : InstanceCoverage.Nothing;
    }

    private static (string Name, object? Value)[] Parameters(string group, DateOnly from, DateOnly to) =>
    [
        ("group", group),
        ("from", AnalyticsSql.DayStart(from)),
        ("to", AnalyticsSql.DayEnd(to)),
        ("seedFrom", InstanceActivitySql.SeedFrom(AnalyticsSql.DayStart(from))),
    ];

    /// <summary>A bare UTC timestamp column, read back as the instant it stands for.</summary>
    private static DateTimeOffset Utc(System.Data.Common.DbDataReader reader, int ordinal)
        => new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc), TimeSpan.Zero);
}
