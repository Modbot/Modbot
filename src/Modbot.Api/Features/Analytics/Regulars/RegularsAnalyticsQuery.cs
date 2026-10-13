using Modbot.Api.Features.Places;
using Modbot.Core.Data;

namespace Modbot.Api.Features.Analytics.Regulars;

/// <summary>
/// "Do people come back?" and "who are our regulars?"
/// </summary>
/// <remarks>
/// <para>
/// <strong>The words, exactly.</strong> A <em>visit</em> is a join: the companion saw a person arrive
/// in one of the group's instances, or already there when it arrived (the two arrival facts, as the
/// heatmap counts them). A <em>visitor in week W</em> is anybody with a visit in that week. They
/// <em>came back</em> if they have a visit in any of the next four weeks (<see cref="ReturnWeeks"/>),
/// in any group instance: coming back to the same world is not asked. The <em>return rate</em> of
/// week W is the visitors who came back over the visitors.
/// </para>
/// <para>
/// A <em>regular</em> is somebody who visited in at least three (<see cref="RegularNeeds"/>) of the
/// latest four whole weeks (<see cref="RegularWeeks"/>). Fixed here, not a setting: the count that
/// feels right differs between a group that meets nightly and one that meets monthly, but a number
/// a moderator can read at a glance and compare with last month is worth more than one they must
/// first decide on.
/// </para>
/// <para>
/// Weeks run Monday to Sunday in UTC, like every day boundary here. A week's rate is left out until
/// the four weeks after it are over, and the week now under way is never counted in the regulars'
/// four, so a figure does not rise or fall after it was first shown only because the week was
/// unfinished.
/// </para>
/// <para>
/// <strong>What the facts cover.</strong> Joins come from the companion's presence reports, so only
/// time a moderator's client stood in the instance is seen. Somebody who did come back to an
/// instance nobody from the team attended is not counted as returning; the rate is a floor, and
/// the page counts the reports so a thin week reads as thin.
/// </para>
/// <para>
/// <strong>What it costs.</strong> One pass over the join facts from the first week to four weeks
/// past the last (the fact table is partitioned by month on <c>occurred_at</c>, so the bounds prune
/// partitions), grouped to one row per person per week inside SQL; the "next visit" is a
/// <c>LEAD</c> over that, not a lookup per person per week. The regulars are a second pass over four
/// weeks.
/// </para>
/// </remarks>
public sealed class RegularsAnalyticsQuery(ModbotContext db)
{
    /// <summary>How many weeks after a visit still count as coming back.</summary>
    public const int ReturnWeeks = 4;

    /// <summary>How many of the latest whole weeks "regular" looks at.</summary>
    public const int RegularWeeks = 4;

    /// <summary>How many of those weeks somebody must have joined in to be a regular.</summary>
    public const int RegularNeeds = 3;

    /// <summary>How many regulars the list names. The count above it is of all of them.</summary>
    public const int ListedRegulars = 200;

    /// <summary>What this page draws from, and so where its "all time" starts: the two arrival facts.</summary>
    public static readonly PageSources Sources = PageSources.Of([], AnalyticsSql.ArrivalTypes);

    private readonly AnalyticsSql _sql = new(db);

    public async Task<RegularsAnalytics> RunAsync(
        DateOnly from,
        DateOnly to,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var today = AnalyticsSql.DayOf(now);

        var firstWeek = WeekStart(from);
        var lastWeek = WeekStart(to);

        var counted = await WeeksAsync(firstWeek, lastWeek, ct);
        var weeks = new List<WeekReturn>();

        for (var week = firstWeek; week <= lastWeek; week = week.AddDays(7))
        {
            counted.TryGetValue(week, out var row);
            weeks.Add(new WeekReturn(week, row.Visitors, IsSettled(week, today) ? row.CameBack : null));
        }

        // The latest whole weeks: the one under way is left out.
        var regularsTo = WeekStart(today);
        var regularsFrom = regularsTo.AddDays(-7 * RegularWeeks);
        var (total, listed) = await RegularsAsync(regularsFrom, regularsTo, ct);

        var names = await _sql.NamesAsync(listed.Select(r => r.UserId).ToList(), ct);

        return new RegularsAnalytics(
            from,
            to,
            weeks,
            ReturnWeeks,
            RegularWeeks,
            RegularNeeds,
            regularsFrom,
            regularsTo.AddDays(-1),
            total,
            listed.Select(r => r with { Name = names.GetValueOrDefault(r.UserId) }).ToList(),
            await new PresenceCounts(db).ReportsAsync(from, to, ct),
            await AnalyticsCoverageQuery.RunAsync(db, ct),
            now);
    }

    /// <summary>The Monday (UTC) of the week a day falls in.</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>
    /// Whether the four weeks after <paramref name="week"/> are over, so its figure will not change:
    /// the day after the fourth of them ended has come.
    /// </summary>
    public static bool IsSettled(DateOnly week, DateOnly today) => week.AddDays(7 * (ReturnWeeks + 1)) <= today;

    /// <summary>
    /// For each week in the range: the visitors, and how many of them have a visit in one of the
    /// next four weeks. The look-ahead may run past the last week asked for; it is what decides it.
    /// </summary>
    private async Task<Dictionary<DateOnly, (int Visitors, int CameBack)>> WeeksAsync(
        DateOnly firstWeek, DateOnly lastWeek, CancellationToken ct)
    {
        const string Sql = """
            WITH visits AS (
                SELECT e.subject_id,
                       date_trunc('week', e.occurred_at AT TIME ZONE 'UTC')::date AS week
                FROM modbot_event e
                WHERE e.type = ANY(@arrivals)
                  AND e.occurred_at >= @from AND e.occurred_at < @lookUntil
                GROUP BY 1, 2
            ),
            next_visit AS (
                SELECT v.subject_id, v.week,
                       LEAD(v.week) OVER (PARTITION BY v.subject_id ORDER BY v.week) AS next_week
                FROM visits v
            )
            SELECT n.week,
                   COUNT(*)::int AS visitors,
                   (COUNT(*) FILTER (WHERE n.next_week <= n.week + @reach))::int AS came_back
            FROM next_visit n
            WHERE n.week <= @lastWeek
            GROUP BY n.week
            ORDER BY n.week
            """;

        var rows = await _sql.ReadAsync(
            Sql,
            r => (Week: AnalyticsSql.DayOf(r, 0), Visitors: r.GetInt32(1), CameBack: r.GetInt32(2)),
            ct,
            ("arrivals", AnalyticsSql.ArrivalTypes),
            ("from", AnalyticsSql.DayStart(firstWeek)),
            ("lookUntil", AnalyticsSql.DayStart(lastWeek.AddDays(7 * (ReturnWeeks + 1)))),
            ("lastWeek", lastWeek),
            ("reach", 7 * ReturnWeeks));

        return rows.ToDictionary(r => r.Week, r => (r.Visitors, r.CameBack));
    }

    /// <summary>Everybody with a visit in at least <see cref="RegularNeeds"/> weeks of the range, and the first of them.</summary>
    private async Task<(int Total, IReadOnlyList<Regular> Listed)> RegularsAsync(
        DateOnly from, DateOnly until, CancellationToken ct)
    {
        const string Sql = """
            WITH weekly AS (
                SELECT e.subject_id,
                       date_trunc('week', e.occurred_at AT TIME ZONE 'UTC')::date AS week,
                       COUNT(DISTINCT (e.occurred_at AT TIME ZONE 'UTC')::date) AS days,
                       MAX(e.occurred_at) AS last_at
                FROM modbot_event e
                WHERE e.type = ANY(@arrivals)
                  AND e.occurred_at >= @from AND e.occurred_at < @until
                GROUP BY 1, 2
            ),
            regulars AS (
                SELECT w.subject_id,
                       COUNT(*)::int AS weeks,
                       SUM(w.days)::int AS days,
                       MAX(w.last_at) AS last_at
                FROM weekly w
                GROUP BY w.subject_id
                HAVING COUNT(*) >= @needs
            )
            SELECT r.subject_id, r.weeks, r.days, r.last_at, (COUNT(*) OVER ())::int AS total
            FROM regulars r
            ORDER BY r.days DESC, r.weeks DESC, r.last_at DESC, r.subject_id
            LIMIT @limit
            """;

        var rows = await _sql.ReadAsync(
            Sql,
            r => (Regular: new Regular(r.GetString(0), null, r.GetInt32(1), r.GetInt32(2), AnalyticsSql.InstantOf(r, 3)), Total: r.GetInt32(4)),
            ct,
            ("arrivals", AnalyticsSql.ArrivalTypes),
            ("from", AnalyticsSql.DayStart(from)),
            ("until", AnalyticsSql.DayStart(until)),
            ("needs", RegularNeeds),
            ("limit", ListedRegulars));

        return (rows.Count == 0 ? 0 : rows[0].Total, rows.Select(r => r.Regular).ToList());
    }
}
