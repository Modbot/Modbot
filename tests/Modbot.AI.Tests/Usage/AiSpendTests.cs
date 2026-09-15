using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI.Insights;
using Modbot.AI.Tests.Insights;
using Modbot.AI.Usage;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.TestSupport;

namespace Modbot.AI.Tests.Usage;

/// <summary>
/// Spend limits for every feature, prices from both places, estimates, warnings and the change from
/// token limits, against a real database (AI chat design §10).
/// </summary>
/// <remarks>The clock starts on Thursday 15 March 2029 at noon, with 16 days of the month left after today.</remarks>
[Collection(nameof(PostgresCollection))]
public class AiSpendTests : InsightTestBase
{
    private const string Priced = "priced-model";

    public AiSpendTests(PostgresFixture fixture) : base(fixture) { }

    // ── Each feature stops at its own limit and at the one for everyone ──────────────────────

    [Theory]
    [InlineData(AiFeatures.Moderation)]
    [InlineData(AiFeatures.Insights)]
    [InlineData(AiFeatures.Chat)]
    [InlineData(AiFeatures.Test)]
    public async Task EachFeature_StopsAtItsOwnLimit_AndNoOtherFeatureDoes(string feature)
    {
        await PriceAsync(Priced, 1m, 1m);
        await SpendAsync(feature, 2_000_000);
        await LimitAsync(new AiSpendLimit { AppliesTo = AiSpendLimit.ForFeature, Feature = feature, PerDay = 2m });

        await using var context = NewContext();
        var reached = await NewUsage(context).LimitReachedAsync(feature, Ct);

        Assert.NotNull(reached);
        Assert.Equal($"The daily AI spend limit for {AiFeatures.LabelOf(feature)} is reached.", reached.Message);

        foreach (var other in AiFeatures.All.Where(f => f != feature))
            Assert.Null(await NewUsage(context).LimitReachedAsync(other, Ct));
    }

    [Theory]
    [InlineData(AiFeatures.Moderation)]
    [InlineData(AiFeatures.Insights)]
    [InlineData(AiFeatures.Chat)]
    [InlineData(AiFeatures.Test)]
    public async Task EveryFeature_StopsAtTheLimitForEveryone_WhicheverFeatureSpent(string feature)
    {
        await PriceAsync(Priced, 1m, 1m);
        await SpendAsync(AiFeatures.Moderation, 600_000);
        await SpendAsync(AiFeatures.Chat, 400_000);
        await LimitAsync(new AiSpendLimit { AppliesTo = AiSpendLimit.Everyone, PerMonth = 1m });

        await using var context = NewContext();
        var reached = await NewUsage(context).LimitReachedAsync(feature, Ct);

        Assert.Equal("This Modbot's monthly AI spend limit is reached.", reached?.Message);
        Assert.Equal(1m, reached!.Spent);
    }

    [Fact]
    public async Task UseAiPastLimits_LiftsPersonLimits_ButNotTheFeaturesOrEveryones()
    {
        var user = await UserAsync();
        await PriceAsync(Priced, 1m, 1m);
        await SpendAsync(AiFeatures.Chat, 1_000_000, user.Id);
        await LimitAsync(new AiSpendLimit { AppliesTo = AiSpendLimit.User, UserId = user.Id, PerDay = 0.5m });

        await using (var context = NewContext())
        {
            Assert.Equal("Your daily AI spend limit is reached.",
                (await NewLimits(context).CheckAsync(AiFeatures.Chat, user.Id, ModbotPermissions.UseAiChat, Ct))?.Message);
            Assert.Null(await NewLimits(context).CheckAsync(AiFeatures.Chat, user.Id, ModbotPermissions.UseAiPastLimits, Ct));
        }

        await LimitAsync(new AiSpendLimit { AppliesTo = AiSpendLimit.ForFeature, Feature = AiFeatures.Chat, PerDay = 0.75m });

        await using (var context = NewContext())
        {
            Assert.Equal("The daily AI spend limit for Chat is reached.",
                (await NewLimits(context).CheckAsync(AiFeatures.Chat, user.Id, ModbotPermissions.UseAiPastLimits, Ct))?.Message);
        }
    }

    [Fact]
    public async Task APersonsLimit_IsChats_AndDoesNotStopOtherFeatures()
    {
        var user = await UserAsync();
        await PriceAsync(Priced, 1m, 1m);
        await SpendAsync(AiFeatures.Insights, 1_000_000, user.Id);
        await LimitAsync(new AiSpendLimit { AppliesTo = AiSpendLimit.User, UserId = user.Id, PerDay = 0.5m });

        await using var context = NewContext();

        // Their insights spend is not Chat spend, and a limit on them does not stop insights.
        Assert.Null(await NewLimits(context).CheckAsync(AiFeatures.Chat, user.Id, ModbotPermissions.UseAiChat, Ct));
        Assert.Null(await NewLimits(context).CheckAsync(AiFeatures.Insights, user.Id, ModbotPermissions.UseAiChat, Ct));
    }

    [Fact]
    public async Task ScheduledInsights_AreSkippedAtTheLimitForEveryone()
    {
        await TurnAiOnAsync();
        await PriceAsync(BaseModel, 1m, 1m);
        await LimitAsync(new AiSpendLimit { AppliesTo = AiSpendLimit.Everyone, PerDay = 0m });

        await using var context = NewContext();
        var attempt = await NewWriter(context).WriteAsync(InsightKinds.Group, InsightKinds.EveryWeek, new DateOnly(2029, 3, 15), InsightStart.Schedule(null), Ct);

        Assert.Equal("This Modbot's daily AI spend limit is reached.", attempt.NotAsked);
        Assert.Empty(Model.Requests);
    }

    // ── One fact per limit per period ────────────────────────────────────────────────────────

    [Fact]
    public async Task ReachingALimit_IsRecordedOnce_AndAgainInTheNextPeriod()
    {
        await PriceAsync(Priced, 1m, 1m);
        await SpendAsync(AiFeatures.Moderation, 1_000_000);
        await LimitAsync(new AiSpendLimit { AppliesTo = AiSpendLimit.ForFeature, Feature = AiFeatures.Moderation, PerDay = 1m });

        for (var i = 0; i < 3; i++)
        {
            await using var context = NewContext();
            Assert.NotNull(await NewUsage(context).LimitReachedAsync(AiFeatures.Moderation, Ct));
        }

        await using (var read = NewContext())
        {
            var fact = Assert.Single(await read.Events.Where(e => e.Type == FactType.AiLimitReached).ToListAsync(Ct));
            Assert.Equal("feature:moderation", fact.SubjectId);
        }

        Clock.Advance(TimeSpan.FromDays(1));
        await SpendAsync(AiFeatures.Moderation, 1_000_000);

        await using (var context = NewContext())
            Assert.NotNull(await NewUsage(context).LimitReachedAsync(AiFeatures.Moderation, Ct));

        await using (var read = NewContext())
            Assert.Equal(2, await read.Events.CountAsync(e => e.Type == FactType.AiLimitReached, Ct));
    }

    [Fact]
    public async Task UnderEveryLimit_NothingIsRecorded()
    {
        await PriceAsync(Priced, 1m, 1m);
        await SpendAsync(AiFeatures.Moderation, 500_000);
        await LimitAsync(new AiSpendLimit { AppliesTo = AiSpendLimit.Everyone, PerDay = 1m });

        await using var context = NewContext();
        Assert.Null(await NewUsage(context).LimitReachedAsync(AiFeatures.Moderation, Ct));
        Assert.False(await context.Events.AnyAsync(e => e.Type == FactType.AiLimitReached, Ct));
    }

    // ── Prices ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnEnteredPrice_BeatsAFetchedOne()
    {
        await using (var context = NewContext())
        {
            context.AiFetchedPrices.Add(new AiFetchedPrice { Model = "both", InputPerMillion = 9m, OutputPerMillion = 9m, FetchedAt = Clock.UtcNow });
            context.AiFetchedPrices.Add(new AiFetchedPrice { Model = "fetched-only", InputPerMillion = 3m, OutputPerMillion = 4m, FetchedAt = Clock.UtcNow });
            context.AiModelPrices.Add(new AiModelPrice { Model = "both", InputPerMillion = 1m, OutputPerMillion = 2m, UpdatedAt = Clock.UtcNow });
            await context.SaveChangesAsync(Ct);
        }

        await using var read = NewContext();
        var prices = await AiPrices.ForAsync(read, ["both", "fetched-only", "neither"], Ct);

        Assert.Equal((1m, AiPriceSources.Entered), (prices["both"].InputPerMillion, prices["both"].Source));
        Assert.Equal((3m, AiPriceSources.OpenRouter), (prices["fetched-only"].InputPerMillion, prices["fetched-only"].Source));
        Assert.False(prices.ContainsKey("neither"));
    }

    [Fact]
    public async Task FetchedPrices_AreSaved_WithWhenTheyWereFetched_AndNeverTouchEnteredOnes()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(AiPricesTests.ModelList, Encoding.UTF8, "application/json"),
        });

        await PriceAsync("anthropic/claude-sonnet-5", 1m, 1m);

        await using (var context = NewContext())
        {
            var result = await new OpenRouterPrices(context, new Factory(handler), Clock).FetchAsync(Ct);
            Assert.Null(result.Error);
            Assert.Equal(4, result.Saved);
        }

        Assert.Equal(OpenRouterPrices.ModelList.ToString(), Assert.Single(handler.Requests).Url);

        await using var read = NewContext();
        var fetched = await read.AiFetchedPrices.SingleAsync(p => p.Model == "anthropic/claude-sonnet-5", Ct);
        Assert.Equal(2m, fetched.InputPerMillion);
        Assert.Equal(Clock.UtcNow, fetched.FetchedAt);

        Assert.Equal(1m, (await read.AiModelPrices.SingleAsync(Ct)).InputPerMillion);
        Assert.Equal(AiPriceSources.Entered, (await AiPrices.ForAsync(read, ["anthropic/claude-sonnet-5"], Ct))["anthropic/claude-sonnet-5"].Source);
    }

    [Fact]
    public async Task A429FromOpenRouter_IsAFailureThatSavesNothing_AndIsNotRetried()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        await using var context = NewContext();
        var result = await new OpenRouterPrices(context, new Factory(handler), Clock).FetchAsync(Ct);

        Assert.True(result.RateLimited);
        Assert.NotNull(result.Error);
        Assert.Single(handler.Requests);
        Assert.False(await context.AiFetchedPrices.AnyAsync(Ct));
    }

    [Fact]
    public async Task TheScheduledFetch_OnlyAsksOpenRouter_WhenAiIsOnAndSetToOpenRouter()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(AiPricesTests.ModelList, Encoding.UTF8, "application/json"),
        });

        await TurnAiOnAsync();
        var service = NewFetchService(handler);

        await service.RunOnceAsync(Ct);
        Assert.Empty(handler.Requests);

        await using (var context = NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.AiProvider = AiProviders.OpenRouter.Id;
            await context.SaveChangesAsync(Ct);
        }

        await service.RunOnceAsync(Ct);
        await service.RunOnceAsync(Ct);
        Assert.Single(handler.Requests);

        Clock.Advance(AiPriceFetchService.FetchEvery);
        await service.RunOnceAsync(Ct);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TheCostOpenRouterReports_IsRecorded_AndUsedInsteadOfThePriceList()
    {
        await PriceAsync(Priced, 1000m, 1000m);

        var usage = System.ClientModel.Primitives.ModelReaderWriter.Read<OpenAI.Chat.ChatCompletion>(BinaryData.FromString(
            """{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":1000,"completion_tokens":10,"total_tokens":1010,"cost":0.0042}}"""))!.Usage;

        await using (var context = NewContext())
            await NewUsage(context).RecordAsync(AiFeatures.Chat, null, Priced, AiProviders.OpenRouter.Id, usage, Ct);

        await using var read = NewContext();
        Assert.Equal(0.0042m, (await read.AiUsage.SingleAsync(Ct)).ReportedCost);

        var (today, _) = await AiSpending.TodayAndMonthAsync(read, Clock.UtcNow, null, null, Ct);
        Assert.Equal(0.0042m, today.Cost);
    }

    // ── Unknown spend ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SpendOfAModelWithNoPrice_IsUnknown_NotZero()
    {
        await PriceAsync(Priced, 1m, 1m);
        await SpendAsync(AiFeatures.Chat, 1_000_000);
        await SpendAsync(AiFeatures.Chat, 300_000, model: "no-price-model");

        await using var context = NewContext();
        var summary = await new AiSpendReport(context, Clock).ReadAsync(Ct);
        var chat = summary.For(AiFeatures.Chat);

        Assert.Equal(1m, chat.Month.Cost);
        Assert.Equal(300_000, chat.Month.UnpricedTokens);
        Assert.True(chat.Month.PartUnknown);
        Assert.True(chat.Estimate.PartUnknown);
        Assert.True(summary.Total.Today.PartUnknown);
        Assert.True(Assert.Single(summary.Days, d => d.Feature == AiFeatures.Chat).Spent.PartUnknown);
    }

    // ── Spend by feature and the estimate ────────────────────────────────────────────────────

    [Fact]
    public async Task SpendByFeature_CoversTodayThisWeekThisMonthAndLastMonth_WithTheEstimate()
    {
        await PriceAsync(Priced, 1m, 1m);

        // Today, Thursday 15th: $1. Monday 12th: $2. 1st: $4. Last month, 20 Feb: $8.
        await SpendAsync(AiFeatures.Moderation, 1_000_000);
        await SpendAsync(AiFeatures.Moderation, 2_000_000, at: Start.AddDays(-3));
        await SpendAsync(AiFeatures.Moderation, 4_000_000, at: Start.AddDays(-14));
        await SpendAsync(AiFeatures.Moderation, 8_000_000, at: new DateTimeOffset(2029, 2, 20, 9, 0, 0, TimeSpan.Zero));
        await SpendAsync(AiFeatures.Insights, 500_000, at: Start.AddDays(-1));

        await using var context = NewContext();
        var summary = await new AiSpendReport(context, Clock).ReadAsync(Ct);
        var moderation = summary.For(AiFeatures.Moderation);

        Assert.Equal(1m, moderation.Today.Cost);
        Assert.Equal(3m, moderation.Week.Cost);
        Assert.Equal(7m, moderation.Month.Cost);
        Assert.Equal(8m, moderation.LastMonth.Cost);
        Assert.Equal(2_000_000, moderation.LastSevenDays.InputTokens);

        // $7 so far, plus $2 over the last seven days times 16 days left over 7.
        Assert.Equal(Math.Round(7m + 2m / 7m * 16m, 6), moderation.Estimate.Cost);

        Assert.Equal(7.5m, summary.Total.Month.Cost);
        Assert.Equal(new DateOnly(2029, 2, 14), summary.FirstDay);
        Assert.Equal(new DateOnly(2029, 3, 15), summary.LastDay);
        Assert.Equal(
            [(new DateOnly(2029, 2, 20), AiFeatures.Moderation), (new DateOnly(2029, 3, 1), AiFeatures.Moderation), (new DateOnly(2029, 3, 12), AiFeatures.Moderation),
             (new DateOnly(2029, 3, 14), AiFeatures.Insights), (new DateOnly(2029, 3, 15), AiFeatures.Moderation)],
            summary.Days.Select(d => (d.Day, d.Feature)));
    }

    [Fact]
    public async Task Warnings_ComeFromTheLimitsForEveryoneAndForFeatures()
    {
        await PriceAsync(Priced, 1m, 1m);
        await SpendAsync(AiFeatures.Moderation, 8_000_000);
        await SpendAsync(AiFeatures.Chat, 1_000_000);

        await LimitAsync(new AiSpendLimit { AppliesTo = AiSpendLimit.ForFeature, Feature = AiFeatures.Moderation, PerDay = 10m });
        await LimitAsync(new AiSpendLimit { AppliesTo = AiSpendLimit.Everyone, PerDay = 9m });
        await LimitAsync(new AiSpendLimit { AppliesTo = AiSpendLimit.ForFeature, Feature = AiFeatures.Chat, PerDay = 100m });

        await using var context = NewContext();
        var warnings = await new AiSpendReport(context, Clock).WarningsAsync(Ct);

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w is { AppliesTo: AiSpendLimit.Everyone, Reached: true, Spent: 9m });
        Assert.Contains(warnings, w => w is { AppliesTo: AiSpendLimit.ForFeature, Feature: AiFeatures.Moderation, Reached: false, Spent: 8m });
    }

    [Fact]
    public async Task WithNoLimits_ThereAreNoWarnings()
    {
        await PriceAsync(Priced, 1m, 1m);
        await SpendAsync(AiFeatures.Moderation, 8_000_000);

        await using var context = NewContext();
        Assert.Empty(await new AiSpendReport(context, Clock).WarningsAsync(Ct));
    }

    [Fact]
    public async Task TopChatUsers_AreOrderedBySpend()
    {
        var a = await UserAsync("a");
        var b = await UserAsync("b");
        await PriceAsync(Priced, 1m, 1m);
        await SpendAsync(AiFeatures.Chat, 1_000_000, a.Id);
        await SpendAsync(AiFeatures.Chat, 3_000_000, b.Id);
        await SpendAsync(AiFeatures.Insights, 9_000_000, a.Id);

        await using var context = NewContext();
        var top = await new AiSpendReport(context, Clock).TopChatUsersAsync(10, Ct);

        Assert.Equal([("b", 3m), ("a", 1m)], top.Select(u => (u.Username, u.Spent.Cost)));
    }

    // ── Token limits become money limits ─────────────────────────────────────────────────────

    [Fact]
    public async Task ATokenLimit_BecomesAMonthlyMoneyLimit_OnceTheFeaturesModelHasAPrice()
    {
        await TurnAiOnAsync();

        await using (var context = NewContext())
        {
            context.AiFeatureLimits.Add(new AiFeatureLimit { Feature = AiFeatures.Moderation, MonthlyTokenLimit = 4_000_000 });
            await context.SaveChangesAsync(Ct);
        }

        // Used 3 input to 1 output.
        await SpendAsync(AiFeatures.Moderation, 300, model: BaseModel, output: 100);

        await using (var context = NewContext())
            Assert.Empty(await new AiTokenLimits(context, Clock).ChangeToMoneyAsync(Ct));

        await using (var read = NewContext())
            Assert.True(await read.AiFeatureLimits.AnyAsync(Ct));

        await PriceAsync(BaseModel, 1m, 5m);

        await using (var context = NewContext())
        {
            var change = Assert.Single(await new AiTokenLimits(context, Clock).ChangeToMoneyAsync(Ct));
            Assert.Equal(new AiTokenLimitChange(AiFeatures.Moderation, 4_000_000, BaseModel, 8m), change);
        }

        await using var after = NewContext();
        Assert.False(await after.AiFeatureLimits.AnyAsync(Ct));

        var limit = await after.AiSpendLimits.SingleAsync(Ct);
        Assert.Equal((AiSpendLimit.ForFeature, AiFeatures.Moderation, (decimal?)null, (decimal?)8m), (limit.AppliesTo, limit.Feature, limit.PerDay, limit.PerMonth));
    }

    [Fact]
    public async Task ATokenLimit_KeepsALowerMoneyLimitAlreadySet()
    {
        await TurnAiOnAsync();
        await PriceAsync(BaseModel, 1m, 1m);
        await LimitAsync(new AiSpendLimit { AppliesTo = AiSpendLimit.ForFeature, Feature = AiFeatures.Insights, PerDay = 0.1m, PerMonth = 0.5m });

        await using (var context = NewContext())
        {
            context.AiFeatureLimits.Add(new AiFeatureLimit { Feature = AiFeatures.Insights, MonthlyTokenLimit = 2_000_000 });
            await context.SaveChangesAsync(Ct);
            await new AiTokenLimits(context, Clock).ChangeToMoneyAsync(Ct);
        }

        await using var read = NewContext();
        var limit = await read.AiSpendLimits.SingleAsync(Ct);
        Assert.Equal((0.1m, 0.5m), (limit.PerDay!.Value, limit.PerMonth!.Value));
        Assert.False(await read.AiFeatureLimits.AnyAsync(Ct));
    }

    [Fact]
    public async Task ATokenLimitWithNoPrice_IsStillCounted_AndStops()
    {
        await SpendAsync(AiFeatures.Moderation, 900, model: "no-price-model", output: 100);

        await using (var context = NewContext())
        {
            context.AiFeatureLimits.Add(new AiFeatureLimit { Feature = AiFeatures.Moderation, MonthlyTokenLimit = 1000 });
            await context.SaveChangesAsync(Ct);
        }

        await using var check = NewContext();
        var reached = await NewUsage(check).LimitReachedAsync(AiFeatures.Moderation, Ct);

        Assert.Equal((AiLimitUnits.Tokens, "The monthly AI token limit for Moderation is reached."), (reached?.Unit, reached?.Message));
    }

    // ── Pieces ───────────────────────────────────────────────────────────────────────────────

    private async Task PriceAsync(string model, decimal input, decimal output)
    {
        await using var context = NewContext();
        context.AiModelPrices.Add(new AiModelPrice { Model = model, InputPerMillion = input, OutputPerMillion = output, UpdatedAt = Clock.UtcNow });
        await context.SaveChangesAsync(Ct);
    }

    private async Task SpendAsync(string feature, int input, Guid? userId = null, DateTimeOffset? at = null, string model = Priced, int output = 0)
    {
        await using var context = NewContext();
        context.AiUsage.Add(new AiUsage
        {
            At = at ?? Clock.UtcNow,
            Feature = feature,
            UserId = userId,
            Model = model,
            InputTokens = input,
            OutputTokens = output,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task LimitAsync(AiSpendLimit limit)
    {
        await using var context = NewContext();
        limit.UpdatedAt = Clock.UtcNow;
        context.AiSpendLimits.Add(limit);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<ModbotUser> UserAsync(string name = "sam")
    {
        await using var context = NewContext();
        var user = new ModbotUser { Username = name, UsernameNormalized = name.ToUpperInvariant(), PasswordHash = "x" };
        context.Users.Add(user);
        await context.SaveChangesAsync(Ct);
        return user;
    }

    private AiPriceFetchService NewFetchService(ScriptedHandler handler)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        services.AddSingleton<IModbotClock>(Clock);
        services.AddSingleton<IHttpClientFactory>(new Factory(handler));
        services.AddScoped<OpenRouterPrices>();
        services.AddScoped<AiTokenLimits>();

        return new AiPriceFetchService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), Clock);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
