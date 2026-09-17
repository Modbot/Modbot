using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Analytics.Group;

/// <summary>
/// The member count chart on My Group: every reading the group-info sync took, thinned to a
/// number of points a chart can draw.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Readings, not days.</strong> The sync reads the group about every five minutes and
/// keeps each reading in <c>group_member_count</c>. A day is 288 readings and a week is 2,016,
/// which is more than a line 800 pixels wide can show, so the window is cut into equal steps of
/// at least <c>span / MaxPoints</c> and the <em>last reading</em> in each step is kept. The last
/// reading and not an average, because every point on the chart is then a number VRChat actually
/// reported at the time shown, which is what the tooltip says it is.
/// </para>
/// <para>
/// <strong>Before the first reading, the facts.</strong> A deployment older than the readings
/// table has its earlier history only in <c>GroupInfoChanged</c> facts -- a baseline, then one
/// fact per change -- and a retention window can also delete old readings while the facts
/// (moderation class) stay. For the time before the earliest reading, the facts are read the way
/// the daily chart always read them: the last observation of each UTC day, each count carried
/// forward from the last fact that stated it. Those points go through the same thinning.
/// </para>
/// </remarks>
public sealed class GroupMemberCountQuery(ModbotContext db)
{
    /// <summary>The most points a range is served as. About one per pixel and a half at a common chart width.</summary>
    public const int MaxPoints = 500;

    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";
    public const string All = "all";

    private readonly AnalyticsSql _sql = new(db);

    /// <summary>How far back a range reaches, or null for all recorded time.</summary>
    /// <exception cref="ArgumentException">The range is not one of the four.</exception>
    public static TimeSpan? SpanOf(string range) => range switch
    {
        Day => TimeSpan.FromDays(1),
        Week => TimeSpan.FromDays(7),
        Month => TimeSpan.FromDays(30),
        All => null,
        _ => throw new ArgumentException($"`{range}` is not a range.", nameof(range)),
    };

    public static bool IsRange(string? range) => range is Day or Week or Month or All;

    /// <summary>
    /// The length of one step, in whole seconds, so that a window of <paramref name="span"/> comes
    /// out at no more than <see cref="MaxPoints"/> points. Never shorter than a second.
    /// </summary>
    public static int StepSeconds(TimeSpan span)
        => Math.Max(1, (int)Math.Ceiling(span.TotalSeconds / MaxPoints));

    /// <returns>Null when <paramref name="range"/> is not one of the four.</returns>
    public async Task<GroupMemberCountSeries?> RunAsync(string? range, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!IsRange(range))
            return null;

        var to = now;
        var firstReading = await db.GroupMemberCounts.AsNoTracking()
            .MinAsync(r => (DateTimeOffset?)r.CountedAt, ct);

        var from = SpanOf(range!) is { } span
            ? to - span
            : await FirstKnownAsync(firstReading, ct) ?? to;

        if (from > to)
            from = to;

        var step = StepSeconds(to - from);
        var points = from < to
            ? await PointsAsync(from, to, firstReading ?? to, step, ct)
            : [];

        return new GroupMemberCountSeries(range!, from, to, step, points, now);
    }

    /// <summary>The earliest time anything is known from: the first reading or the first fact that carried a count.</summary>
    private async Task<DateTimeOffset?> FirstKnownAsync(DateTimeOffset? firstReading, CancellationToken ct)
    {
        const string Sql = """
            SELECT MIN(COALESCE(e.occurred_before, e.occurred_at))
            FROM modbot_event e
            WHERE e.type = @type
              AND COALESCE(e.data->'changed'->'MemberCount'->>'new', e.data->'baseline'->>'MemberCount') IS NOT NULL
            """;

        var rows = await _sql.ReadAsync(Sql, r => AnalyticsSql.InstantOrNull(r, 0), ct, ("type", FactType.GroupInfoChanged));
        var firstFact = rows.Count > 0 ? rows[0] : null;

        return (firstReading, firstFact) switch
        {
            (null, null) => null,
            (null, var f) => f,
            (var r, null) => r,
            (var r, var f) => r < f ? r : f,
        };
    }

    /// <summary>
    /// The readings in the window, and the facts' daily observations from before the first
    /// reading, thinned to the last of each step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The steps are counted from <paramref name="from"/>, not from the epoch, so a window is at
    /// most <see cref="MaxPoints"/> steps and not that plus one straddling the start.
    /// </para>
    /// <para>
    /// The fact half is the carry-forward from the catch-up migration, written a second time here
    /// because a migration's SQL is frozen the day it ships and this query is not.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<MemberCountPoint>> PointsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset readingsFrom,
        int step,
        CancellationToken ct)
    {
        const string Sql = """
            SELECT p.at, p.members, p.online
            FROM (
                SELECT DISTINCT ON (floor(extract(epoch FROM q.at - @from) / @step))
                       q.at, q.members, q.online
                FROM (
                    SELECT c.counted_at AS at, c.member_count AS members, c.online_member_count AS online
                    FROM group_member_count c
                    WHERE c.counted_at >= @from AND c.counted_at < @to
                    UNION ALL
                    SELECT d.at, d.members, d.online
                    FROM (
                        SELECT DISTINCT ON ((k.at AT TIME ZONE 'UTC')::date) k.at, k.members, COALESCE(k.online, 0) AS online
                        FROM (
                            SELECT f.at, f.id,
                                   first_value(f.members) OVER (PARTITION BY f.members_run ORDER BY f.at, f.id) AS members,
                                   first_value(f.online) OVER (PARTITION BY f.online_run ORDER BY f.at, f.id) AS online
                            FROM (
                                SELECT r.at, r.id, r.members, r.online,
                                       count(r.members) OVER (ORDER BY r.at, r.id) AS members_run,
                                       count(r.online) OVER (ORDER BY r.at, r.id) AS online_run
                                FROM (
                                    SELECT COALESCE(e.occurred_before, e.occurred_at) AS at,
                                           e.id,
                                           COALESCE(e.data->'changed'->'MemberCount'->>'new', e.data->'baseline'->>'MemberCount')::int AS members,
                                           COALESCE(e.data->'changed'->'OnlineMemberCount'->>'new', e.data->'baseline'->>'OnlineMemberCount')::int AS online
                                    FROM modbot_event e
                                    WHERE e.type = @type
                                ) r
                                WHERE r.members IS NOT NULL OR r.online IS NOT NULL
                            ) f
                        ) k
                        WHERE k.members IS NOT NULL
                        ORDER BY (k.at AT TIME ZONE 'UTC')::date, k.at DESC, k.id DESC
                    ) d
                    WHERE d.at >= @from AND d.at < @to AND d.at < @readingsFrom
                ) q
                ORDER BY floor(extract(epoch FROM q.at - @from) / @step), q.at DESC
            ) p
            ORDER BY p.at
            """;

        return await _sql.ReadAsync(
            Sql,
            r => new MemberCountPoint(AnalyticsSql.InstantOf(r, 0), r.GetInt32(1), r.GetInt32(2)),
            ct,
            ("step", step),
            ("from", from),
            ("to", to),
            ("readingsFrom", readingsFrom),
            ("type", FactType.GroupInfoChanged));
    }
}
