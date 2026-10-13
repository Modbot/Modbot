using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The event alert settings (event alerts design §3): off with direct messages on and an hour
/// before on a new install, saved in one request by Manage calendar, minutes held to 5 to 1,440,
/// a ping needing a channel, and a fact for each value that changed.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventAlertSettingsTests(PostgresFixture db)
{
    private const string Path = "/api/calendar/alerts";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync()
    {
        var host = await ApiTestHost.StartAsync(db);

        // The settings row is shared with every other test: start each one from the defaults.
        using var scope = host.Services.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await stored.GetSettingsAsync(Ct);
        settings.EventAlertsOn = false;
        settings.EventAlertsDirectMessage = true;
        settings.EventAlertsPing = false;
        settings.EventAlertsChannelId = null;
        settings.EventAlertsMinutesBefore = EventAlertSettings.DefaultMinutesBefore;
        await stored.SaveChangesAsync(Ct);

        return host;
    }

    private static Task<HttpResponseMessage> SaveAsync(
        ApiTestHost host, string cookie, bool on, bool directMessage, bool ping, string? channelId, int minutesBefore) =>
        host.SendJsonAsync(HttpMethod.Put, Path, new { on, directMessage, ping, channelId, minutesBefore }, cookie, Ct);

    private static bool IsAlertSetting(ModbotEvent fact) =>
        ApiTestHost.DataOf(fact).TryGetProperty("setting", out var setting)
        && setting.GetString()?.StartsWith("eventAlerts", StringComparison.Ordinal) == true;

    private static async Task<List<ModbotEvent>> AlertFactsAsync(ApiTestHost host) =>
        [.. (await host.FactsAsync(FactType.SettingsChanged, "settings", Ct)).Where(IsAlertSetting)];

    [Fact]
    public void ANewInstallHasAlertsOff_DirectMessagesOn_AndAnHourBefore()
    {
        var settings = new Modbot.Core.Data.Entities.Settings();

        Assert.False(settings.EventAlertsOn);
        Assert.True(settings.EventAlertsDirectMessage);
        Assert.False(settings.EventAlertsPing);
        Assert.Null(settings.EventAlertsChannelId);
        Assert.Equal(60, settings.EventAlertsMinutesBefore);
    }

    [Fact]
    public async Task TheEndpointAnswersWithTheDefaults()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Get, Path, null, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var view = await ApiTestHost.BodyOf(response, Ct);
        Assert.False(view.GetProperty("on").GetBoolean());
        Assert.True(view.GetProperty("directMessage").GetBoolean());
        Assert.False(view.GetProperty("ping").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, view.GetProperty("channelId").ValueKind);
        Assert.Equal(60, view.GetProperty("minutesBefore").GetInt32());
    }

    [Fact]
    public async Task OnlyManageCalendarCanReadOrSaveThem()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SaveAsync(host, viewer, true, true, false, null, 60)).StatusCode);

        // The refused request changed nothing.
        var view = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, manager, Ct), Ct);
        Assert.False(view.GetProperty("on").GetBoolean());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(1441)]
    public async Task MinutesOutsideFiveToADayAreRefused(int minutes)
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var before = (await AlertFactsAsync(host)).Count;

        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(host, manager, true, true, false, null, minutes)).StatusCode);

        var view = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, manager, Ct), Ct);
        Assert.False(view.GetProperty("on").GetBoolean());
        Assert.Equal(60, view.GetProperty("minutesBefore").GetInt32());
        Assert.Equal(before, (await AlertFactsAsync(host)).Count);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1440)]
    public async Task TheEdgesOfTheRangeAreAllowed(int minutes)
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var response = await SaveAsync(host, manager, true, true, false, null, minutes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(minutes, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("minutesBefore").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PingOnNeedsAChannel(string? channelId)
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(host, manager, true, true, true, channelId, 60)).StatusCode);

        var view = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, manager, Ct), Ct);
        Assert.False(view.GetProperty("ping").GetBoolean());
    }

    [Fact]
    public async Task SavingPingWithAChannelStoresThemAndAChannelWithPingOffIsKept()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var saved = await SaveAsync(host, manager, true, false, true, " 123456 ", 30);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var view = await ApiTestHost.BodyOf(saved, Ct);
        Assert.True(view.GetProperty("on").GetBoolean());
        Assert.False(view.GetProperty("directMessage").GetBoolean());
        Assert.True(view.GetProperty("ping").GetBoolean());
        Assert.Equal("123456", view.GetProperty("channelId").GetString());
        Assert.Equal(30, view.GetProperty("minutesBefore").GetInt32());

        var again = await ApiTestHost.BodyOf(await SaveAsync(host, manager, true, true, false, "123456", 30), Ct);
        Assert.False(again.GetProperty("ping").GetBoolean());
        Assert.Equal("123456", again.GetProperty("channelId").GetString());
    }

    [Fact]
    public async Task EachValueThatChangedIsAFact_AndTheSameAnswerAgainSaysNothing()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var earlier = (await AlertFactsAsync(host)).Select(f => f.Id).ToHashSet();
        var before = earlier.Count;

        // From the defaults: on, ping on, a channel, 90 minutes. Direct message stays on.
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(host, manager, true, true, true, "777", 90)).StatusCode);

        var facts = await AlertFactsAsync(host);
        Assert.Equal(before + 4, facts.Count);

        var settings = facts.Where(f => !earlier.Contains(f.Id))
            .Select(f => ApiTestHost.DataOf(f).GetProperty("setting").GetString())
            .Order()
            .ToList();
        Assert.Equal(
            ["eventAlertsChannelId", "eventAlertsMinutesBefore", "eventAlertsOn", "eventAlertsPing"],
            settings);

        var minutes = ApiTestHost.DataOf(facts.First(f =>
            !earlier.Contains(f.Id)
            && ApiTestHost.DataOf(f).GetProperty("setting").GetString() == "eventAlertsMinutesBefore"));
        Assert.Equal(60, minutes.GetProperty("before").GetInt32());
        Assert.Equal(90, minutes.GetProperty("after").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(host, manager, true, true, true, "777", 90)).StatusCode);
        Assert.Equal(before + 4, (await AlertFactsAsync(host)).Count);

        // One value changing writes one fact.
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(host, manager, true, false, true, "777", 90)).StatusCode);
        Assert.Equal(before + 5, (await AlertFactsAsync(host)).Count);
    }
}
