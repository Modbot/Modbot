using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI.Usage;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Insights;

/// <summary>
/// Reading insights needs View analytics; changing when they are written, and Generate now, need
/// Change settings. Plus what a save checks and does.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class InsightEndpointsTests
{
    private const string List = "/api/insights";
    private const string Settings = "/api/settings/ai/insights";
    private const string Generate = "/api/settings/ai/insights/group/generate";

    private readonly PostgresFixture _db;

    public InsightEndpointsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static readonly TheoryData<string, string> SettingsRequests = new()
    {
        { "GET", Settings },
        { "PUT", Settings },
        { "POST", Generate },
    };

    private static async Task ClearAsync(PostgresFixture db)
    {
        await using var context = db.NewContext();
        await context.Insights.ExecuteDeleteAsync(Ct);
        await context.InsightSchedules.ExecuteDeleteAsync(Ct);
        await context.InsightSettings.ExecuteDeleteAsync(Ct);
    }

    private static object Body(string? timeZone = null, string every = "week", int hour = 9, int weekday = 1, string? channel = null, bool enabled = true) => new
    {
        timeZone,
        model = (string?)null,
        kinds = new[] { new { kind = "group", enabled, every, hour, weekday, discordChannelId = channel } },
    };

    [Fact]
    public async Task ListingWithoutSigningIn_Is401()
    {
        await using var host = await ApiTestHost.StartAsync(_db);

        var response = await host.Client.GetAsync(List, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListingNeedsViewAnalytics()
    {
        await using var host = await ApiTestHost.StartAsync(_db);

        var (_, without) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, List, null, without, Ct)).StatusCode);

        var (_, with) = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, List, null, with, Ct)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(SettingsRequests))]
    public async Task SettingsAndGenerateNowNeedChangeSettings(string method, string path)
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog, Ct);

        var response = await host.SendJsonAsync(new HttpMethod(method), path, method == "PUT" ? Body() : null, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EveryKindIsListedOffUntilSomebodyTurnsItOn()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Settings, null, cookie, Ct), Ct);

        Assert.Equal(["group", "team", "rooms"], body.GetProperty("kinds").EnumerateArray().Select(k => k.GetProperty("kind").GetString()));
        Assert.All(body.GetProperty("kinds").EnumerateArray(), k => Assert.False(k.GetProperty("enabled").GetBoolean()));
    }

    [Fact]
    public async Task ASaveReadsBack_AndMarksTheTimeAlreadyGoneByAsHandled()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        // 2026-01-01 is a Thursday; the fake clock starts at midnight, so Monday 9 am last came on 29 December.
        var saved = await host.SendJsonAsync(HttpMethod.Put, Settings, Body(timeZone: "Europe/London", channel: "112233"), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var body = await ApiTestHost.BodyOf(saved, Ct);
        Assert.Equal("Europe/London", body.GetProperty("timeZone").GetString());

        var group = body.GetProperty("kinds").EnumerateArray().Single(k => k.GetProperty("kind").GetString() == "group");
        Assert.True(group.GetProperty("enabled").GetBoolean());
        Assert.Equal("112233", group.GetProperty("discordChannelId").GetString());

        await using var context = _db.NewContext();
        var schedule = await context.InsightSchedules.AsNoTracking().SingleAsync(s => s.Kind == "group", Ct);
        Assert.Equal(new DateTimeOffset(2025, 12, 29, 9, 0, 0, TimeSpan.Zero), schedule.HandledThrough);
    }

    [Theory]
    [InlineData("Mars/Olympus", "week", 9, 1, null, "Choose a time zone from the list.")]
    [InlineData(null, "month", 9, 1, null, "Choose every day or every week.")]
    [InlineData(null, "week", 24, 1, null, "Choose a time.")]
    [InlineData(null, "week", 9, 7, null, "Choose a day of the week.")]
    [InlineData(null, "week", 9, 1, "#general", "Choose a Discord channel.")]
    public async Task ASaveThatCannotWorkIsRefused(string? timeZone, string every, int hour, int weekday, string? channel, string error)
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, Settings, Body(timeZone, every, hour, weekday, channel), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task GenerateNowWithAiOff_Is409_AndStoresNothing()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Generate, null, cookie, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var context = _db.NewContext();
        Assert.False(await context.Insights.AnyAsync(Ct));
    }

    [Fact]
    public async Task GenerateNowForAKindThatDoesNotExist_Is404()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/settings/ai/insights/weather/generate", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheListLeavesOutFailedAttempts_AndCarriesTheFigures()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        using (var scope = host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var figures = new Modbot.AI.Insights.InsightFigures(
                "team", new(2025, 12, 25), new(2025, 12, 31), new(2025, 12, 18), new(2025, 12, 24),
                [new("Warnings", 4, 2)], []);

            context.Insights.AddRange(
                new Insight { Kind = "team", FirstDay = figures.FirstDay, LastDay = figures.LastDay, CreatedAt = host.Clock.UtcNow, StartedBy = "schedule", Figures = figures.ToJson(), Text = "Twice as many warnings." },
                new Insight { Kind = "team", FirstDay = figures.FirstDay, LastDay = figures.LastDay, CreatedAt = host.Clock.UtcNow.AddMinutes(1), StartedBy = "schedule", Figures = figures.ToJson(), Error = "No credit left" });
            await context.SaveChangesAsync(Ct);
        }

        var body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, List + "?kind=team", null, cookie, Ct), Ct);

        var insight = Assert.Single(body.GetProperty("insights").EnumerateArray());
        Assert.Equal("Twice as many warnings.", insight.GetProperty("text").GetString());
        Assert.Equal("Moderation team", insight.GetProperty("label").GetString());

        var figure = Assert.Single(insight.GetProperty("figures").GetProperty("figures").EnumerateArray());
        Assert.Equal("Warnings", figure.GetProperty("name").GetString());
        Assert.Equal(4, figure.GetProperty("now").GetDecimal());
    }

    [Fact]
    public async Task GenerateNowWithTheSpendLimitReached_Is409WithTheLimitsMessage()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await using (var context = _db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.AiEnabled = true;
            settings.AiProvider = "custom";
            settings.AiEndpoint = "https://llm.test/v1";
            settings.AiModel = "base-model";

            await context.AiUsage.Where(u => u.Feature == AiFeatures.Insights).ExecuteDeleteAsync(Ct);
            await context.AiFeatureLimits.Where(l => l.Feature == AiFeatures.Insights).ExecuteDeleteAsync(Ct);

            context.AiFeatureLimits.Add(new AiFeatureLimit { Feature = AiFeatures.Insights, MonthlyTokenLimit = 10 });
            context.AiUsage.Add(new AiUsage { At = host.Clock.UtcNow, Feature = AiFeatures.Insights, Model = "base-model", InputTokens = 8, OutputTokens = 2 });
            await context.SaveChangesAsync(Ct);
        }

        try
        {
            var response = await host.SendJsonAsync(HttpMethod.Post, Generate, null, cookie, Ct);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("The AI spend limit for insights is reached.", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());

            await using var read = _db.NewContext();
            Assert.False(await read.Insights.AnyAsync(Ct));
        }
        finally
        {
            await using var cleanup = _db.NewContext();
            await cleanup.AiUsage.Where(u => u.Feature == AiFeatures.Insights).ExecuteDeleteAsync(Ct);
            await cleanup.AiFeatureLimits.Where(l => l.Feature == AiFeatures.Insights).ExecuteDeleteAsync(Ct);
            await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        }
    }
}
