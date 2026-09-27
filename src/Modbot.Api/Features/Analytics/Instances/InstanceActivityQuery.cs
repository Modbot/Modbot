using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Activity;
using Modbot.Core.Data;

namespace Modbot.Api.Features.Analytics.Instances;

/// <summary>
/// The line the Instances page draws: people in the group's instances, moment by moment.
/// </summary>
/// <remarks>
/// <para>
/// Its own range rather than the page's, for the same reason the member count chart has one. The
/// page's ranges are whole days of daily totals; this is a staircase of readings taken every thirty
/// seconds, where a day is the interesting range and a month is already a hundred thousand moments.
/// Folding it into the page would either send every reading with every page load or make one date
/// control mean two different things.
/// </para>
/// <para>
/// Thinned by the server, never by the chart. The window is cut into steps on fixed boundaries
/// (<see cref="ReadingRange"/>), and in each step three readings are kept: the one with the most
/// people, the one with the most instances open, and the last. The highest, because a line that
/// kept only each step's last reading dropped an evening's peak whenever it fell between two
/// readings kept, and never reached the "most people at once" printed above it. The last as well,
/// because a staircase holds each point until the next one, and a step's peak held across the
/// quiet hours after it would draw a crowd that had gone home. Every point is still a total that
/// was true at the time shown. Picking points in the browser would mean sending the whole
/// staircase first, which is the cost the thinning exists to avoid.
/// </para>
/// <para>
/// Several changes at the same instant (the sweep closing two instances on one clock reading) are
/// read as one: only the total after the last of them can be kept. That is the rule the page's
/// peaks use, so the chart and the tiles agree about the highest point.
/// </para>
/// </remarks>
public sealed class InstanceActivityQuery(ModbotContext db)
{
    private readonly AnalyticsSql _sql = new(db);

    /// <returns>Null when <paramref name="range"/> is not one of the four.</returns>
    public async Task<InstanceActivitySeries?> RunAsync(
        string? range,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (!ReadingRange.IsRange(range))
            return null;

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var group = settings?.ManagedGroupId;

        var to = now;
        var from = ReadingRange.SpanOf(range!) is { } span
            ? to - span
            : await FirstReadingAsync(ct) ?? to;

        if (from > to)
            from = to;

        var step = ReadingRange.StepSeconds(to - from);

        var points = string.IsNullOrWhiteSpace(group) || from >= to
            ? []
            : await PointsAsync(group!, from, to, step, ct);

        var days = new MissingDaysQuery(db);
        var missing = from < to
            ? await days.HeadCountsAsync(AnalyticsSql.DayOf(from), AnalyticsSql.DayOf(to), await days.ModbotStartAsync(ct), ct)
            : [];

        return new InstanceActivitySeries(range!, from, to, step, points, now, missing);
    }

    /// <summary>
    /// The first head count Modbot ever recorded — what "all" means for this chart.
    /// </summary>
    /// <remarks>
    /// Asked of the whole table without a group filter, because the sync only ever writes head
    /// counts for the group's own open instances, and the filtered version could not use the index
    /// on <c>counted_at</c> that makes this a single index entry rather than a scan.
    /// </remarks>
    private async Task<DateTimeOffset?> FirstReadingAsync(CancellationToken ct)
        => await db.InstanceHeadCounts.AsNoTracking().MinAsync(h => (DateTimeOffset?)h.CountedAt, ct);

    private async Task<IReadOnlyList<ActivityPoint>> PointsAsync(
        string group,
        DateTimeOffset from,
        DateTimeOffset to,
        int step,
        CancellationToken ct)
    {
        var sql = $"""
            {InstanceActivitySql.Running}
            , settled AS (
                SELECT r.at, r.id, r.people::int AS people, r.instances::int AS instances,
                       floor(EXTRACT(EPOCH FROM r.at) / @step)::bigint AS step_no,
                       LEAD(r.at) OVER (ORDER BY r.at, r.id) AS next_at
                FROM running r
            ),
            ranked AS (
                SELECT s.at, s.id, s.people, s.instances,
                       row_number() OVER (PARTITION BY s.step_no ORDER BY s.people DESC, s.instances DESC, s.at, s.id) AS by_people,
                       row_number() OVER (PARTITION BY s.step_no ORDER BY s.instances DESC, s.people DESC, s.at, s.id) AS by_instances,
                       row_number() OVER (PARTITION BY s.step_no ORDER BY s.at DESC, s.id DESC) AS by_last
                FROM settled s
                WHERE s.next_at IS NULL OR s.next_at > s.at
            )
            SELECT p.at, p.people, p.instances
            FROM ranked p
            WHERE p.by_people = 1 OR p.by_instances = 1 OR p.by_last = 1
            ORDER BY p.at, p.id
            """;

        return await _sql.ReadAsync(
            sql,
            r => new ActivityPoint(AnalyticsSql.InstantOf(r, 0), r.GetInt32(1), r.GetInt32(2)),
            ct,
            ("group", group),
            ("from", from),
            ("to", to),
            ("seedFrom", InstanceActivitySql.SeedFrom(from)),
            ("step", step));
    }
}
