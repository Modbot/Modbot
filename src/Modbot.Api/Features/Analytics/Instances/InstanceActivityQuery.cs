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
/// Thinned by the server, never by the chart: the window is cut into equal steps and the last
/// reading in each step is kept, so every point is a total that was true at the time shown. Picking
/// points in the browser would mean sending the whole staircase first, which is the cost the
/// thinning exists to avoid.
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

        var missing = from < to
            ? await new MissingDaysQuery(db).HeadCountsAsync(AnalyticsSql.DayOf(from), AnalyticsSql.DayOf(to), ct)
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
            SELECT p.at, p.people, p.instances
            FROM (
                SELECT DISTINCT ON (floor(EXTRACT(EPOCH FROM (r.at - @from)) / @step))
                       r.at, r.people::int AS people, r.instances::int AS instances, r.id
                FROM running r
                ORDER BY floor(EXTRACT(EPOCH FROM (r.at - @from)) / @step), r.at DESC, r.id DESC
            ) p
            ORDER BY p.at
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
