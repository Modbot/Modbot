using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.AI.Usage;

/// <summary>One feature's spend over the stretches the limits page shows, with its month-end estimate.</summary>
/// <param name="Week">Since Monday, UTC.</param>
/// <param name="LastSevenDays">The seven whole days before today, which the estimate averages.</param>
public sealed record AiFeatureSpend(
    string Feature,
    AiSpent Today,
    AiSpent Week,
    AiSpent Month,
    AiSpent LastMonth,
    AiSpent LastSevenDays,
    AiSpent Estimate);

/// <summary>One feature's spend on one UTC day.</summary>
public sealed record AiDaySpend(DateOnly Day, string Feature, AiSpent Spent);

/// <summary>Spend by feature, in total, and by day.</summary>
/// <param name="Days">The last <see cref="AiSpendReport.DaysShown"/> days, today included, only days and features with usage.</param>
public sealed record AiSpendSummary(
    DateTimeOffset Now,
    DateOnly FirstDay,
    DateOnly LastDay,
    IReadOnlyList<AiFeatureSpend> Features,
    AiFeatureSpend Total,
    IReadOnlyList<AiDaySpend> Days)
{
    public AiFeatureSpend For(string feature) =>
        Features.FirstOrDefault(f => f.Feature == feature)
        ?? new AiFeatureSpend(feature, AiSpent.None, AiSpent.None, AiSpent.None, AiSpent.None, AiSpent.None, AiSpent.None);
}

/// <summary>A limit for everyone or for a feature that is close to being reached, or reached.</summary>
/// <param name="AppliesTo"><c>everyone</c> or <c>feature</c>.</param>
/// <param name="Period"><c>day</c> or <c>month</c>.</param>
/// <param name="Unit"><see cref="AiLimitUnits.Money"/> or <see cref="AiLimitUnits.Tokens"/>.</param>
/// <param name="Estimate">The month-end estimate, for a monthly limit.</param>
/// <param name="Reached">Spent is at or over the limit. Otherwise it is at <see cref="AiSpendReport.WarnAt"/> of it, or the estimate is over it.</param>
/// <param name="PartUnknown">Some of the spend is of a model with no price, so the real figure is higher.</param>
public sealed record AiSpendWarning(
    string AppliesTo,
    string? Feature,
    string Period,
    string Unit,
    decimal Limit,
    decimal Spent,
    decimal? Estimate,
    bool Reached,
    bool PartUnknown);

/// <summary>One account's spend.</summary>
public sealed record AiUserSpend(Guid UserId, string? Username, AiSpent Spent);

/// <summary>The month-end estimate (AI chat design §10.6).</summary>
public static class AiEstimates
{
    /// <summary>How many whole days before today the daily average is taken over.</summary>
    public const int DaysAveraged = 7;

    /// <summary>
    /// Spend so far this month, plus the average day of the last seven whole days times the days left
    /// after today.
    /// </summary>
    /// <remarks>
    /// Deliberately plain, so anybody can check it by hand. The seven days may reach back into last
    /// month, which is what makes the estimate usable on the first of the month. Tokens with no price
    /// are estimated the same way, so an estimate built on unknown spend says so.
    /// </remarks>
    public static AiSpent MonthEnd(AiSpent monthSoFar, AiSpent lastSevenDays, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(monthSoFar);
        ArgumentNullException.ThrowIfNull(lastSevenDays);

        var daysLeft = AiPeriods.DaysInMonth(now) - now.ToUniversalTime().Day;
        var perDay = 1m / DaysAveraged * daysLeft;

        long More(long sevenDays) => (long)Math.Round(sevenDays * perDay, MidpointRounding.AwayFromZero);

        return new AiSpent(
            Math.Round(monthSoFar.Cost + lastSevenDays.Cost * perDay, 6),
            monthSoFar.InputTokens + More(lastSevenDays.InputTokens),
            monthSoFar.CachedInputTokens + More(lastSevenDays.CachedInputTokens),
            monthSoFar.OutputTokens + More(lastSevenDays.OutputTokens),
            monthSoFar.UnpricedTokens + More(lastSevenDays.UnpricedTokens));
    }
}

/// <summary>What AI has cost by feature and by day, estimates, and warnings about limits.</summary>
public sealed class AiSpendReport
{
    /// <summary>Days in the daily spend chart, today included.</summary>
    public const int DaysShown = 30;

    /// <summary>How far through a limit spend has to be to warn.</summary>
    public const decimal WarnAt = 0.8m;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public AiSpendReport(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    /// <summary>Spend by feature for today, this week, this month and last month, and by day.</summary>
    public async Task<AiSpendSummary> ReadAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var today = AiPeriods.DayOf(now);
        var lastMonth = AiPeriods.MonthOf(now).AddMonths(-1);
        var chartStart = today.AddDays(-(DaysShown - 1));

        return await SummaryAsync(now, lastMonth < chartStart ? lastMonth : chartStart, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every limit for everyone or for a feature that is at <see cref="WarnAt"/> of its amount, whose
    /// month-end estimate is over it, or that is reached.
    /// </summary>
    public async Task<IReadOnlyList<AiSpendWarning>> WarningsAsync(CancellationToken ct)
    {
        var limits = await _db.AiSpendLimits.AsNoTracking()
            .Where(l => l.AppliesTo == AiSpendLimit.Everyone || l.AppliesTo == AiSpendLimit.ForFeature)
            .ToListAsync(ct).ConfigureAwait(false);

        var tokenLimits = await _db.AiFeatureLimits.AsNoTracking()
            .Where(l => l.MonthlyTokenLimit != null)
            .ToListAsync(ct).ConfigureAwait(false);

        if (limits.Count == 0 && tokenLimits.Count == 0)
            return [];

        var now = _clock.UtcNow;
        var from = AiPeriods.DayOf(now).AddDays(-AiEstimates.DaysAveraged);
        var month = AiPeriods.MonthOf(now);

        var summary = await SummaryAsync(now, from < month ? from : month, ct).ConfigureAwait(false);
        return WarningsFor(summary, limits, tokenLimits);
    }

    /// <summary>The warnings for these limits against this spend. Public for the tests.</summary>
    public static IReadOnlyList<AiSpendWarning> WarningsFor(
        AiSpendSummary summary, IEnumerable<AiSpendLimit> limits, IEnumerable<AiFeatureLimit> tokenLimits)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(tokenLimits);

        var warnings = new List<AiSpendWarning>();

        foreach (var limit in limits.OrderBy(l => l.AppliesTo == AiSpendLimit.Everyone ? 0 : 1).ThenBy(l => l.Feature, StringComparer.Ordinal))
        {
            AiFeatureSpend spend;
            if (limit.AppliesTo == AiSpendLimit.Everyone)
                spend = summary.Total;
            else if (limit.AppliesTo == AiSpendLimit.ForFeature && limit.Feature is { } feature)
                spend = summary.For(feature);
            else
                continue;

            if (limit.PerDay is { } perDay)
                Add(limit.AppliesTo, limit.Feature, "day", AiLimitUnits.Money, perDay, spend.Today.Cost, null, spend.Today.PartUnknown);

            if (limit.PerMonth is { } perMonth)
            {
                Add(limit.AppliesTo, limit.Feature, "month", AiLimitUnits.Money, perMonth, spend.Month.Cost, spend.Estimate.Cost,
                    spend.Estimate.PartUnknown);
            }
        }

        foreach (var limit in tokenLimits.Where(l => l.MonthlyTokenLimit is not null).OrderBy(l => l.Feature, StringComparer.Ordinal))
        {
            var spend = summary.For(limit.Feature);
            Add(AiLimitReached.TokenLimit, limit.Feature, "month", AiLimitUnits.Tokens, limit.MonthlyTokenLimit!.Value,
                spend.Month.Tokens, spend.Estimate.Tokens, false);
        }

        return warnings;

        void Add(string appliesTo, string? feature, string period, string unit, decimal limit, decimal spent, decimal? estimate, bool partUnknown)
        {
            var reached = spent >= limit;
            var near = spent >= limit * WarnAt || estimate > limit;

            if (reached || near)
                warnings.Add(new AiSpendWarning(appliesTo, feature, period, unit, limit, spent, estimate, reached, partUnknown));
        }
    }

    /// <summary>The accounts that spent most on Chat this month, most first.</summary>
    public async Task<IReadOnlyList<AiUserSpend>> TopChatUsersAsync(int count, CancellationToken ct)
    {
        var month = AiPeriods.MonthOf(_clock.UtcNow);

        var rows = await _db.AiUsage.AsNoTracking()
            .Where(u => u.Feature == AiFeatures.Chat && u.At >= month && u.UserId != null)
            .GroupBy(u => new { u.UserId, u.Model, Reported = u.ReportedCost != null })
            .Select(g => new
            {
                UserId = g.Key.UserId!.Value,
                Sum = new AiUsageSum(
                    g.Key.Model,
                    g.Key.Reported,
                    g.Sum(u => (long)u.InputTokens),
                    g.Sum(u => (long)u.CachedInputTokens),
                    g.Sum(u => (long)u.OutputTokens),
                    g.Sum(u => u.ReportedCost ?? 0m)),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0)
            return [];

        var prices = await AiSpending.PricesForAsync(_db, rows.Select(r => r.Sum), ct).ConfigureAwait(false);

        var top = rows
            .GroupBy(r => r.UserId)
            .Select(g => (UserId: g.Key, Spent: AiSpent.Sum(g.Select(r => r.Sum.PricedWith(prices)))))
            .OrderByDescending(u => u.Spent.Cost)
            .ThenByDescending(u => u.Spent.Tokens)
            .Take(count)
            .ToList();

        var ids = top.Select(u => u.UserId).ToList();
        var names = await _db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct).ConfigureAwait(false);

        return [.. top.Select(u => new AiUserSpend(u.UserId, names.GetValueOrDefault(u.UserId), u.Spent))];
    }

    private async Task<AiSpendSummary> SummaryAsync(DateTimeOffset now, DateTimeOffset from, CancellationToken ct)
    {
        var today = AiPeriods.DayOf(now);
        var week = AiPeriods.WeekOf(now);
        var month = AiPeriods.MonthOf(now);
        var lastMonth = month.AddMonths(-1);
        var sevenDaysBack = today.AddDays(-AiEstimates.DaysAveraged);
        var chartStart = today.AddDays(-(DaysShown - 1));

        var rows = await _db.AiUsage.AsNoTracking()
            .Where(u => u.At >= from)
            // The day as three numbers: Npgsql reads a DateTimeOffset's parts in UTC, and has no way
            // to hand back a date cut from one.
            .GroupBy(u => new { u.Feature, u.At.Year, u.At.Month, u.At.Day, u.Model, Reported = u.ReportedCost != null })
            .Select(g => new
            {
                g.Key.Feature,
                g.Key.Year,
                g.Key.Month,
                g.Key.Day,
                Sum = new AiUsageSum(
                    g.Key.Model,
                    g.Key.Reported,
                    g.Sum(u => (long)u.InputTokens),
                    g.Sum(u => (long)u.CachedInputTokens),
                    g.Sum(u => (long)u.OutputTokens),
                    g.Sum(u => u.ReportedCost ?? 0m)),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var prices = await AiSpending.PricesForAsync(_db, rows.Select(r => r.Sum), ct).ConfigureAwait(false);

        var priced = rows
            .Select(r => (r.Feature, Day: new DateTimeOffset(r.Year, r.Month, r.Day, 0, 0, 0, TimeSpan.Zero), Spent: r.Sum.PricedWith(prices)))
            .ToList();

        var names = AiFeatures.All
            .Concat(priced.Select(r => r.Feature).Distinct(StringComparer.Ordinal).Where(f => !AiFeatures.All.Contains(f)).Order(StringComparer.Ordinal))
            .ToList();

        AiFeatureSpend Spend(string name, IReadOnlyList<(string Feature, DateTimeOffset Day, AiSpent Spent)> part)
        {
            AiSpent Between(DateTimeOffset start, DateTimeOffset? end) =>
                AiSpent.Sum(part.Where(r => r.Day >= start && (end is null || r.Day < end)).Select(r => r.Spent));

            var thisMonth = Between(month, null);
            var lastSeven = Between(sevenDaysBack, today);

            return new AiFeatureSpend(
                name,
                Between(today, null),
                Between(week, null),
                thisMonth,
                Between(lastMonth, month),
                lastSeven,
                AiEstimates.MonthEnd(thisMonth, lastSeven, now));
        }

        var features = names.Select(n => Spend(n, priced.Where(r => r.Feature == n).ToList())).ToList();
        var total = Spend("total", priced);

        var days = priced
            .Where(r => r.Day >= chartStart)
            .GroupBy(r => (r.Day, r.Feature))
            .Select(g => new AiDaySpend(DateOnly.FromDateTime(g.Key.Day.UtcDateTime), g.Key.Feature, AiSpent.Sum(g.Select(r => r.Spent))))
            .OrderBy(d => d.Day)
            .ThenBy(d => d.Feature, StringComparer.Ordinal)
            .ToList();

        return new AiSpendSummary(
            now,
            DateOnly.FromDateTime(chartStart.UtcDateTime),
            DateOnly.FromDateTime(today.UtcDateTime),
            features,
            total,
            days);
    }
}
