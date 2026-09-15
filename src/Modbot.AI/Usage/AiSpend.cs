using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;

namespace Modbot.AI.Usage;

/// <summary>Money and tokens spent over one stretch of time.</summary>
/// <param name="Cost">
/// What the priced part cost: the provider's reported cost where it sent one, otherwise the tokens
/// priced from the price list.
/// </param>
/// <param name="UnpricedTokens">
/// Input and output tokens of models with no price. Their cost is unknown, and never counted as
/// zero: anything showing <see cref="Cost"/> shows that part of it is unknown.
/// </param>
public sealed record AiSpent(decimal Cost, long InputTokens, long CachedInputTokens, long OutputTokens, long UnpricedTokens = 0)
{
    public static AiSpent None { get; } = new(0m, 0, 0, 0);

    /// <summary>Input plus output tokens, which is what a token limit counts.</summary>
    public long Tokens => InputTokens + OutputTokens;

    public bool PartUnknown => UnpricedTokens > 0;

    public AiSpent Plus(AiSpent other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new AiSpent(
            Cost + other.Cost,
            InputTokens + other.InputTokens,
            CachedInputTokens + other.CachedInputTokens,
            OutputTokens + other.OutputTokens,
            UnpricedTokens + other.UnpricedTokens);
    }

    public static AiSpent Sum(IEnumerable<AiSpent> parts) => parts.Aggregate(None, (total, part) => total.Plus(part));
}

/// <summary>One model's usage added up, before it is priced.</summary>
/// <param name="Reported">Whether these rows carry the provider's own cost.</param>
public sealed record AiUsageSum(string Model, bool Reported, long Input, long Cached, long Output, decimal ReportedCost)
{
    public AiSpent PricedWith(IReadOnlyDictionary<string, AiPrice> prices)
    {
        ArgumentNullException.ThrowIfNull(prices);

        if (Reported)
            return new AiSpent(ReportedCost, Input, Cached, Output);

        return AiPrices.CostOf(prices.GetValueOrDefault(Model), Input, Cached, Output) is { } cost
            ? new AiSpent(cost, Input, Cached, Output)
            : new AiSpent(0m, Input, Cached, Output, Input + Output);
    }
}

/// <summary>The UTC days, weeks and months spend is counted in.</summary>
public static class AiPeriods
{
    public static DateTimeOffset DayOf(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
    }

    public static DateTimeOffset MonthOf(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>The Monday the week starts on.</summary>
    public static DateTimeOffset WeekOf(DateTimeOffset at)
    {
        var day = DayOf(at);
        var sinceMonday = ((int)day.DayOfWeek + 6) % 7;
        return day.AddDays(-sinceMonday);
    }

    public static int DaysInMonth(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return DateTime.DaysInMonth(utc.Year, utc.Month);
    }
}

/// <summary>Reads what AI has cost from <c>ai_usage</c>.</summary>
public static class AiSpending
{
    /// <summary>Spend today and this month, for one feature or all, and one account or everyone.</summary>
    public static async Task<(AiSpent Today, AiSpent Month)> TodayAndMonthAsync(
        ModbotContext db, DateTimeOffset now, string? feature, Guid? userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var day = AiPeriods.DayOf(now);
        var month = AiPeriods.MonthOf(now);

        var query = db.AiUsage.AsNoTracking().Where(u => u.At >= month);
        if (feature is not null)
            query = query.Where(u => u.Feature == feature);
        if (userId is { } id)
            query = query.Where(u => u.UserId == id);

        // One row per model, part of the month and kind of cost, so each model is priced with its
        // own price and a reported cost is never priced again.
        var rows = await query
            .GroupBy(u => new { u.Model, Today = u.At >= day, Reported = u.ReportedCost != null })
            .Select(g => new
            {
                g.Key.Today,
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
            return (AiSpent.None, AiSpent.None);

        var prices = await PricesForAsync(db, rows.Select(r => r.Sum), ct).ConfigureAwait(false);

        var today = AiSpent.Sum(rows.Where(r => r.Today).Select(r => r.Sum.PricedWith(prices)));
        var all = AiSpent.Sum(rows.Select(r => r.Sum.PricedWith(prices)));

        return (today, all);
    }

    /// <summary>Prices for the models of the sums that need one.</summary>
    public static Task<IReadOnlyDictionary<string, AiPrice>> PricesForAsync(
        ModbotContext db, IEnumerable<AiUsageSum> sums, CancellationToken ct)
        => AiPrices.ForAsync(db, sums.Where(s => !s.Reported).Select(s => s.Model).Distinct(StringComparer.Ordinal).ToList(), ct);
}
