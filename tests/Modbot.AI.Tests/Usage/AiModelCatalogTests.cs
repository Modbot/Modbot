using System.Text;
using Modbot.AI.Usage;

namespace Modbot.AI.Tests.Usage;

/// <summary>
/// The model picker's list: what is read from OpenRouter's model list, which models are recommended
/// for a feature, and what a thousand calls would cost (AI chat design §10.9).
/// </summary>
public class AiModelCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Cut down from the live list in September 2026, with the awkward cases kept.</summary>
    private const string ModelList = """
        {
          "data": [
            {
              "id": "anthropic/claude-sonnet-5",
              "name": "Anthropic: Claude Sonnet 5",
              "created": 1756944000,
              "context_length": 200000,
              "architecture": { "input_modalities": ["text", "image"], "output_modalities": ["text"] },
              "pricing": { "prompt": "0.000002", "completion": "0.00001", "input_cache_read": "0.0000002", "web_search": "0.01" },
              "top_provider": { "context_length": 200000, "max_completion_tokens": 64000, "is_moderated": true },
              "supported_parameters": ["tools", "structured_outputs", "temperature"]
            },
            {
              "id": "~deepseek/deepseek-flash-latest",
              "name": "DeepSeek: DeepSeek Flash Latest",
              "created": 1756944000,
              "top_provider": { "context_length": 128000, "max_completion_tokens": null },
              "pricing": {
                "prompt": "0.00000015", "completion": "0.0000006",
                "overrides": [ { "utc_start": 0, "prompt": "0.0000003" } ]
              },
              "supported_parameters": ["tools", "structured_outputs"]
            },
            {
              "id": "openrouter/auto",
              "name": "Auto Router",
              "created": 1756944000,
              "pricing": { "prompt": "-1", "completion": "-1" },
              "supported_parameters": ["tools", "structured_outputs"]
            },
            { "id": "old/model", "created": 1600000000, "pricing": { "prompt": "0.0000001", "completion": "0.0000001" },
              "supported_parameters": ["tools", "structured_outputs"] },
            { "id": "chatty/model", "created": 1756944000, "pricing": { "prompt": "0.000001", "completion": "0.000001" },
              "supported_parameters": ["tools"] },
            { "id": "no-pricing/model", "created": 1756944000, "supported_parameters": ["tools", "structured_outputs"] },
            { "id": "bare-model" }
          ]
        }
        """;

    private static IReadOnlyList<OpenRouterModel> Models() => OpenRouterPrices.ReadModels(Encoding.UTF8.GetBytes(ModelList));

    // ── Reading the list ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryModelIsRead_WhetherOrNotItHasAPrice()
    {
        Assert.Equal(
            ["anthropic/claude-sonnet-5", "~deepseek/deepseek-flash-latest", "openrouter/auto", "old/model", "chatty/model", "no-pricing/model", "bare-model"],
            Models().Select(m => m.Id));
    }

    [Fact]
    public void AModelsDetails_AreReadAsListed()
    {
        var model = Models().First();

        Assert.Equal("Anthropic: Claude Sonnet 5", model.Name);
        Assert.Equal(200000, model.ContextLength);
        Assert.Equal(64000, model.MaxOutputTokens);
        Assert.Equal(["text", "image"], model.InputModalities);
        Assert.Equal(["text"], model.OutputModalities);
        Assert.Contains("structured_outputs", model.SupportedParameters);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1756944000), model.AddedAt);
        Assert.Equal(new OpenRouterPrice("anthropic/claude-sonnet-5", 2m, 0.2m, 10m), model.Price);
        Assert.False(model.PriceVaries);

        // Every price field is kept as listed, per unit, so nothing is lost.
        Assert.Equal(0.000002m, model.Prices["prompt"]);
        Assert.Equal(0.01m, model.Prices["web_search"]);
        Assert.DoesNotContain("overrides", model.Prices.Keys);
    }

    [Fact]
    public void ARouterPricedMinusOne_HasNoPrice_AndSaysItVaries()
    {
        var router = Models().Single(m => m.Id == "openrouter/auto");

        Assert.Null(router.Price);
        Assert.True(router.PriceVaries);
        Assert.Equal(-1m, router.Prices["prompt"]);
    }

    [Fact]
    public void MissingFields_AreLeftEmpty_NotGuessed()
    {
        var bare = Models().Single(m => m.Id == "bare-model");

        Assert.Null(bare.Name);
        Assert.Null(bare.ContextLength);
        Assert.Null(bare.MaxOutputTokens);
        Assert.Null(bare.AddedAt);
        Assert.Null(bare.Price);
        Assert.False(bare.PriceVaries);
        Assert.Empty(bare.InputModalities);
        Assert.Empty(bare.SupportedParameters);
        Assert.Empty(bare.Prices);

        // A null max_completion_tokens is not a number, and the context length falls back to the provider's.
        var flash = Models().Single(m => m.Id == "~deepseek/deepseek-flash-latest");
        Assert.Null(flash.MaxOutputTokens);
        Assert.Equal(128000, flash.ContextLength);
        Assert.Null(flash.Price!.CachedInputPerMillion);
    }

    [Fact]
    public void TheMakerIsThePartBeforeTheSlash_WithoutOpenRoutersAliasMark()
    {
        Assert.Equal("anthropic", AiModelCatalog.MakerOf("anthropic/claude-sonnet-5"));
        Assert.Equal("deepseek", AiModelCatalog.MakerOf("~deepseek/deepseek-flash-latest"));
        Assert.Equal(string.Empty, AiModelCatalog.MakerOf("gpt-5"));
    }

    // ── Recommended first ────────────────────────────────────────────────────────────────────

    private static AiCatalogEntry Entry(
        string id,
        decimal? input = 1m,
        decimal? output = 1m,
        bool varies = false,
        int monthsOld = 1,
        params string[] parameters) =>
        new(
            id,
            id,
            AiModelCatalog.MakerOf(id),
            100000,
            8000,
            ["text"],
            ["text"],
            parameters.Length == 0 ? ["tools", "structured_outputs"] : parameters,
            Now.AddMonths(-monthsOld),
            input is null || output is null ? null : new AiPrice(id, input.Value, null, output.Value, AiPriceSources.OpenRouter, Now),
            varies);

    [Fact]
    public void AModelIsRecommended_WhenItDoesWhatTheFeatureNeeds_IsPriced_AndIsNotOld()
    {
        Assert.True(AiModelCatalog.IsRecommended(Entry("a/new"), AiModelCatalog.BaseFeature, Now));

        // Older than eighteen months.
        Assert.False(AiModelCatalog.IsRecommended(Entry("a/old", monthsOld: 19), AiModelCatalog.BaseFeature, Now));

        // A router, whose price depends on the model it picks.
        Assert.False(AiModelCatalog.IsRecommended(Entry("a/router", varies: true), AiModelCatalog.BaseFeature, Now));

        // No price at all.
        Assert.False(AiModelCatalog.IsRecommended(Entry("a/unpriced", input: null, output: null), AiModelCatalog.BaseFeature, Now));

        // Chat needs tools; Moderation needs structured output; Insights asks for plain text and
        // so needs neither; Base may end up running any of them and is held to both.
        Assert.False(AiModelCatalog.IsRecommended(Entry("a/no-tools", parameters: ["structured_outputs"]), AiFeatures.Chat, Now));
        Assert.True(AiModelCatalog.IsRecommended(Entry("a/no-tools", parameters: ["structured_outputs"]), AiFeatures.Moderation, Now));
        Assert.False(AiModelCatalog.IsRecommended(Entry("a/no-shape", parameters: ["tools"]), AiFeatures.Moderation, Now));
        Assert.True(AiModelCatalog.IsRecommended(Entry("a/no-shape", parameters: ["tools"]), AiFeatures.Chat, Now));

        Assert.True(AiModelCatalog.IsRecommended(Entry("a/plain", parameters: ["temperature"]), AiFeatures.Insights, Now));
        Assert.False(AiModelCatalog.IsRecommended(Entry("a/plain", parameters: ["temperature"]), AiModelCatalog.BaseFeature, Now));
        Assert.Empty(AiModelCatalog.NeedsOf(AiFeatures.Insights));
    }

    [Fact]
    public void WhatAFeatureCannotDo_IsNamed_SoTheRowCanSayIt()
    {
        Assert.Equal(["tools"], AiModelCatalog.MissingFor(Entry("a/no-tools", parameters: ["structured_outputs"]), AiFeatures.Chat));
        Assert.Equal(["structuredOutput"], AiModelCatalog.MissingFor(Entry("a/no-shape", parameters: ["tools"]), AiFeatures.Moderation));
        Assert.Empty(AiModelCatalog.MissingFor(Entry("a/both"), AiModelCatalog.BaseFeature));

        // Insights asks for plain text, so nothing is ever missing for it.
        Assert.Empty(AiModelCatalog.MissingFor(Entry("a/plain", parameters: ["temperature"]), AiFeatures.Insights));
    }

    [Fact]
    public void RecommendedModelsComeFirst_CheapestFirst_AndUnpricedOnesLast()
    {
        var models = new[]
        {
            Entry("a/dear", input: 10m, output: 10m),
            Entry("a/unpriced", input: null, output: null),
            Entry("a/old", input: 0.1m, output: 0.1m, monthsOld: 24),
            Entry("a/cheap", input: 1m, output: 1m),
            Entry("a/router", varies: true),
        };

        var order = AiModelCatalog.Order(models, AiModelCatalog.BaseFeature, Now).Select(m => m.Model.Id);

        // The two recommended ones first, cheapest first; then the rest by price, with the one
        // that has no price at all last.
        Assert.Equal(["a/cheap", "a/dear", "a/old", "a/router", "a/unpriced"], order);
    }

    [Fact]
    public void WithUsageToWorkFrom_CheapestMeansCheapestForThisDeployment()
    {
        // A deployment whose calls are almost all input: the model with the cheap input wins, even
        // though its output costs more.
        var average = new AiTokenAverage(Calls: 10, InputTokens: 100_000, CachedInputTokens: 0, OutputTokens: 100);

        var models = new[] { Entry("a/cheap-output", input: 10m, output: 1m), Entry("a/cheap-input", input: 1m, output: 10m) };

        Assert.Equal(["a/cheap-input", "a/cheap-output"], AiModelCatalog.Order(models, AiModelCatalog.BaseFeature, Now, average).Select(m => m.Model.Id));
    }

    // ── What a thousand calls would cost ─────────────────────────────────────────────────────

    [Fact]
    public void TheCostOfAThousandCalls_IsTheAverageCall_PricedTheWaySpendIs()
    {
        // Ten calls of 10,000 input (2,000 of them cached) and 1,000 output tokens each.
        var average = new AiTokenAverage(Calls: 10, InputTokens: 100_000, CachedInputTokens: 20_000, OutputTokens: 10_000);
        var price = new AiPrice("m", 2m, 0.5m, 10m, AiPriceSources.Entered, Now);

        // One call: 8,000 x $2 + 2,000 x $0.50 + 1,000 x $10 per million = $0.027. A thousand: $27.
        Assert.Equal(27m, AiModelCatalog.CostPerThousandCalls(price, average));
    }

    [Fact]
    public void WithNoPriceOrNoUsage_ThereIsNoCost_ForTheColumnToHide()
    {
        var average = new AiTokenAverage(10, 1000, 0, 100);
        var price = new AiPrice("m", 1m, null, 1m, AiPriceSources.OpenRouter, Now);

        Assert.Null(AiModelCatalog.CostPerThousandCalls(null, average));
        Assert.Null(AiModelCatalog.CostPerThousandCalls(price, AiTokenAverage.None));
        Assert.Null(AiModelCatalog.CostPerThousandCalls(price, null));
        Assert.False(AiTokenAverage.None.HasHistory);
    }

    [Fact]
    public void TheAverageCall_IsTheSumsOverTheCalls()
    {
        var average = new AiTokenAverage(Calls: 4, InputTokens: 1000, CachedInputTokens: 200, OutputTokens: 50);

        Assert.Equal(250m, average.InputPerCall);
        Assert.Equal(50m, average.CachedInputPerCall);
        Assert.Equal(12.5m, average.OutputPerCall);
    }
}
