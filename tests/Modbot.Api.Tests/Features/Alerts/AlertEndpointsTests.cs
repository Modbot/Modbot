using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Alerts;

/// <summary>
/// Reading and hiding alerts needs View analytics; changing what is watched needs Change settings.
/// Plus what a save checks, and that hiding keeps the alert.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AlertEndpointsTests
{
    private const string List = "/api/alerts";
    private const string Settings = "/api/settings/ai/alerts";

    private readonly PostgresFixture _db;

    public AlertEndpointsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task ClearAsync(PostgresFixture db)
    {
        await using var context = db.NewContext();
        await context.Alerts.ExecuteDeleteAsync(Ct);
        await context.AlertWatches.ExecuteDeleteAsync(Ct);
        await context.AlertSettings.ExecuteDeleteAsync(Ct);
    }

    private static object Body(string? channel = null, int quietHours = 6, string sensitivity = "normal") => new
    {
        discordChannelId = channel,
        quietHours,
        writeSentence = true,
        watchers = new[] { new { watcher = "flags", sensitivity } },
    };

    private static async Task<Guid> AddAlertAsync(PostgresFixture db, DateTimeOffset at)
    {
        await using var context = db.NewContext();

        var alert = new Alert
        {
            Watcher = AlertWatchers.Flags,
            At = at,
            WindowStart = at.AddHours(-1),
            WindowEnd = at,
            Now = 20,
            Normal = 2,
            Spread = 1,
            Score = 18,
            Sensitivity = AlertSensitivities.Normal,
            Figures = "{}",
            Link = "/flags",
        };

        context.Alerts.Add(alert);
        await context.SaveChangesAsync(Ct);
        return alert.Id;
    }

    [Fact]
    public async Task ListingWithoutSigningIn_Is401()
    {
        await using var host = await ApiTestHost.StartAsync(_db);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(List, Ct)).StatusCode);
    }

    [Fact]
    public async Task ListingAndHidingNeedViewAnalytics()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var id = await AddAlertAsync(_db, host.Clock.UtcNow);

        var (_, without) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, List, null, without, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, $"{List}/{id}/dismiss", null, without, Ct)).StatusCode);

        var (_, with) = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, List, null, with, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task TheSettingsNeedChangeSettings(string method)
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog, Ct);

        var response = await host.SendJsonAsync(new HttpMethod(method), Settings, method == "PUT" ? Body() : null, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EveryWatcherIsListedOffUntilSomebodyTurnsItOn()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Settings, null, cookie, Ct), Ct);

        Assert.Equal(
            AlertWatchers.All,
            body.GetProperty("watchers").EnumerateArray().Select(w => w.GetProperty("watcher").GetString()!).ToList());
        Assert.All(body.GetProperty("watchers").EnumerateArray(), w => Assert.Equal("off", w.GetProperty("sensitivity").GetString()));
        Assert.Equal(AlertWatchers.DefaultQuietHours, body.GetProperty("quietHours").GetInt32());
    }

    [Fact]
    public async Task ASaveReadsBack()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var saved = await host.SendJsonAsync(HttpMethod.Put, Settings, Body(channel: "112233", quietHours: 12, sensitivity: "high"), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var body = await ApiTestHost.BodyOf(saved, Ct);
        Assert.Equal("112233", body.GetProperty("discordChannelId").GetString());
        Assert.Equal(12, body.GetProperty("quietHours").GetInt32());

        var flags = body.GetProperty("watchers").EnumerateArray().Single(w => w.GetProperty("watcher").GetString() == "flags");
        Assert.Equal("high", flags.GetProperty("sensitivity").GetString());
    }

    [Theory]
    [InlineData("#general", 6, "normal", "Choose a Discord channel.")]
    [InlineData(null, 9999, "normal", "Choose a quiet time.")]
    [InlineData(null, 6, "paranoid", "Choose a sensitivity.")]
    public async Task ASaveThatCannotWorkIsRefused(string? channel, int quietHours, string sensitivity, string error)
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, Settings, Body(channel, quietHours, sensitivity), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AWatcherNobodyHasHeardOfIsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var body = new
        {
            discordChannelId = (string?)null,
            quietHours = 6,
            writeSentence = true,
            watchers = new[] { new { watcher = "weather", sensitivity = "normal" } },
        };

        var response = await host.SendJsonAsync(HttpMethod.Put, Settings, body, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Not something Modbot watches.", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task HidingAnAlertTakesItOffTheListAndKeepsIt()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var id = await AddAlertAsync(_db, host.Clock.UtcNow);

        var before = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, List, null, cookie, Ct), Ct);
        Assert.Single(before.GetProperty("alerts").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, $"{List}/{id}/dismiss", null, cookie, Ct)).StatusCode);

        var after = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, List, null, cookie, Ct), Ct);
        Assert.Empty(after.GetProperty("alerts").EnumerateArray());

        var all = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, $"{List}?all=true", null, cookie, Ct), Ct);
        Assert.Single(all.GetProperty("alerts").EnumerateArray());

        await using var context = _db.NewContext();
        Assert.NotNull((await context.Alerts.AsNoTracking().SingleAsync(a => a.Id == id, Ct)).DismissedAt);
    }

    [Fact]
    public async Task AnAlertOlderThanADayIsNotOnTheCard()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        await AddAlertAsync(_db, host.Clock.UtcNow.AddDays(-2));

        var body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, List, null, cookie, Ct), Ct);

        Assert.Empty(body.GetProperty("alerts").EnumerateArray());
    }

    [Fact]
    public async Task HidingAnAlertThatIsNotThere_Is404()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, $"{List}/{Guid.NewGuid()}/dismiss", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
