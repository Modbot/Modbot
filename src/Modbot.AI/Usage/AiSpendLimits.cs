using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.AI.Usage;

/// <summary>Money and tokens spent over one stretch of time.</summary>
/// <param name="Cost">Priced from <c>ai_model_price</c>; tokens of a model with no price add nothing.</param>
public sealed record AiSpent(decimal Cost, long InputTokens, long CachedInputTokens, long OutputTokens)
{
    public static AiSpent None { get; } = new(0m, 0, 0, 0);
}

/// <summary>A spend limit somebody has reached.</summary>
/// <param name="Period"><c>day</c> or <c>month</c>.</param>
/// <param name="Message">The short sentence to show, naming the limit.</param>
public sealed record AiLimitReached(string AppliesTo, string? Name, string Period, decimal Limit, decimal Spent, string Message);

public static class AiPrices
{
    private const decimal Million = 1_000_000m;

    /// <summary>
    /// What some tokens of one model cost, or null when no price is saved for it.
    /// </summary>
    /// <remarks>
    /// Providers count cached tokens inside the input count, so the cached ones are taken out of
    /// the input price and charged at the cached price instead -- or at the input price when no
    /// cached price was entered.
    /// </remarks>
    public static decimal? CostOf(AiModelPrice? price, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        if (price is null)
            return null;

        var cached = Math.Clamp(cachedInputTokens, 0, Math.Max(0, inputTokens));

        return ((inputTokens - cached) * price.InputPerMillion
                + cached * (price.CachedInputPerMillion ?? price.InputPerMillion)
                + outputTokens * price.OutputPerMillion) / Million;
    }
}

/// <summary>
/// Whether a person may start another AI call, by the spend limits in money that apply to them
/// (AI chat design §10).
/// </summary>
/// <remarks>
/// <para>
/// A person is stopped by the tightest limit that applies: one set on them, one set on any role
/// they hold, or the one for everyone. A limit on a person or a role is compared with that
/// person's own spend; the limit for everyone with everyone's.
/// </para>
/// <para>
/// <see cref="ModbotPermissions.UseAiPastLimits"/> takes the person and role limits away and
/// leaves the one for everyone, which is the operator's ceiling on the bill.
/// </para>
/// <para>
/// Spend is <c>ai_usage</c>'s token counts priced with today's <c>ai_model_price</c>, so a price
/// entered today also prices what was used earlier this month. Days and months are UTC, from
/// <see cref="IModbotClock"/>. The check runs before a call, so a call that starts just under a
/// limit can finish a little over it; the next one is refused.
/// </para>
/// </remarks>
public sealed class AiSpendLimits
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public AiSpendLimits(ModbotContext db, IModbotClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>The start of today and of this month, in UTC.</summary>
    public static (DateTimeOffset Day, DateTimeOffset Month) PeriodsAt(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return (new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>The limit this person has reached, or null when they may go on.</summary>
    public async Task<AiLimitReached?> CheckAsync(Guid userId, ModbotPermissions held, CancellationToken ct)
    {
        var limits = await _db.AiSpendLimits.AsNoTracking()
            .Select(l => new { l.AppliesTo, l.RoleId, l.UserId, l.PerDay, l.PerMonth, RoleName = l.RoleRow == null ? null : l.RoleRow.Name })
            .ToListAsync(ct);

        if (limits.Count == 0)
            return null;

        var pastPersonalLimits = held.HasFlag(ModbotPermissions.Administrator) || held.HasFlag(ModbotPermissions.UseAiPastLimits);

        var roles = pastPersonalLimits
            ? []
            : await _db.UserRoles.AsNoTracking().Where(r => r.UserId == userId).Select(r => r.RoleId).ToListAsync(ct);

        var applicable = limits.Where(l => l.AppliesTo switch
        {
            AiSpendLimit.Everyone => true,
            AiSpendLimit.User => !pastPersonalLimits && l.UserId == userId,
            AiSpendLimit.Role => !pastPersonalLimits && l.RoleId is { } role && roles.Contains(role),
            _ => false,
        }).ToList();

        if (applicable.Count == 0)
            return null;

        var everyone = applicable.Any(l => l.AppliesTo == AiSpendLimit.Everyone)
            ? await SpentAsync(null, ct)
            : (AiSpent.None, AiSpent.None);

        var mine = applicable.Any(l => l.AppliesTo != AiSpendLimit.Everyone)
            ? await SpentAsync(userId, ct)
            : (AiSpent.None, AiSpent.None);

        var reached = applicable
            .SelectMany(l =>
            {
                var (today, month) = l.AppliesTo == AiSpendLimit.Everyone ? everyone : mine;
                return new[]
                {
                    (Limit: l, Period: "day", Amount: l.PerDay, Spent: today.Cost),
                    (Limit: l, Period: "month", Amount: l.PerMonth, Spent: month.Cost),
                };
            })
            .Where(c => c.Amount is { } amount && c.Spent >= amount)
            .OrderBy(c => c.Amount)
            .FirstOrDefault();

        if (reached.Limit is null)
            return null;

        var l = reached.Limit;
        var every = reached.Period == "day" ? "daily" : "monthly";

        var message = l.AppliesTo switch
        {
            AiSpendLimit.User => $"Your {every} AI spend limit is reached.",
            AiSpendLimit.Role => $"The {l.RoleName} role's {every} AI spend limit is reached.",
            _ => $"This Modbot's {every} AI spend limit is reached.",
        };

        return new AiLimitReached(l.AppliesTo, l.RoleName, reached.Period, reached.Amount!.Value, reached.Spent, message);
    }

    /// <summary>Spend today and this month, for one account or, with null, for everyone.</summary>
    public async Task<(AiSpent Today, AiSpent Month)> SpentAsync(Guid? userId, CancellationToken ct)
    {
        var (day, month) = PeriodsAt(_clock.UtcNow);

        var query = _db.AiUsage.AsNoTracking().Where(u => u.At >= month);
        if (userId is { } id)
            query = query.Where(u => u.UserId == id);

        // One row per model and part of the month, so each model is priced with its own price.
        var rows = await query
            .GroupBy(u => new { u.Model, Today = u.At >= day })
            .Select(g => new
            {
                g.Key.Model,
                g.Key.Today,
                Input = g.Sum(u => (long)u.InputTokens),
                Cached = g.Sum(u => (long)u.CachedInputTokens),
                Output = g.Sum(u => (long)u.OutputTokens),
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return (AiSpent.None, AiSpent.None);

        var models = rows.Select(r => r.Model).Distinct().ToList();
        var prices = await _db.AiModelPrices.AsNoTracking()
            .Where(p => models.Contains(p.Model))
            .ToDictionaryAsync(p => p.Model, StringComparer.Ordinal, ct);

        AiSpent Sum(IEnumerable<(string Model, long Input, long Cached, long Output)> part) =>
            part.Aggregate(AiSpent.None, (total, r) => new AiSpent(
                total.Cost + (AiPrices.CostOf(prices.GetValueOrDefault(r.Model), r.Input, r.Cached, r.Output) ?? 0m),
                total.InputTokens + r.Input,
                total.CachedInputTokens + r.Cached,
                total.OutputTokens + r.Output));

        var all = rows.Select(r => (r.Model, r.Input, r.Cached, r.Output)).ToList();
        var today = rows.Where(r => r.Today).Select(r => (r.Model, r.Input, r.Cached, r.Output));

        return (Sum(today), Sum(all));
    }
}
