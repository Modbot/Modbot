using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Reviews;

/// <summary>
/// What the team usually does in a day, from the baselines: the number a busy day is compared to.
/// </summary>
/// <param name="UsualPerModeratorDay">Actions over active moderator-days, across the whole team.</param>
/// <param name="Days">Distinct days in the window on which anybody did anything. Zero means no baseline yet.</param>
/// <param name="ByModerator">Each moderator's own usual, keyed by id.</param>
public sealed record TeamBaseline(
    decimal UsualPerModeratorDay,
    int Days,
    IReadOnlyDictionary<string, ModeratorBaseline> ByModerator);

/// <summary>
/// Rebuilds <c>modbot_moderator_baseline</c> from the daily totals.
/// </summary>
/// <remarks>
/// From the daily totals rather than from facts because the per-moderator, per-kind, per-day
/// counts already exist there (spec 5.4) and a 90-day baseline for a team of twenty is a few
/// hundred rows. The window ends yesterday so that today's burst -- the thing being judged --
/// cannot raise the usual it is judged against.
/// </remarks>
public static class ModeratorBaselines
{
    public static async Task<TeamBaseline> RecomputeAsync(
        ModbotContext db,
        DateTimeOffset now,
        ReviewThresholds thresholds,
        CancellationToken ct)
    {
        var today = ReviewSql.DayOf(now);
        var from = today.AddDays(-thresholds.BaselineDays);

        const string Sql = """
            SELECT dimension, SUM(value), COUNT(DISTINCT day)::int, MIN(day), MAX(day)
            FROM modbot_daily_total
            WHERE metric = ANY(@metrics) AND dimension <> '' AND day >= @from AND day < @today
            GROUP BY dimension
            """;

        var rows = await ReviewSql.ReadAsync(
            db,
            Sql,
            r => (
                Dimension: r.GetString(0),
                Actions: r.GetDecimal(1),
                ActiveDays: r.GetInt32(2),
                First: DateOnly.FromDateTime(r.GetDateTime(3)),
                Last: DateOnly.FromDateTime(r.GetDateTime(4))),
            ct,
            ("metrics", ActionsOnPeople.BaselineMetrics),
            ("from", from),
            ("today", today));

        const string TeamDaysSql = """
            SELECT COUNT(DISTINCT day)::int
            FROM modbot_daily_total
            WHERE metric = ANY(@metrics) AND dimension <> '' AND day >= @from AND day < @today
            """;

        var teamDays = (await ReviewSql.ReadAsync(db, TeamDaysSql, r => r.GetInt32(0), ct,
            ("metrics", ActionsOnPeople.BaselineMetrics),
            ("from", from),
            ("today", today))).FirstOrDefault();

        await db.ModeratorBaselines.ExecuteDeleteAsync(ct);

        var baselines = new Dictionary<string, ModeratorBaseline>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            // The dimension is "platform:id" (DailyTotalDimensions). Split on the first colon
            // only; the id half is opaque and may contain anything (spec 3.1.1).
            var colon = row.Dimension.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
                continue;

            if (!Enum.TryParse<FactPlatform>(row.Dimension[..colon], ignoreCase: true, out var platform))
                continue;

            var id = row.Dimension[(colon + 1)..];
            if (id.Length == 0 || row.ActiveDays == 0)
                continue;

            var baseline = new ModeratorBaseline
            {
                Platform = platform,
                ModeratorId = id,
                Actions = row.Actions,
                ActiveDays = row.ActiveDays,
                ActionsPerActiveDay = Math.Round(row.Actions / row.ActiveDays, 2),
                FirstDay = row.First,
                LastDay = row.Last,
                ComputedAt = now,
            };

            db.ModeratorBaselines.Add(baseline);

            if (platform == FactPlatform.VRChat)
                baselines[id] = baseline;
        }

        await db.SaveChangesAsync(ct);

        // Detached after saving so a later run in the same context re-adds fresh rows rather than
        // tripping over tracked ones the ExecuteDelete above did not know about.
        foreach (var entry in db.ChangeTracker.Entries<ModeratorBaseline>().ToList())
            entry.State = EntityState.Detached;

        var totalActions = baselines.Values.Sum(b => b.Actions);
        var totalDays = baselines.Values.Sum(b => b.ActiveDays);

        return new TeamBaseline(
            totalDays == 0 ? 0 : Math.Round(totalActions / totalDays, 2),
            teamDays,
            baselines);
    }
}
