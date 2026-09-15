using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Usage;

/// <summary>Where a price came from.</summary>
public static class AiPriceSources
{
    /// <summary>Typed in by the operator. Always wins.</summary>
    public const string Entered = "entered";

    /// <summary>Fetched from OpenRouter's model list.</summary>
    public const string OpenRouter = "openrouter";
}

/// <summary>What one model costs per million tokens, and where that came from.</summary>
/// <param name="CachedInputPerMillion">Null means cached input costs the same as other input.</param>
/// <param name="At">When it was entered or fetched.</param>
public sealed record AiPrice(
    string Model,
    decimal InputPerMillion,
    decimal? CachedInputPerMillion,
    decimal OutputPerMillion,
    string Source,
    DateTimeOffset At);

public static class AiPrices
{
    private const decimal Million = 1_000_000m;

    /// <summary>What some tokens of one model cost, or null when there is no price for it.</summary>
    /// <remarks>
    /// Providers count cached tokens inside the input count, so the cached ones are taken out of
    /// the input price and charged at the cached price instead -- or at the input price when there
    /// is no cached price.
    /// </remarks>
    public static decimal? CostOf(AiPrice? price, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        if (price is null)
            return null;

        var cached = Math.Clamp(cachedInputTokens, 0, Math.Max(0, inputTokens));

        return ((inputTokens - cached) * price.InputPerMillion
                + cached * (price.CachedInputPerMillion ?? price.InputPerMillion)
                + outputTokens * price.OutputPerMillion) / Million;
    }

    /// <summary>
    /// The price of each of <paramref name="models"/> that has one: the operator's where they entered
    /// one, otherwise OpenRouter's. A model with neither is left out.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, AiPrice>> ForAsync(
        ModbotContext db, IReadOnlyCollection<string> models, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(models);

        var prices = new Dictionary<string, AiPrice>(StringComparer.Ordinal);
        if (models.Count == 0)
            return prices;

        var fetched = await db.AiFetchedPrices.AsNoTracking()
            .Where(p => models.Contains(p.Model))
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var p in fetched)
            prices[p.Model] = new AiPrice(p.Model, p.InputPerMillion, p.CachedInputPerMillion, p.OutputPerMillion, AiPriceSources.OpenRouter, p.FetchedAt);

        var entered = await db.AiModelPrices.AsNoTracking()
            .Where(p => models.Contains(p.Model))
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var p in entered)
            prices[p.Model] = new AiPrice(p.Model, p.InputPerMillion, p.CachedInputPerMillion, p.OutputPerMillion, AiPriceSources.Entered, p.UpdatedAt);

        return prices;
    }
}
