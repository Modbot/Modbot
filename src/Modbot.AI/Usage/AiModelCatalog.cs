using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;

namespace Modbot.AI.Usage;

/// <summary>What a feature needs a model to be able to do.</summary>
public static class AiModelNeeds
{
    /// <summary>Chat: the model must be able to call tools.</summary>
    public const string Tools = "tools";

    /// <summary>Moderation's AI topics: the model must be able to answer in a fixed shape.</summary>
    public const string StructuredOutput = "structuredOutput";
}

/// <summary>One model the picker can offer, with the price in use for it.</summary>
/// <param name="Price">The operator's price where they entered one, otherwise OpenRouter's. Null when there is none.</param>
/// <param name="PriceVaries">A router OpenRouter prices <c>-1</c>, whose price depends on the model it picks.</param>
public sealed record AiCatalogEntry(
    string Id,
    string? Name,
    string Maker,
    int? ContextLength,
    int? MaxOutputTokens,
    IReadOnlyList<string> InputModalities,
    IReadOnlyList<string> OutputModalities,
    IReadOnlyList<string> SupportedParameters,
    DateTimeOffset? AddedAt,
    AiPrice? Price,
    bool PriceVaries)
{
    /// <summary>The model can call tools.</summary>
    public bool Tools => Takes("tools");

    /// <summary>The model can be made to answer in a fixed shape.</summary>
    public bool StructuredOutput => Takes("structured_outputs");

    /// <summary>The model reads pictures.</summary>
    public bool ImagesIn => InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase);

    /// <summary>Input and output both cost nothing.</summary>
    public bool Free => !PriceVaries && Price is { InputPerMillion: 0m, OutputPerMillion: 0m };

    private bool Takes(string parameter) => SupportedParameters.Contains(parameter, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One model on the picker, in the order it is shown, with what this deployment would pay for it.
/// </summary>
/// <param name="Missing">The needs of the feature the picker was opened from that this model does not meet.</param>
/// <param name="CostPerThousandCalls">
/// What a thousand calls would cost at this deployment's own average token counts, or null when
/// there is no usage to work from and when the model has no price.
/// </param>
public sealed record AiRankedModel(
    AiCatalogEntry Model,
    bool Recommended,
    IReadOnlyList<string> Missing,
    decimal? CostPerThousandCalls);

/// <summary>
/// What one call of a feature has used on average, added up from <c>ai_usage</c>.
/// </summary>
/// <param name="Calls">How many calls the sums are over. Zero means there is no history.</param>
public sealed record AiTokenAverage(long Calls, long InputTokens, long CachedInputTokens, long OutputTokens)
{
    public static AiTokenAverage None { get; } = new(0, 0, 0, 0);

    public bool HasHistory => Calls > 0;

    public decimal InputPerCall => Per(InputTokens);

    public decimal CachedInputPerCall => Per(CachedInputTokens);

    public decimal OutputPerCall => Per(OutputTokens);

    private decimal Per(long tokens) => Calls == 0 ? 0m : Math.Round((decimal)tokens / Calls, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// OpenRouter's model list as the picker uses it: which models suit a feature, which are
/// recommended, and what a thousand calls would cost (AI chat design §10.9).
/// </summary>
public static class AiModelCatalog
{
    /// <summary>The Base model, which every feature uses unless it has its own.</summary>
    public const string BaseFeature = "base";

    /// <summary>How old a model may be and still be recommended.</summary>
    public const int RecommendedMonths = 18;

    /// <summary>How far back calls are averaged, so the estimate follows the prompts in use now.</summary>
    public static readonly TimeSpan AverageOver = TimeSpan.FromDays(30);

    /// <summary>The features a picker can be opened from.</summary>
    public static IReadOnlyList<string> Features { get; } =
        [BaseFeature, AiFeatures.Moderation, AiFeatures.Insights, AiFeatures.Chat];

    /// <summary>The part of a model id before the <c>/</c>. OpenRouter marks its aliases with a leading <c>~</c>.</summary>
    public static string MakerOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var name = id.TrimStart('~');
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        return slash <= 0 ? string.Empty : name[..slash];
    }

    /// <summary>
    /// What the feature needs a model to do. Insights needs nothing in particular: it asks for
    /// plain text. Base is held to both, because any feature may end up running on it.
    /// </summary>
    public static IReadOnlyList<string> NeedsOf(string? feature) => feature switch
    {
        AiFeatures.Chat => [AiModelNeeds.Tools],
        AiFeatures.Moderation => [AiModelNeeds.StructuredOutput],
        AiFeatures.Insights => [],
        _ => [AiModelNeeds.Tools, AiModelNeeds.StructuredOutput],
    };

    /// <summary>The needs of the feature this model does not meet.</summary>
    public static IReadOnlyList<string> MissingFor(AiCatalogEntry model, string? feature)
    {
        ArgumentNullException.ThrowIfNull(model);

        return
        [
            .. NeedsOf(feature).Where(need => need switch
            {
                AiModelNeeds.Tools => !model.Tools,
                AiModelNeeds.StructuredOutput => !model.StructuredOutput,
                _ => false,
            }),
        ];
    }

    /// <summary>
    /// Whether the model is recommended for the feature: it does what the feature needs, it has a
    /// price that does not vary, and it is less than eighteen months old.
    /// </summary>
    public static bool IsRecommended(AiCatalogEntry model, string? feature, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(model);

        return MissingFor(model, feature).Count == 0
            && !model.PriceVaries
            && model.Price is not null
            && model.AddedAt is { } added
            && added >= now.AddMonths(-RecommendedMonths);
    }

    /// <summary>
    /// What a thousand calls of the feature would cost on this model, priced the same way spend is
    /// (<see cref="AiPrices.CostOf"/>) from the feature's own average tokens. Null without either.
    /// </summary>
    public static decimal? CostPerThousandCalls(AiPrice? price, AiTokenAverage? average)
    {
        if (price is null || average is not { Calls: > 0 } used)
            return null;

        var cost = AiPrices.CostOf(price, used.InputTokens, used.CachedInputTokens, used.OutputTokens);
        return cost is null ? null : Math.Round(cost.Value * 1000m / used.Calls, 6, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The models in the order the picker shows them: the ones recommended for the feature first,
    /// cheapest first inside each group, then by name. A model with no price goes last.
    /// </summary>
    /// <remarks>
    /// Cheapest means what a thousand calls would cost where there is usage to work from, and
    /// otherwise the input and output prices added together -- no favourite model is ever named.
    /// </remarks>
    public static IReadOnlyList<AiRankedModel> Order(
        IEnumerable<AiCatalogEntry> models, string? feature, DateTimeOffset now, AiTokenAverage? average = null)
    {
        ArgumentNullException.ThrowIfNull(models);

        return
        [
            .. models
                .Select(m => new AiRankedModel(m, IsRecommended(m, feature, now), MissingFor(m, feature), CostPerThousandCalls(m.Price, average)))
                .OrderByDescending(m => m.Recommended)
                .ThenBy(m => SortPrice(m, average) is null ? 1 : 0)
                .ThenBy(m => SortPrice(m, average) ?? 0m)
                .ThenBy(m => m.Model.Name ?? m.Model.Id, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static decimal? SortPrice(AiRankedModel model, AiTokenAverage? average) =>
        average is { Calls: > 0 }
            ? model.CostPerThousandCalls
            : model.Model.Price is { } price ? price.InputPerMillion + price.OutputPerMillion : null;

    /// <summary>
    /// The models as last fetched, with the price in use for each: the operator's where they
    /// entered one, otherwise OpenRouter's.
    /// </summary>
    public static async Task<IReadOnlyList<AiCatalogEntry>> ReadAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var listed = await db.AiCatalogModels.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        if (listed.Count == 0)
            return [];

        var entered = await db.AiModelPrices.AsNoTracking().ToDictionaryAsync(p => p.Model, StringComparer.Ordinal, ct).ConfigureAwait(false);

        return
        [
            .. listed.Select(m =>
            {
                var price = entered.TryGetValue(m.Model, out var own)
                    ? new AiPrice(m.Model, own.InputPerMillion, own.CachedInputPerMillion, own.OutputPerMillion, AiPriceSources.Entered, own.UpdatedAt)
                    : m.InputPerMillion is { } input && m.OutputPerMillion is { } output
                        ? new AiPrice(m.Model, input, m.CachedInputPerMillion, output, AiPriceSources.OpenRouter, m.FetchedAt)
                        : null;

                return new AiCatalogEntry(
                    m.Model,
                    m.Name,
                    m.Maker,
                    m.ContextLength,
                    m.MaxOutputTokens,
                    m.InputModalities,
                    m.OutputModalities,
                    m.SupportedParameters,
                    m.AddedAt,
                    price,
                    // An entered price is a real price, whatever OpenRouter says.
                    m.PriceVaries && price?.Source != AiPriceSources.Entered);
            }),
        ];
    }

    /// <summary>
    /// What one call of the feature has used on average over the last thirty days. Base averages
    /// every feature together, apart from the Test button, which is one short message and no work.
    /// </summary>
    public static async Task<AiTokenAverage> AverageAsync(ModbotContext db, string? feature, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var since = now - AverageOver;
        var rows = db.AiUsage.AsNoTracking().Where(u => u.At >= since);

        rows = feature is null or BaseFeature
            ? rows.Where(u => u.Feature != AiFeatures.Test)
            : rows.Where(u => u.Feature == feature);

        var used = await rows
            .GroupBy(_ => 1)
            .Select(g => new AiTokenAverage(
                g.LongCount(),
                g.Sum(u => (long)u.InputTokens),
                g.Sum(u => (long)u.CachedInputTokens),
                g.Sum(u => (long)u.OutputTokens)))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return used ?? AiTokenAverage.None;
    }
}
