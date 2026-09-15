using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI;
using Modbot.AI.Usage;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → AI → Limits and the Health page's spend warnings: who may see and change them, and
/// what they show (AI chat design §10).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AiLimitsSettingsTests
{
    private const string Path = "/api/settings/ai";

    /// <summary>A small OpenRouter model list, the shape the live one had in September 2026.</summary>
    private const string ModelList = """
        {"data":[
          {"id":"test-model","pricing":{"prompt":"0.000001","completion":"0.000002","input_cache_read":"0.0000001"}},
          {"id":"other/model","pricing":{"prompt":"0.000003","completion":"0.000004"}}
        ]}
        """;

    private readonly PostgresFixture _db;

    public AiLimitsSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Who may use it ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/limits")]
    [InlineData("PUT", "/limits")]
    [InlineData("PUT", "/prices")]
    [InlineData("POST", "/prices/fetch")]
    public async Task WithoutTheSettingsPermission_EveryLimitsEndpointIsRefused(string method, string suffix)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var handler = new RecordingHandler(HttpStatusCode.OK, ModelList);
        await using var host = await StartAsync(handler);

        // Everything but ManageSettings, including the permission to go past limits.
        var (_, cookie) = await host.SignedInAsync(
            ModbotPermissions.UseAiChat | ModbotPermissions.UseAiPastLimits | ModbotPermissions.ViewOperationalLog | ModbotPermissions.ViewAuditLog, Ct);

        var body = method == "GET" ? null : new { limits = Array.Empty<object>(), prices = Array.Empty<object>() };

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(new HttpMethod(method), Path + suffix, body, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendJsonAsync(new HttpMethod(method), Path + suffix, body, null, Ct)).StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SpendWarningsOnHealth_NeedTheOperationalLogPermission()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartAsync(new RecordingHandler(HttpStatusCode.OK, ModelList));
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/health/sync", null, cookie, Ct)).StatusCode);
    }

    // ── What it shows ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SpendIsShownByFeature_WithTheEstimate_AndUnpricedSpendAsUnknown()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartAsync(new RecordingHandler(HttpStatusCode.OK, ModelList));
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SeedAsync(host, db =>
        {
            db.AiModelPrices.Add(new AiModelPrice { Model = "priced", InputPerMillion = 1m, OutputPerMillion = 1m, UpdatedAt = host.Clock.UtcNow });
            db.AiUsage.Add(Usage(host, AiFeatures.Moderation, "priced", 2_000_000));
            db.AiUsage.Add(Usage(host, AiFeatures.Chat, "priced", 500_000, user.Id));
            db.AiUsage.Add(Usage(host, AiFeatures.Chat, "no-price", 1234, user.Id));
            db.AiSpendLimits.Add(new AiSpendLimit { AppliesTo = AiSpendLimit.ForFeature, Feature = AiFeatures.Moderation, PerMonth = 50m, UpdatedAt = host.Clock.UtcNow });
        });

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Get, Path + "/limits", null, cookie, Ct));

        var spend = body.GetProperty("spend").EnumerateArray().ToDictionary(s => s.GetProperty("feature").GetString()!);
        Assert.Equal(["moderation", "insights", "chat", "test"], spend.Keys);
        Assert.Equal(2m, spend["moderation"].GetProperty("month").GetProperty("cost").GetDecimal());
        Assert.Equal(1234, spend["chat"].GetProperty("month").GetProperty("unpricedTokens").GetInt64());
        Assert.Equal(0.5m, spend["chat"].GetProperty("today").GetProperty("cost").GetDecimal());
        Assert.Equal(2.5m, body.GetProperty("total").GetProperty("month").GetProperty("cost").GetDecimal());

        var limit = Assert.Single(body.GetProperty("limits").EnumerateArray());
        Assert.Equal("feature", limit.GetProperty("appliesTo").GetString());
        Assert.Equal("Moderation", limit.GetProperty("name").GetString());
        Assert.True(limit.GetProperty("estimate").GetProperty("cost").GetDecimal() >= 2m);

        var top = Assert.Single(body.GetProperty("topChatUsers").EnumerateArray());
        Assert.Equal(user.Username, top.GetProperty("username").GetString());

        var noPrice = body.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("model").GetString() == "no-price");
        Assert.Equal(JsonValueKind.Null, noPrice.GetProperty("entered").ValueKind);
        Assert.Equal(JsonValueKind.Null, noPrice.GetProperty("fetched").ValueKind);
    }

    [Fact]
    public async Task FeatureLimits_AreSavedAndReadBack_AndTokenLimitsKeptWhenNotSent()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartAsync(new RecordingHandler(HttpStatusCode.OK, ModelList));
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SeedAsync(host, db => db.AiFeatureLimits.Add(new AiFeatureLimit { Feature = AiFeatures.Chat, MonthlyTokenLimit = 5000 }));

        var saved = await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path + "/limits", new
        {
            limits = new object[]
            {
                new { appliesTo = "feature", feature = "insights", perDay = 0.5m, perMonth = 5m },
                new { appliesTo = "everyone", perMonth = 20m },
            },
        }, cookie, Ct));

        Assert.Equal(["everyone", "feature"], saved.GetProperty("limits").EnumerateArray().Select(l => l.GetProperty("appliesTo").GetString()));
        Assert.Equal(5000, Assert.Single(saved.GetProperty("tokenLimits").EnumerateArray()).GetProperty("monthlyTokens").GetInt64());

        var unknown = await host.SendJsonAsync(HttpMethod.Put, Path + "/limits", new
        {
            limits = new object[] { new { appliesTo = "feature", feature = "nonsense", perDay = 1m } },
        }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var removed = await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path + "/limits", new
        {
            limits = Array.Empty<object>(),
            tokenLimits = Array.Empty<object>(),
        }, cookie, Ct));
        Assert.Empty(removed.GetProperty("tokenLimits").EnumerateArray());
        Assert.Empty(removed.GetProperty("limits").EnumerateArray());
    }

    [Fact]
    public async Task FetchPrices_SavesOpenRoutersPrices_AndShowsThemBesideTheEnteredOnes()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var handler = new RecordingHandler(HttpStatusCode.OK, ModelList);
        await using var host = await StartAsync(handler);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SeedAsync(host, db => db.AiModelPrices.Add(new AiModelPrice { Model = "test-model", InputPerMillion = 7m, OutputPerMillion = 7m, UpdatedAt = host.Clock.UtcNow }));

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/prices/fetch", null, cookie, Ct));

        Assert.Equal("https://openrouter.ai/api/v1/models", Assert.Single(handler.Requests).Url);
        Assert.Equal(host.Clock.UtcNow, body.GetProperty("pricesFetchedAt").GetDateTimeOffset());

        var model = body.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("model").GetString() == "test-model");
        Assert.Equal(7m, model.GetProperty("entered").GetProperty("inputPerMillion").GetDecimal());
        Assert.Equal(1m, model.GetProperty("fetched").GetProperty("inputPerMillion").GetDecimal());
        Assert.Equal(0.1m, model.GetProperty("fetched").GetProperty("cachedInputPerMillion").GetDecimal());
    }

    [Fact]
    public async Task AFailedFetch_Is502WithTheReason()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var handler = new RecordingHandler(HttpStatusCode.TooManyRequests, "{}");
        await using var host = await StartAsync(handler);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Path + "/prices/fetch", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("openrouter.ai answered 429.", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EnteringAPrice_ChangesATokenLimitIntoAMoneyLimit()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartAsync(new RecordingHandler(HttpStatusCode.OK, ModelList));
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SeedAsync(host, db => db.AiFeatureLimits.Add(new AiFeatureLimit { Feature = AiFeatures.Moderation, MonthlyTokenLimit = 3_000_000 }));

        await using (var db = _db.NewContext())
        {
            var settings = await db.GetSettingsAsync(Ct);
            settings.AiModel = "test-model";
            await db.SaveChangesAsync(Ct);
        }

        // With no usage to take a mix from, 3M tokens are priced as input: 3 x $2.
        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path + "/prices", new
        {
            prices = new[] { new { model = "test-model", inputPerMillion = 2m, cachedInputPerMillion = (decimal?)null, outputPerMillion = 4m } },
        }, cookie, Ct));

        Assert.Empty(body.GetProperty("tokenLimits").EnumerateArray());
        var limit = Assert.Single(body.GetProperty("limits").EnumerateArray());
        Assert.Equal("moderation", limit.GetProperty("feature").GetString());
        Assert.Equal(6m, limit.GetProperty("perMonth").GetDecimal());
    }

    // ── The connection test is a feature like any other ──────────────────────────────────────

    [Fact]
    public async Task TheConnectionTest_IsCounted_AndStopsAtTheLimitForEveryone()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"id":"c1","object":"chat.completion","created":1700000000,"model":"test-model","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"OK"}}],"usage":{"prompt_tokens":12,"completion_tokens":1,"total_tokens":13}}""");
        await using var host = await StartAsync(handler);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var form = new { provider = "custom", endpoint = "https://llm.example.org/v1", model = "test-model" };

        var first = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/test", form, cookie, Ct));
        Assert.True(first.GetProperty("worked").GetBoolean());

        await using (var read = _db.NewContext())
        {
            var usage = await read.AiUsage.SingleAsync(Ct);
            Assert.Equal((AiFeatures.Test, user.Id, 12), (usage.Feature, usage.UserId, usage.InputTokens));
        }

        await SeedAsync(host, db => db.AiSpendLimits.Add(new AiSpendLimit { AppliesTo = AiSpendLimit.Everyone, PerDay = 0m, UpdatedAt = host.Clock.UtcNow }));

        var second = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/test", form, cookie, Ct));
        Assert.False(second.GetProperty("worked").GetBoolean());
        Assert.Equal("This Modbot's daily AI spend limit is reached.", second.GetProperty("message").GetString());
        Assert.Single(handler.Requests);
    }

    // ── Health ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Health_ShowsALimitAtEightyPercent_AndOneReached()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartAsync(new RecordingHandler(HttpStatusCode.OK, ModelList));
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, Ct);

        await SeedAsync(host, db =>
        {
            db.AiModelPrices.Add(new AiModelPrice { Model = "priced", InputPerMillion = 1m, OutputPerMillion = 1m, UpdatedAt = host.Clock.UtcNow });
            db.AiUsage.Add(Usage(host, AiFeatures.Insights, "priced", 850_000));
            db.AiUsage.Add(Usage(host, AiFeatures.Chat, "priced", 1_000_000));
            db.AiSpendLimits.Add(new AiSpendLimit { AppliesTo = AiSpendLimit.ForFeature, Feature = AiFeatures.Insights, PerDay = 1m, UpdatedAt = host.Clock.UtcNow });
            db.AiSpendLimits.Add(new AiSpendLimit { AppliesTo = AiSpendLimit.ForFeature, Feature = AiFeatures.Chat, PerDay = 1m, UpdatedAt = host.Clock.UtcNow });
        });

        var health = await OkAsync(await host.SendJsonAsync(HttpMethod.Get, "/api/health/sync", null, cookie, Ct));
        var warnings = health.GetProperty("aiSpend").EnumerateArray().ToDictionary(w => w.GetProperty("feature").GetString()!);

        Assert.Equal(2, warnings.Count);
        Assert.False(warnings["insights"].GetProperty("reached").GetBoolean());
        Assert.Equal("Insights", warnings["insights"].GetProperty("label").GetString());
        Assert.True(warnings["chat"].GetProperty("reached").GetBoolean());
    }

    // ── Pieces ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The real host, with both the AI provider and OpenRouter answered by <paramref name="handler"/>.</summary>
    private Task<ApiTestHost> StartAsync(RecordingHandler handler) =>
        ApiTestHost.StartAsync(_db, configure: services =>
        {
            services.AddHttpClient(AiClients.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddHttpClient(OpenRouterPrices.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        });

    private static AiUsage Usage(ApiTestHost host, string feature, string model, int input, Guid? userId = null) => new()
    {
        At = host.Clock.UtcNow,
        Feature = feature,
        Model = model,
        InputTokens = input,
        UserId = userId,
    };

    private static async Task SeedAsync(ApiTestHost host, Action<ModbotContext> seed)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        seed(db);
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return body;
    }
}
