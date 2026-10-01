using Microsoft.EntityFrameworkCore;

namespace Modbot.Api.Features.Analytics.Team;

/// <summary>
/// "When is nobody covering?" as a week: the busy hours, and in which of them nobody from the team
/// was on (<see cref="CoverWeek"/>).
/// </summary>
/// <remarks>
/// <para>
/// A list of gaps cannot answer "when": a head moderator wants to see that Friday evenings keep
/// going uncovered, which is a rota change, and not read twelve rows each with a name on it
/// (analytics design review F9). So the gaps are laid over the hours of the week.
/// </para>
/// <para>
/// <strong>Busy comes from head counts, cover from presence.</strong> VRChat's head count for
/// every open group instance needs no moderator's client, so it is the only fair measure of how
/// full an instance was while nobody from the team was in it. The companion's presence reports say
/// whether a moderator was there. The two are never blended into one number (peaks spec 3.3): one
/// decides whether an hour counts at all, the other what kind of hour it was.
/// </para>
/// <para>
/// <strong>Not seen is its own answer.</strong> A busy instance no companion reported from in an
/// hour may have had a moderator in it without the client. Calling that hour uncovered would blame
/// the team for what Modbot cannot see, so it is counted apart and drawn apart. The gaps themselves
/// are the ones the list shows, found exactly as before; only where they are drawn is new.
/// </para>
/// </remarks>
public sealed class TeamCoverWeek(AnalyticsSql sql)
{
    private const int Buckets = 168;

    /// <summary>As in the gaps query: how far outside an instance's life a presence report still counts as from it.</summary>
    private static readonly TimeSpan PresenceLeeway = TimeSpan.FromMinutes(5);

    public async Task<CoverWeek> RunAsync(
        DateOnly from,
        DateOnly to,
        DateTimeOffset now,
        int people,
        int savedPeople,
        IReadOnlyList<CoverageGap> gaps,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gaps);

        var busy = new int[Buckets];
        var nobodyOn = new int[Buckets];
        var notSeen = new int[Buckets];
        var answer = new CoverWeek(people, savedPeople, busy, nobodyOn, notSeen);

        var group = await sql.Db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ManagedGroupId)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(group))
            return answer;

        var hours = await BusyHoursAsync(group, from, to, now, people, ct);
        if (hours.Count == 0)
            return answer;

        var seen = await SeenAsync(hours, now, ct);

        var gapsByPlace = gaps
            .GroupBy(g => (g.WorldId, g.InstanceId))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var hour in hours.GroupBy(h => h.HourStart))
        {
            var start = hour.Key;
            var end = start.AddHours(1);

            var states = hour.Select(h => StateOf(h, start, end, gapsByPlace, seen)).ToList();
            var bucket = BucketOf(start);

            busy[bucket]++;

            if (states.Contains(HourState.NobodyOn))
                nobodyOn[bucket]++;
            else if (states.Contains(HourState.NotSeen))
                notSeen[bucket]++;
        }

        return answer;
    }

    private enum HourState
    {
        Covered,
        NobodyOn,
        NotSeen,
    }

    private static HourState StateOf(
        BusyHour hour,
        DateTimeOffset start,
        DateTimeOffset end,
        Dictionary<(string, string), List<CoverageGap>> gapsByPlace,
        IReadOnlyDictionary<Guid, (DateTimeOffset First, DateTimeOffset Last)> seen)
    {
        if (hour.Number is not null && gapsByPlace.TryGetValue((hour.WorldId, hour.Number), out var here))
        {
            var overlaps = here.Any(g =>
                (g.ModbotInstanceId is null || g.ModbotInstanceId == hour.InstanceId)
                && g.StartedAt < end
                // A gap whose end nobody saw is drawn only in the hour it began: after that nothing
                // is known about the instance, and what is not known is "not seen", not "nobody on".
                && (g.EndedAt ?? g.StartedAt.AddTicks(1)) > start);

            if (overlaps)
                return HourState.NobodyOn;
        }

        return seen.TryGetValue(hour.InstanceId, out var span) && span.First < end && span.Last >= start
            ? HourState.Covered
            : HourState.NotSeen;
    }

    /// <summary>Monday 00:00 UTC is bucket 0, as the Activity tab's heatmap counts them.</summary>
    private static int BucketOf(DateTimeOffset hourStart)
    {
        var utc = hourStart.UtcDateTime;
        var day = ((int)utc.DayOfWeek + 6) % 7;
        return day * 24 + utc.Hour;
    }

    private sealed record BusyHour(
        Guid InstanceId,
        string WorldId,
        string? Number,
        DateTimeOffset OpenedAt,
        DateTimeOffset EndedAt,
        DateTimeOffset HourStart);

    /// <summary>
    /// Every clock hour, per group instance, in which the instance held at least
    /// <paramref name="people"/> people.
    /// </summary>
    /// <remarks>
    /// A head count row is written only when the count changes, so the count during an hour is the
    /// one carried in from before it plus every change inside it; the hour is busy when the most of
    /// those reaches the bar. Hours are cut on UTC, the boundary the daily totals and the heatmaps
    /// use, and clipped to the instance's life and to the window.
    /// </remarks>
    private async Task<IReadOnlyList<BusyHour>> BusyHoursAsync(
        string group,
        DateOnly from,
        DateOnly to,
        DateTimeOffset now,
        int people,
        CancellationToken ct)
    {
        const string Sql = """
            WITH lives AS (
                SELECT i.id, i.world_id, i.vr_chat_instance_id AS number, i.opened_at,
                       COALESCE(i.closed_at, i.last_seen_at, @now) AS ended_at,
                       GREATEST(i.opened_at, @from) AS s,
                       LEAST(COALESCE(i.closed_at, i.last_seen_at, @now), @to) AS e
                FROM vrchat_instance i
                WHERE i.group_id = @group
                  AND i.opened_at < @to
                  AND COALESCE(i.closed_at, i.last_seen_at, @now) > @from
            ),
            hours AS (
                SELECT l.*, h.at AS hour_start
                FROM lives l
                CROSS JOIN LATERAL generate_series(
                    date_trunc('hour', l.s AT TIME ZONE 'UTC') AT TIME ZONE 'UTC',
                    l.e,
                    interval '1 hour') AS h(at)
                WHERE l.e > l.s AND h.at < l.e
            )
            SELECT hr.id, hr.world_id, hr.number, hr.opened_at, hr.ended_at, hr.hour_start
            FROM hours hr
            WHERE GREATEST(
                COALESCE((
                    SELECT MAX(c.head_count) FROM instance_head_count c
                    WHERE c.instance_id = hr.id
                      AND c.counted_at >= GREATEST(hr.hour_start, hr.s)
                      AND c.counted_at < LEAST(hr.hour_start + interval '1 hour', hr.e)), 0),
                COALESCE((
                    SELECT c.head_count FROM instance_head_count c
                    WHERE c.instance_id = hr.id AND c.counted_at < GREATEST(hr.hour_start, hr.s)
                    ORDER BY c.counted_at DESC
                    LIMIT 1), 0)) >= @people
            """;

        return await sql.ReadAsync(
            Sql,
            r => new BusyHour(
                r.GetGuid(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                AnalyticsSql.InstantOf(r, 3),
                AnalyticsSql.InstantOf(r, 4),
                AnalyticsSql.InstantOf(r, 5)),
            ct,
            ("group", group),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)),
            ("now", now),
            ("people", people));
    }

    /// <summary>
    /// For each busy instance with a number, the first and last presence report made from it while
    /// it ran: the stretch a companion was there to see it.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, (DateTimeOffset First, DateTimeOffset Last)>> SeenAsync(
        IReadOnlyList<BusyHour> hours,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var instances = hours
            .Where(h => h.Number is not null)
            .GroupBy(h => h.InstanceId)
            .Select(g => g.First())
            .ToList();

        if (instances.Count == 0)
            return new Dictionary<Guid, (DateTimeOffset, DateTimeOffset)>();

        const string Sql = """
            SELECT o.id, MIN(e.occurred_at), MAX(e.occurred_at)
            FROM unnest(@ids::uuid[], @worlds::text[], @numbers::text[], @froms::timestamptz[], @tos::timestamptz[])
                 AS o(id, world_id, instance_id, from_at, to_at)
            JOIN modbot_event e
              ON e.type = ANY(@presence)
             AND e.world_id = o.world_id AND e.instance_id = o.instance_id
             AND e.occurred_at >= o.from_at AND e.occurred_at <= o.to_at
            GROUP BY o.id
            """;

        var rows = await sql.ReadAsync(
            Sql,
            r => (Id: r.GetGuid(0), First: AnalyticsSql.InstantOf(r, 1), Last: AnalyticsSql.InstantOf(r, 2)),
            ct,
            ("ids", instances.Select(i => i.InstanceId).ToArray()),
            ("worlds", instances.Select(i => i.WorldId).ToArray()),
            ("numbers", instances.Select(i => i.Number!).ToArray()),
            ("froms", instances.Select(i => (i.OpenedAt - PresenceLeeway).UtcDateTime).ToArray()),
            ("tos", instances.Select(i => ((i.EndedAt > now ? now : i.EndedAt) + PresenceLeeway).UtcDateTime).ToArray()),
            ("presence", AnalyticsSql.PresenceTypes));

        return rows.ToDictionary(r => r.Id, r => (r.First, r.Last));
    }
}
