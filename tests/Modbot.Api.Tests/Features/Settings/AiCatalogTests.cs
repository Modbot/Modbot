using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI;
using Modbot.AI.Usage;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// The model picker's list: who may see it, what it says about each model, and what a thousand
/// calls would cost this deployment (AI chat design §10.9).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AiCatalogTests
{
    private const string Path = "/api/settings/ai/catalog";

    /// <summary>The shape OpenRouter's live list had in September 2026, cut down.</summary>
    private const string ModelList = """
        {"data":[
          {"id":"maker-a/cheap","name":"Maker A: Cheap","created":1756944000,"context_length":200000,
           "architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},
           "pricing":{"prompt":"0.000001","completion":"0.000002","input_cache_read":"0.0000001"},
           "top_provider":{"context_length":200000,"max_completion_tokens":32000},
           "supported_parameters":["tools","structured_outputs"]},
          {"id":"maker-b/dear","name":"Maker B: Dear","created":1756944000,
           "pricing":{"prompt":"0.00001","completion":"0.00002"},
           "supported_parameters":["structured_outputs"]},
          {"id":"openrouter/auto","name":"Auto Router","created":1756944000,
           "pricing":{"prompt":"-1","completion":"-1"},"supported_parameters":["tools","structured_outputs"]}
        ]}
        """;

    private readonly PostgresFixture _db;

    public AiCatalogTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithoutTheSettingsPermission_TheCatalogIsRefused()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartAsync(new RecordingHandler(HttpStatusCode.OK, ModelList));

        // Everything but ManageSettings, including the permissions the AI pages hand out.
        var (_, cookie) = await host.SignedInAsync(
            ModbotPermissions.UseAiChat | ModbotPermissions.UseAiPastLimits | ModbotPermissions.ViewOperationalLog, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendJsonAsync(HttpMethod.Get, Path, null, null, Ct)).StatusCode);
    }

    [Fact]
    public async Task AnUnknownFeature_IsRefused()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartAsync(new RecordingHandler(HttpStatusCode.OK, ModelList));
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.SendJsonAsync(HttpMethod.Get, $"{Path}?feature=billing", null, cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheFetchedList_IsServedWithPricesAndWhatEachModelCanDo()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartAsync(new RecordingHandler(HttpStatusCode.OK, ModelList));
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        // The picker's Refresh button is the price fetch, and it stores the list too.
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, "/api/settings/ai/prices/fetch", null, cookie, Ct)).StatusCode);

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Get, $"{Path}?feature=chat", null, cookie, Ct));

        Assert.Equal("chat", body.GetProperty("feature").GetString());
        Assert.Equal(["tools"], body.GetProperty("needs").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(host.Clock.UtcNow, body.GetProperty("fetchedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("average").ValueKind);

        var models = body.GetProperty("models").EnumerateArray().ToList();

        // Chat needs tools, so the one model with tools and a price is recommended and comes first.
        Assert.Equal("maker-a/cheap", models[0].GetProperty("id").GetString());
        Assert.True(models[0].GetProperty("recommended").GetBoolean());
        Assert.Equal("maker-a", models[0].GetProperty("maker").GetString());
        Assert.Equal(1m, models[0].GetProperty("inputPerMillion").GetDecimal());
        Assert.Equal(0.1m, models[0].GetProperty("cachedInputPerMillion").GetDecimal());
        Assert.Equal(200000, models[0].GetProperty("contextLength").GetInt32());
        Assert.Equal(32000, models[0].GetProperty("maxOutputTokens").GetInt32());
        Assert.True(models[0].GetProperty("imagesIn").GetBoolean());
        Assert.Equal("openrouter", models[0].GetProperty("priceSource").GetString());
        Assert.Equal(JsonValueKind.Null, models[0].GetProperty("costPerThousandCalls").ValueKind);

        var dear = models.Single(m => m.GetProperty("id").GetString() == "maker-b/dear");
        Assert.False(dear.GetProperty("recommended").GetBoolean());
        Assert.Equal(["tools"], dear.GetProperty("missing").EnumerateArray().Select(n => n.GetString()));

        var router = models.Single(m => m.GetProperty("id").GetString() == "openrouter/auto");
        Assert.True(router.GetProperty("priceVaries").GetBoolean());
        Assert.Equal(JsonValueKind.Null, router.GetProperty("inputPerMillion").ValueKind);
    }

    [Fact]
    public async Task WithUsage_EachModelShowsWhatAThousandCallsWouldCost_AndAnEnteredPriceWins()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartAsync(new RecordingHandler(HttpStatusCode.OK, ModelList));
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, "/api/settings/ai/prices/fetch", null, cookie, Ct)).StatusCode);

        await SeedAsync(host, db =>
        {
            db.AiModelPrices.Add(new AiModelPrice { Model = "maker-a/cheap", InputPerMillion = 4m, OutputPerMillion = 4m, UpdatedAt = host.Clock.UtcNow });

            // Two Chat calls of 1,000 input and 100 output tokens each.
            for (var i = 0; i < 2; i++)
            {
                db.AiUsage.Add(new AiUsage
                {
                    At = host.Clock.UtcNow,
                    Feature = AiFeatures.Chat,
                    Model = "maker-a/cheap",
                    InputTokens = 1000,
                    OutputTokens = 100,
                });
            }
        });

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Get, $"{Path}?feature=chat", null, cookie, Ct));

        var average = body.GetProperty("average");
        Assert.Equal(2, average.GetProperty("calls").GetInt64());
        Assert.Equal(1000m, average.GetProperty("inputTokens").GetDecimal());

        var cheap = body.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "maker-a/cheap");

        // The operator's price wins over OpenRouter's: 1,100 tokens at $4 a million, a thousand times.
        Assert.Equal("entered", cheap.GetProperty("priceSource").GetString());
        Assert.Equal(4m, cheap.GetProperty("inputPerMillion").GetDecimal());
        Assert.Equal(4.4m, cheap.GetProperty("costPerThousandCalls").GetDecimal());
    }

    private Task<ApiTestHost> StartAsync(RecordingHandler handler) =>
        ApiTestHost.StartAsync(_db, configure: services =>
        {
            services.AddHttpClient(AiClients.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddHttpClient(OpenRouterPrices.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        });

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
