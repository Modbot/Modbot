using System.Net;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Tests.Features.Settings;

[Collection(nameof(PostgresCollection))]
public class SyncSettingsTests
{
    private readonly PostgresFixture _db;

    public SyncSettingsTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task ItReportsTheCadenceTheProducersWouldActuallyUse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, ct);
        var settings = await host.GetJsonAsync<SyncSettingsResponse>(
            "/api/settings/sync", cookie, ct);

        Assert.True(settings.Running);
        Assert.Equal(
            AuditLogSyncOptions.PacingFloor.TotalSeconds,
            settings.AuditLog.PacingFloorSeconds);

        // Clamped on read as well as on write, so what the screen shows is what the producer uses
        // rather than what somebody asked for (spec 4.2.1).
        Assert.True(settings.AuditLog.MinIntervalSeconds >= settings.AuditLog.PacingFloorSeconds);
        Assert.True(settings.GroupInfo.IntervalSeconds >= settings.GroupInfo.PacingFloorSeconds);
    }

    [Fact]
    public async Task ItSaysPlainlyThatNothingHereCanBeChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, ct);
        var settings = await host.GetJsonAsync<SyncSettingsResponse>(
            "/api/settings/sync", cookie, ct);

        // An editable-looking control that discarded its value would be worse than none: the
        // operator would believe they had dialled a rate down when they had not.
        Assert.False(settings.Editable);
        Assert.Equal(SyncSettingsEndpoints.NotEditable, settings.EditableExplanation);
    }

    [Fact]
    public async Task AHostWithNoProducers_ShowsTheDefaultsAndSaysNothingIsRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, withSync: false);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, ct);
        var settings = await host.GetJsonAsync<SyncSettingsResponse>(
            "/api/settings/sync", cookie, ct);

        Assert.False(settings.Running);
    }

    [Fact]
    public async Task WithoutManageSettings_Is403()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var response = await host.GetAsync("/api/settings/sync", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
