using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Serilog;
using Modbot.Core.Logging;

namespace Modbot.AI.Usage;

/// <summary>A token limit that became a money limit.</summary>
/// <param name="PerMonth">The money limit it became, in US dollars.</param>
public sealed record AiTokenLimitChange(string Feature, long Tokens, string Model, decimal PerMonth);

/// <summary>
/// Changes the monthly token limits kept from before prices into monthly money limits
/// (AI chat design §10.5).
/// </summary>
/// <remarks>
/// <para>
/// A token limit counts input and output tokens together, and the two have different prices, so it
/// is priced at the mix of input, cached input and output tokens that feature has actually used,
/// with the price of the model the feature is set to use now. With no usage to take the mix from, it
/// is priced as all uncached input, which is cheaper than output on nearly every model: the money
/// limit it becomes then stops the feature no later than the token limit would have.
/// </para>
/// <para>
/// When the feature already has a monthly money limit, the lower of the two is kept. A token limit
/// whose model has no price is left as it is, and changed on a later pass once it has one.
/// </para>
/// </remarks>
public sealed class AiTokenLimits
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Setup);

    public AiTokenLimits(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    public async Task<IReadOnlyList<AiTokenLimitChange>> ChangeToMoneyAsync(CancellationToken ct)
    {
        var tokenLimits = await _db.AiFeatureLimits.ToListAsync(ct).ConfigureAwait(false);
        if (tokenLimits.Count == 0)
            return [];

        // A row with no amount limits nothing; it goes whatever else happens.
        _db.AiFeatureLimits.RemoveRange(tokenLimits.Where(l => l.MonthlyTokenLimit is null));

        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.AiModel, s.AiChatModel })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var insightModel = await _db.InsightSettings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.Model)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        string? ModelOf(string feature)
        {
            var chosen = feature switch
            {
                AiFeatures.Chat => settings?.AiChatModel,
                AiFeatures.Insights => insightModel,
                _ => null,
            };

            var model = string.IsNullOrWhiteSpace(chosen) ? settings?.AiModel : chosen;
            return string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        }

        var withModels = tokenLimits
            .Where(l => l.MonthlyTokenLimit is not null)
            .Select(l => (Limit: l, Model: ModelOf(l.Feature)))
            .Where(l => l.Model is not null)
            .ToList();

        var prices = await AiPrices.ForAsync(_db, withModels.Select(l => l.Model!).Distinct(StringComparer.Ordinal).ToList(), ct)
            .ConfigureAwait(false);

        var changes = new List<AiTokenLimitChange>();
        var now = _clock.UtcNow;

        foreach (var (limit, model) in withModels)
        {
            if (!prices.TryGetValue(model!, out var price))
                continue;

            var mix = await _db.AiUsage.AsNoTracking()
                .Where(u => u.Feature == limit.Feature)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Input = g.Sum(u => (long)u.InputTokens),
                    Cached = g.Sum(u => (long)u.CachedInputTokens),
                    Output = g.Sum(u => (long)u.OutputTokens),
                })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            var tokens = limit.MonthlyTokenLimit!.Value;
            var perMonth = Math.Round(PerMonth(price, tokens, mix?.Input ?? 0, mix?.Cached ?? 0, mix?.Output ?? 0), 6, MidpointRounding.ToZero);

            var existing = await _db.AiSpendLimits
                .FirstOrDefaultAsync(l => l.AppliesTo == AiSpendLimit.ForFeature && l.Feature == limit.Feature, ct)
                .ConfigureAwait(false);

            if (existing is null)
            {
                _db.AiSpendLimits.Add(new AiSpendLimit
                {
                    AppliesTo = AiSpendLimit.ForFeature,
                    Feature = limit.Feature,
                    PerMonth = perMonth,
                    UpdatedAt = now,
                });
            }
            else if (existing.PerMonth is null || perMonth < existing.PerMonth)
            {
                existing.PerMonth = perMonth;
                existing.UpdatedAt = now;
            }

            _db.AiFeatureLimits.Remove(limit);
            changes.Add(new AiTokenLimitChange(limit.Feature, tokens, model!, perMonth));
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var change in changes)
        {
            _log.Information(
                "Changed the {Feature} limit of {Tokens} tokens a month into {PerMonth} US dollars a month at the price of {Model}",
                change.Feature, change.Tokens, change.PerMonth, change.Model);
        }

        return changes;
    }

    /// <summary>What <paramref name="tokens"/> tokens cost in the given mix. Public for the tests.</summary>
    public static decimal PerMonth(AiPrice price, long tokens, long usedInput, long usedCached, long usedOutput)
    {
        ArgumentNullException.ThrowIfNull(price);

        var used = usedInput + usedOutput;
        if (used <= 0)
            return tokens * price.InputPerMillion / 1_000_000m;

        return AiPrices.CostOf(price, usedInput, usedCached, usedOutput)!.Value / used * tokens;
    }
}
