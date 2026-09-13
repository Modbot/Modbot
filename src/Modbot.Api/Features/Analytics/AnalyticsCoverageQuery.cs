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
    /// The first day either source knows about — what "all time" means for a window.
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
