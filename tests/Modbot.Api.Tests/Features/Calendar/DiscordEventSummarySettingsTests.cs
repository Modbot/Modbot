using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using SettingsRow = Modbot.Core.Data.Entities.Settings;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// Settings → Discord → Monthly event summary: off to start; a channel is needed to turn it on;
/// turning it on sets the last month to the one before, so a month already over is never posted;
/// and only somebody who can change settings may save it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordEventSummarySettingsTests
{
    private const string Channel = "900";
    private const string Url = "/api/settings/discord-event-summary";

    private readonly PostgresFixture _db;

    public DiscordEventSummarySettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SettingsRow> StoredAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ModbotContext>().GetSettingsAsync(Ct);
    }

    [Fact]
    public async Task NothingChosen_IsOff()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var view = await host.GetJsonAsync<DiscordEventSummarySettingsResponse>(Url, cookie, Ct);

        Assert.False(view.On);
        Assert.Null(view.ChannelId);
        Assert.Null(view.Problem);
    }

    [Fact]
    public async Task TurningItOn_NeedsAChannel_AndStartsFromTheMonthBefore()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var refused = await host.PutJsonAsync(Url, new { on = true }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False((await StoredAsync(host)).EventSummaryOn);

        var saved = await host.PutJsonAsync(Url, new { on = true, channelId = Channel }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var stored = await StoredAsync(host);
        Assert.True(stored.EventSummaryOn);
        Assert.Equal(Channel, stored.EventSummaryChannelId);
        Assert.Equal(EventMonthSummary.MonthOf(host.Clock.UtcNow).AddMonths(-1), stored.EventSummaryLastMonth);
    }

    [Fact]
    public async Task SomebodyWithoutTheSettingsPermission_IsTurnedAway()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var viewer = await host.SignedInAsync(ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewMembers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(Url, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync(Url, new { on = false }, viewer, Ct)).StatusCode);
    }
}
