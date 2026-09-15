using System.ClientModel.Primitives;
using System.Text;
using Modbot.AI.Usage;
using Modbot.Core.Data.Entities;
using OpenAI.Chat;

namespace Modbot.AI.Tests.Usage;

/// <summary>Prices, the estimate and the warnings, worked out without a database (AI chat design §10).</summary>
public class AiPricesTests
{
    private static AiPrice Price(decimal input, decimal? cached, decimal output) =>
        new("m", input, cached, output, AiPriceSources.Entered, DateTimeOffset.UnixEpoch);

    [Fact]
    public void WithNoPrice_TheCostIsUnknown_NotZero()
    {
        Assert.Null(AiPrices.CostOf(null, 1000, 0, 1000));

        var sum = new AiUsageSum("unpriced", Reported: false, 1000, 0, 500, 0m);
        var spent = sum.PricedWith(new Dictionary<string, AiPrice>());

        Assert.Equal(1500, spent.UnpricedTokens);
        Assert.True(spent.PartUnknown);
    }

    [Fact]
    public void CachedInput_IsTakenOutOfTheInputCount_AndChargedAtItsOwnPrice()
    {
        // 3M input of which 1M cached, 0.5M output: 2M x $2 + 1M x $0.50 + 0.5M x $8.
        Assert.Equal(8.5m, AiPrices.CostOf(Price(2m, 0.5m, 8m), 3_000_000, 1_000_000, 500_000));
    }

    [Fact]
    public void WithNoCachedPrice_CachedInputCostsTheSameAsOtherInput()
    {
        Assert.Equal(6m, AiPrices.CostOf(Price(2m, null, 0m), 3_000_000, 1_000_000, 0));
    }

    [Fact]
    public void AReportedCost_IsUsedAsItIs_AndNeverPricedAgain()
    {
        var sum = new AiUsageSum("m", Reported: true, 1_000_000, 0, 0, 0.42m);
        var prices = new Dictionary<string, AiPrice> { ["m"] = Price(100m, null, 100m) };

        var spent = sum.PricedWith(prices);

        Assert.Equal(0.42m, spent.Cost);
        Assert.False(spent.PartUnknown);
    }

    [Theory]
    [InlineData(2026, 9, 15, 23, 59, 2026, 9, 15, 2026, 9, 1)]
    [InlineData(2026, 10, 1, 0, 0, 2026, 10, 1, 2026, 10, 1)]
    public void DaysAndMonthsStartAtMidnightUtc(int y, int mo, int d, int h, int mi, int dy, int dmo, int dd, int my, int mmo, int md)
    {
        var (day, month) = AiSpendLimits.PeriodsAt(new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(dy, dmo, dd, 0, 0, 0, TimeSpan.Zero), day);
        Assert.Equal(new DateTimeOffset(my, mmo, md, 0, 0, 0, TimeSpan.Zero), month);
    }

    [Fact]
    public void AWeekStartsOnMonday()
    {
        // 2029-03-15 is a Thursday; 2029-03-18 a Sunday.
        Assert.Equal(new DateTimeOffset(2029, 3, 12, 0, 0, 0, TimeSpan.Zero), AiPeriods.WeekOf(new DateTimeOffset(2029, 3, 15, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2029, 3, 12, 0, 0, 0, TimeSpan.Zero), AiPeriods.WeekOf(new DateTimeOffset(2029, 3, 18, 23, 0, 0, TimeSpan.Zero)));
    }

    // ── The estimate ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheEstimate_IsSpendSoFar_PlusTheLastSevenDaysAverage_TimesTheDaysLeft()
    {
        // 15 March: 16 days left after today. $14 over seven days is $2 a day, so $32 more.
        var now = new DateTimeOffset(2029, 3, 15, 12, 0, 0, TimeSpan.Zero);

        var estimate = AiEstimates.MonthEnd(new AiSpent(10m, 1000, 0, 100), new AiSpent(14m, 700, 0, 70), now);

        Assert.Equal(42m, estimate.Cost);
        Assert.Equal(1000 + 1600, estimate.InputTokens);
        Assert.Equal(100 + 160, estimate.OutputTokens);
    }

    [Fact]
    public void OnTheLastDayOfTheMonth_TheEstimateIsWhatWasSpent()
    {
        var now = new DateTimeOffset(2029, 2, 28, 23, 0, 0, TimeSpan.Zero);

        Assert.Equal(5m, AiEstimates.MonthEnd(new AiSpent(5m, 0, 0, 0), new AiSpent(700m, 0, 0, 0), now).Cost);
    }

    [Fact]
    public void AnEstimateBuiltOnUnpricedTokens_SaysPartIsUnknown()
    {
        var now = new DateTimeOffset(2029, 3, 15, 12, 0, 0, TimeSpan.Zero);

        var estimate = AiEstimates.MonthEnd(new AiSpent(1m, 0, 0, 0), new AiSpent(0m, 70, 0, 0, UnpricedTokens: 70), now);

        Assert.True(estimate.PartUnknown);
        Assert.Equal(1m, estimate.Cost);
    }

    // ── Warnings ─────────────────────────────────────────────────────────────────────────────

    private static AiSpendSummary Summary(decimal today, decimal month, decimal estimate, string feature = AiFeatures.Moderation)
    {
        var spend = new AiFeatureSpend(
            feature,
            new AiSpent(today, 0, 0, 0),
            AiSpent.None,
            new AiSpent(month, 0, 0, 0),
            AiSpent.None,
            AiSpent.None,
            new AiSpent(estimate, 0, 0, 0));

        return new AiSpendSummary(DateTimeOffset.UnixEpoch, default, default, [spend], spend with { Feature = "total" }, []);
    }

    private static AiSpendLimit FeatureLimit(decimal? perDay = null, decimal? perMonth = null) =>
        new() { AppliesTo = AiSpendLimit.ForFeature, Feature = AiFeatures.Moderation, PerDay = perDay, PerMonth = perMonth };

    [Fact]
    public void UnderEightyPercent_WithTheEstimateUnderTheLimit_NothingIsWarned()
    {
        Assert.Empty(AiSpendReport.WarningsFor(Summary(7.9m, 7.9m, 9.9m), [FeatureLimit(10m, 10m)], []));
    }

    [Fact]
    public void AtEightyPercent_ItWarns_WithoutSayingReached()
    {
        var warning = Assert.Single(AiSpendReport.WarningsFor(Summary(8m, 1m, 1m), [FeatureLimit(perDay: 10m)], []));

        Assert.Equal("day", warning.Period);
        Assert.False(warning.Reached);
        Assert.Equal(8m, warning.Spent);
    }

    [Fact]
    public void AnEstimateOverTheMonthlyLimit_Warns_EvenFarBelowEightyPercent()
    {
        var warning = Assert.Single(AiSpendReport.WarningsFor(Summary(0m, 2m, 10.01m), [FeatureLimit(perMonth: 10m)], []));

        Assert.Equal("month", warning.Period);
        Assert.False(warning.Reached);
        Assert.Equal(10.01m, warning.Estimate);
    }

    [Fact]
    public void AtTheLimit_ItSaysReached()
    {
        var warning = Assert.Single(AiSpendReport.WarningsFor(
            Summary(0m, 10m, 10m, AiFeatures.Insights),
            [new AiSpendLimit { AppliesTo = AiSpendLimit.Everyone, PerMonth = 10m }],
            []));

        Assert.Equal(AiSpendLimit.Everyone, warning.AppliesTo);
        Assert.True(warning.Reached);
    }

    [Fact]
    public void RoleAndUserLimits_AreNotWarnedAbout()
    {
        Assert.Empty(AiSpendReport.WarningsFor(
            Summary(100m, 100m, 100m),
            [new AiSpendLimit { AppliesTo = AiSpendLimit.User, UserId = Guid.NewGuid(), PerDay = 1m }],
            []));
    }

    [Fact]
    public void ATokenLimit_WarnsInTokens()
    {
        var spend = new AiFeatureSpend(AiFeatures.Chat, AiSpent.None, AiSpent.None, new AiSpent(0m, 850, 0, 0), AiSpent.None, AiSpent.None, new AiSpent(0m, 900, 0, 0));
        var summary = new AiSpendSummary(DateTimeOffset.UnixEpoch, default, default, [spend], spend, []);

        var warning = Assert.Single(AiSpendReport.WarningsFor(summary, [], [new AiFeatureLimit { Feature = AiFeatures.Chat, MonthlyTokenLimit = 1000 }]));

        Assert.Equal(AiLimitUnits.Tokens, warning.Unit);
        Assert.Equal(850m, warning.Spent);
    }

    // ── Token limits into money ──────────────────────────────────────────────────────────────

    [Fact]
    public void ATokenLimit_IsPricedAtTheMixTheFeatureUsed()
    {
        // Used 3 input to 1 output; $1 and $5 per million: 4M tokens in that mix cost 3 x $1 + 1 x $5.
        Assert.Equal(8m, AiTokenLimits.PerMonth(Price(1m, null, 5m), 4_000_000, 300, 0, 100));
    }

    [Fact]
    public void ATokenLimitWithNoUsage_IsPricedAsInput()
    {
        Assert.Equal(2m, AiTokenLimits.PerMonth(Price(1m, null, 5m), 2_000_000, 0, 0, 0));
    }

    // ── The cost OpenRouter reports ──────────────────────────────────────────────────────────

    private static ChatTokenUsage UsageWithCost(string cost) =>
        ModelReaderWriter.Read<ChatCompletion>(BinaryData.FromString(
            $$$"""{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"cost":{{{cost}}}}}"""))!.Usage;

    [Fact]
    public void OpenRoutersCost_IsReadFromTheUsage()
    {
        Assert.Equal(0.00123m, AiReportedCost.Of(UsageWithCost("0.00123"), AiProviders.OpenRouter.Id));
    }

    [Fact]
    public void AnotherProvidersCost_IsNotBelieved()
    {
        Assert.Null(AiReportedCost.Of(UsageWithCost("0.00123"), AiProviders.OpenAI.Id));
    }

    [Fact]
    public void OnlyOpenRouter_IsAskedForItsCost()
    {
        var openRouter = new ChatCompletionOptions();
        AiReportedCost.AskFor(openRouter, AiProviders.OpenRouter.Id);

        var openAi = new ChatCompletionOptions();
        AiReportedCost.AskFor(openAi, AiProviders.OpenAI.Id);

        Assert.Contains("\"usage\":{\"include\":true}", ModelReaderWriter.Write(openRouter).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("usage", ModelReaderWriter.Write(openAi).ToString(), StringComparison.Ordinal);
    }

    // ── OpenRouter's model list ──────────────────────────────────────────────────────────────

    /// <summary>Cut down from the live list in September 2026, with the awkward cases kept.</summary>
    public const string ModelList = """
        {
          "data": [
            {
              "id": "anthropic/claude-sonnet-5",
              "name": "Anthropic: Claude Sonnet 5",
              "pricing": { "prompt": "0.000002", "completion": "0.00001", "web_search": "0.01", "input_cache_read": "0.0000002", "input_cache_write": "0.0000025" }
            },
            {
              "id": "~deepseek/deepseek-flash-latest",
              "pricing": {
                "prompt": "0.00000015", "completion": "0.0000006", "input_cache_read": "0.000000015",
                "overrides": [ { "utc_start": 0, "utc_end": 1400, "prompt": "0.0000003", "completion": "0.0000012" } ]
              }
            },
            { "id": "openrouter/auto", "pricing": { "prompt": "-1", "completion": "-1" } },
            { "id": "no-cache/model", "pricing": { "prompt": 0.000001, "completion": "0.000002" } },
            { "id": "free/model", "pricing": { "prompt": "0", "completion": "0" } },
            { "id": "broken/model", "pricing": { "prompt": "abc", "completion": "0.1" } },
            { "id": "no-pricing/model" },
            { "id": "anthropic/claude-sonnet-5", "pricing": { "prompt": "9", "completion": "9" } }
          ]
        }
        """;

    [Fact]
    public void OpenRoutersList_IsReadAsPricesPerMillion()
    {
        var read = OpenRouterPrices.Read(Encoding.UTF8.GetBytes(ModelList));
        var prices = read.ToDictionary(p => p.Model);

        // The first listing of a model wins; routers, broken prices and models without prices are left out.
        Assert.Equal(["anthropic/claude-sonnet-5", "~deepseek/deepseek-flash-latest", "no-cache/model", "free/model"], read.Select(p => p.Model));

        Assert.Equal(new OpenRouterPrice("anthropic/claude-sonnet-5", 2m, 0.2m, 10m), prices["anthropic/claude-sonnet-5"]);
        Assert.Equal(new OpenRouterPrice("~deepseek/deepseek-flash-latest", 0.15m, 0.015m, 0.6m), prices["~deepseek/deepseek-flash-latest"]);
        Assert.Equal(new OpenRouterPrice("no-cache/model", 1m, null, 2m), prices["no-cache/model"]);
        Assert.Equal(new OpenRouterPrice("free/model", 0m, null, 0m), prices["free/model"]);
    }

    [Fact]
    public void SomethingThatIsNotAModelList_GivesNoPrices()
    {
        Assert.Empty(OpenRouterPrices.Read("""{"error":{"message":"nope"}}"""u8));
        Assert.Empty(OpenRouterPrices.Read("[]"u8));
    }
}
