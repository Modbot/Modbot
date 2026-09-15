using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.AI.Insights;
using Modbot.AI.Usage;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.TestSupport;
using Npgsql;

namespace Modbot.AI.Tests.Insights;

/// <summary>
/// One container for the whole assembly. Repeated per test assembly because xUnit resolves
/// collection definitions only within the assembly declaring the tests.
/// </summary>
[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

/// <summary>
/// A private migrated database per test, a fake clock, and a scripted model endpoint. Nothing here
/// reaches a real provider.
/// </summary>
/// <remarks>
/// Private because the figures are sums over whole tables: two tests sharing a database would be
/// counting each other's rows.
/// </remarks>
public abstract class InsightTestBase : IAsyncLifetime
{
    /// <summary>A Thursday. Fixed, so a week always has the same days in it.</summary>
    protected static readonly DateTimeOffset Start = new(2029, 3, 15, 12, 0, 0, TimeSpan.Zero);

    protected const string Endpoint = "https://llm.test/v1";
    protected const string BaseModel = "base-model";

    private readonly PostgresFixture _fixture;
    private string _connectionString = "";

    protected InsightTestBase(PostgresFixture fixture) => _fixture = fixture;

    protected FakeClock Clock { get; } = new(Start);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What the scripted model answers. Replace it to make the call fail.</summary>
    protected Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } =
        _ => Completion("Joins were up on last week.");

    protected ScriptedHandler Model { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var name = $"modbot_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        _connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name }.ConnectionString;

        await using var context = NewContext();
        await context.Database.MigrateAsync(Ct);

        Model = new ScriptedHandler(request => Answer(request));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected ModbotContext NewContext()
        => new(new DbContextOptionsBuilder<ModbotContext>().UseNpgsql(_connectionString).Options);

    protected InsightWriter NewWriter(ModbotContext context)
        => new(context, NewClients(context), NewUsage(context), Clock, new InsightFigureReader(context));

    protected AiSpendLimits NewLimits(ModbotContext context)
        => new(context, Clock, new AiLimitNotices(context, new FactWriter(context, Clock), new EventPartitionMaintainer(context, Clock), Clock, new NoAiSpendAlerts()));

    protected AiUsageLedger NewUsage(ModbotContext context) => new(context, Clock, NewLimits(context));

    protected InsightScheduler NewScheduler(ModbotContext context)
        => new(context, NewWriter(context), Clock);

    protected AiClients NewClients(ModbotContext context)
        => new(context, AesGcmSecretProtector.ForTesting(), new SingleClientFactory(Model));

    protected async Task TurnAiOnAsync(bool on = true)
    {
        await using var context = NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.AiEnabled = on;
        settings.AiProvider = "custom";
        settings.AiEndpoint = Endpoint;
        settings.AiModel = BaseModel;
        await context.SaveChangesAsync(Ct);
    }

    protected async Task AddTotalAsync(DateOnly day, string metric, decimal value, string dimension = "")
    {
        await using var context = NewContext();
        context.DailyTotals.Add(new DailyTotal
        {
            Day = day,
            Metric = metric,
            Dimension = dimension,
            Value = value,
            Origin = DailyTotalOrigin.Computed,
        });
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>A headcount as the group-info sync writes it: the whole snapshot on its first fact.</summary>
    protected async Task AddMemberCountAsync(DateTimeOffset at, int members)
    {
        await using var context = NewContext();
        await new EventPartitionMaintainer(context, Clock).EnsureForAsync(at, Ct);
        await new FactWriter(context, Clock).WriteManyAsync(
        [
            new FactRecord
            {
                Type = FactType.GroupInfoChanged,
                OccurredAt = at,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = "grp_test",
                Source = FactSource.SyncDiff,
                Data = new JsonObject { ["baseline"] = new JsonObject { ["MemberCount"] = members } },
            },
        ], Ct);
    }

    protected static DateOnly Day(int month, int day) => new(2029, month, day);

    /// <summary>A monthly spend limit for insights of $1, already used up this month.</summary>
    protected async Task UseUpTheLimitAsync()
    {
        await using var context = NewContext();
        context.AiModelPrices.Add(new AiModelPrice { Model = BaseModel, InputPerMillion = 1000m, OutputPerMillion = 1000m, UpdatedAt = Clock.UtcNow });
        context.AiSpendLimits.Add(new AiSpendLimit { AppliesTo = AiSpendLimit.ForFeature, Feature = AiFeatures.Insights, PerMonth = 1m, UpdatedAt = Clock.UtcNow });
        context.AiUsage.Add(new AiUsage
        {
            At = Clock.UtcNow,
            Feature = AiFeatures.Insights,
            Model = BaseModel,
            InputTokens = 900,
            OutputTokens = 100,
        });
        await context.SaveChangesAsync(Ct);
    }

    protected static HttpResponseMessage Completion(string text, string model = BaseModel) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            id = "chatcmpl-1",
            @object = "chat.completion",
            created = 1_700_000_000,
            model,
            choices = new[]
            {
                new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = text } },
            },
            usage = new
            {
                prompt_tokens = 900,
                completion_tokens = 120,
                total_tokens = 1020,
                prompt_tokens_details = new { cached_tokens = 300 },
            },
        }), Encoding.UTF8, "application/json"),
    };

    protected static HttpResponseMessage Refused(string message) => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { error = new { message } }), Encoding.UTF8, "application/json"),
    };

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
