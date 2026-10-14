using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using SettingsRow = Modbot.Core.Data.Entities.Settings;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// Settings → Discord → Event recaps: off to start; a channel is needed to turn it on; turning it on
/// stamps the moment, so an instance that closed before gets no recap; and only somebody who can
/// change settings may save it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordEventRecapSettingsTests
{
    private const string Channel = "900";
    private const string Url = "/api/settings/discord-event-recaps";

    private readonly PostgresFixture _db;

    public DiscordEventRecapSettingsTests(PostgresFixture db) => _db = db;

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

        var view = await host.GetJsonAsync<DiscordEventRecapSettingsResponse>(Url, cookie, Ct);

        Assert.False(view.On);
        Assert.Null(view.ChannelId);
        Assert.Null(view.Problem);
    }

    [Fact]
    public async Task TurningItOn_NeedsAChannel_AndStampsTheMomentItWasTurnedOn()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var refused = await host.PutJsonAsync(Url, new { on = true }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False((await StoredAsync(host)).EventRecapsOn);

        var saved = await host.PutJsonAsync(Url, new { on = true, channelId = Channel }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var stored = await StoredAsync(host);
        Assert.True(stored.EventRecapsOn);
        Assert.Equal(Channel, stored.EventRecapChannelId);
        Assert.Equal(host.Clock.UtcNow, stored.EventRecapsFrom);
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
