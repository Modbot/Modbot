using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;

namespace Modbot.Api.Features.Analytics;

/// <summary>
/// How far each source reaches, for the footer every analytics page carries.
/// </summary>
public static class AnalyticsCoverageQuery
{
    public static async Task<AnalyticsCoverage> RunAsync(ModbotContext db, CancellationToken ct)
    {
        var dailyTotalsFirst = await db.DailyTotals.AsNoTracking().MinAsync(r => (DateOnly?)r.Day, ct);
        var dailyTotalsLast = await db.DailyTotals.AsNoTracking().MaxAsync(r => (DateOnly?)r.Day, ct);
        var state = await db.DailyTotalsState.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        var factFirst = await db.Events.AsNoTracking().MinAsync(e => (DateTimeOffset?)e.OccurredAt, ct);
        var factLast = await db.Events.AsNoTracking().MaxAsync(e => (DateTimeOffset?)e.OccurredAt, ct);

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        var moderation = settings?.ModerationFactRetentionDays ?? 0;
        var presence = settings?.PresenceFactRetentionDays ?? 0;

        return new AnalyticsCoverage(
            dailyTotalsFirst,
            dailyTotalsLast,
            state?.UpdatedAt,
            factFirst is null ? null : AnalyticsSql.DayOf(factFirst.Value),
            factLast is null ? null : AnalyticsSql.DayOf(factLast.Value),
            moderation > 0 || presence > 0,
            moderation,
            presence);
    }

    /// <summary>
    /// The first day one analytics page has anything for — what "all time" means on that page —
    /// or null when it has nothing at all.
    /// </summary>
    /// <remarks>
    /// Every part is one index lookup: a first day per metric on the daily totals' metric index, a
    /// first fact per type on the fact log's type index, and the first row of each table the page
    /// keeps. Only the parts the page reads are asked for.
    /// </remarks>
    public static async Task<DateOnly?> FirstDayAsync(ModbotContext db, PageSources sources, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var parts = new List<string>
        {
            """
            (SELECT MIN(f.first_day)
             FROM unnest(@metrics) AS m(metric)
             CROSS JOIN LATERAL (
                 SELECT MIN(t.day) AS first_day
                 FROM modbot_daily_total t
                 WHERE t.metric = m.metric
             ) f)
            """,
            """
            (SELECT (MIN(f.first_at) AT TIME ZONE 'UTC')::date
             FROM unnest(@types) AS ft(type)
             CROSS JOIN LATERAL (
                 SELECT MIN(e.occurred_at) AS first_at
                 FROM modbot_event e
                 WHERE e.type = ft.type
             ) f)
            """,
        };

        var parameters = new List<(string Name, object? Value)>
        {
            ("metrics", sources.Metrics.ToArray()),
            ("types", sources.FactTypes.ToArray()),
        };

        if (sources.GroupInstances || sources.MemberCountReadings)
        {
            var group = await db.Settings.AsNoTracking()
                .Where(s => s.Id == 1)
                .Select(s => s.ManagedGroupId)
                .FirstOrDefaultAsync(ct);

            // No group set up: an empty id matches no row, which is the truth.
            parameters.Add(("group", string.IsNullOrWhiteSpace(group) ? string.Empty : group));
        }

        if (sources.GroupInstances)
            parts.Add("(SELECT (MIN(i.opened_at) AT TIME ZONE 'UTC')::date FROM vrchat_instance i WHERE i.group_id = @group)");

        if (sources.HeadCounts)
            parts.Add("(SELECT (MIN(h.counted_at) AT TIME ZONE 'UTC')::date FROM instance_head_count h)");

        if (sources.MemberCountReadings)
            parts.Add("(SELECT (MIN(c.counted_at) AT TIME ZONE 'UTC')::date FROM group_member_count c WHERE c.group_id = @group)");

        var rows = await new AnalyticsSql(db).ReadAsync(
            $"SELECT LEAST({string.Join(", ", parts)})",
            r => r.IsDBNull(0) ? (DateOnly?)null : AnalyticsSql.DayOf(r, 0),
            ct,
            [.. parameters]);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// The first day either source knows about, across the whole store — every page's data at
    /// once. The analytics pages each start at their own first day instead (see
    /// <see cref="PageSources"/>).
    /// </summary>
    public static async Task<DateOnly?> FirstDayAsync(ModbotContext db, CancellationToken ct)
    {
        var dailyTotalsFirst = await db.DailyTotals.AsNoTracking().MinAsync(r => (DateOnly?)r.Day, ct);
        var factFirst = await db.Events.AsNoTracking().MinAsync(e => (DateTimeOffset?)e.OccurredAt, ct);

        var factDay = factFirst is null ? null : (DateOnly?)AnalyticsSql.DayOf(factFirst.Value);

        return (dailyTotalsFirst, factDay) switch
        {
            (null, null) => null,
            (null, var f) => f,
            (var d, null) => d,
            (var d, var f) => d < f ? d : f,
        };
    }
}
