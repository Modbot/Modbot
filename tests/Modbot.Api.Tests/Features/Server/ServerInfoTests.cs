using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Tests.Features.Onboarding;
using Modbot.Core;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Server;

/// <summary>
/// <c>GET /api/server</c> (server info and account email design §2): what a stranger's browser is
/// told this Modbot is.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ServerInfoTests
{
    private readonly PostgresFixture _db;

    public ServerInfoTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BeforeSetupItAnswersOkWithNulls()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await host.GetAsync("/api/server", cookie: null, Ct);

        // Not 404: "a Modbot that has not been set up" is a real answer to somebody typing an
        // address, and a 404 looks the same as a wrong address.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.ReadJsonAsync(Ct);
        Assert.Equal(JsonValueKindNull, body.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKindNull, body.GetProperty("groupId").ValueKind);
        Assert.Equal(JsonValueKindNull, body.GetProperty("iconUrl").ValueKind);
        Assert.Equal(JsonValueKindNull, body.GetProperty("bannerUrl").ValueKind);
        Assert.Equal(JsonValueKindNull, body.GetProperty("ownerEmail").ValueKind);
        Assert.Equal(JsonValueKindNull, body.GetProperty("publicAddress").ValueKind);

        // The version is the one thing a server knows about itself before anybody sets it up.
        Assert.Equal(ModbotVersion.Release, body.GetProperty("version").GetString());
    }

    [Fact]
    public async Task OnceSetUpItSaysTheGroupTheAddressAndTheOwner()
    {
        var (host, _) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        await SetSettingsAsync(settings =>
        {
            settings.ManagedGroupId = "grp_test";
            settings.ManagedGroupName = "Sunset Lounge";
            settings.ManagedGroupIconUrl = "https://files.example/icon.png";
            settings.ManagedGroupBannerUrl = "https://files.example/banner.png";
            settings.PublicAddress = "https://modbot.example.com";
        });

        var body = await (await host.GetAsync("/api/server", cookie: null, Ct)).ReadJsonAsync(Ct);

        Assert.Equal("Sunset Lounge", body.GetProperty("name").GetString());
        Assert.Equal("grp_test", body.GetProperty("groupId").GetString());
        Assert.Equal("https://files.example/icon.png", body.GetProperty("iconUrl").GetString());
        Assert.Equal("https://files.example/banner.png", body.GetProperty("bannerUrl").GetString());
        Assert.Equal("https://modbot.example.com", body.GetProperty("publicAddress").GetString());
        Assert.Equal(OnboardingTestContext.AdminEmail, body.GetProperty("ownerEmail").GetString());
    }

    [Fact]
    public async Task ItNeedsNoSessionAndIsReadableFromAnyOrigin()
    {
        var (host, _) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/server");
        request.Headers.Add("Origin", "https://my.modbot.co");

        var response = await host.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // my.modbot.co reads this from the browser of somebody with no account here.
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task APreflightIsAnswered()
    {
        var (host, _) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        var response = await host.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Options, "/api/server"), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task TheSwitchWithholdsTheOwnersAddressAndChangesNothingElse()
    {
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        await SetSettingsAsync(settings => settings.ManagedGroupName = "Sunset Lounge");

        var off = await host.SendJsonAsync(
            HttpMethod.Put, "/api/settings/server", new { showOwnerEmail = false }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await off.ReadJsonAsync(Ct)).GetProperty("showOwnerEmail").GetBoolean());

        var body = await (await host.GetAsync("/api/server", cookie: null, Ct)).ReadJsonAsync(Ct);

        Assert.Equal(JsonValueKindNull, body.GetProperty("ownerEmail").ValueKind);
        Assert.Equal("Sunset Lounge", body.GetProperty("name").GetString());

        // And back on again, so the switch is a switch and not a one-way door.
        await host.SendJsonAsync(
            HttpMethod.Put, "/api/settings/server", new { showOwnerEmail = true }, cookie, Ct);

        var again = await (await host.GetAsync("/api/server", cookie: null, Ct)).ReadJsonAsync(Ct);
        Assert.Equal(OnboardingTestContext.AdminEmail, again.GetProperty("ownerEmail").GetString());
    }

    [Fact]
    public async Task TheSwitchStartsOn()
    {
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        var body = await (await host.GetAsync("/api/settings/server", cookie, Ct)).ReadJsonAsync(Ct);

        Assert.True(body.GetProperty("showOwnerEmail").GetBoolean());
    }

    [Fact]
    public async Task TurningTheSwitchIsRecorded()
    {
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        await host.SendJsonAsync(
            HttpMethod.Put, "/api/settings/server", new { showOwnerEmail = false }, cookie, Ct);

        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);
        var payload = ApiTestHost.DataOf(Assert.Single(facts));

        Assert.Equal("server.showOwnerEmail", payload.GetProperty("setting").GetString());
        Assert.False(payload.GetProperty("after").GetBoolean());
    }

    [Fact]
    public async Task TheSwitchNeedsManageSettings()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Put, "/api/settings/server", new { showOwnerEmail = false }, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    private async Task SetSettingsAsync(Action<Core.Data.Entities.Settings> change)
    {
        await using var context = _db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        change(settings);
        await context.SaveChangesAsync(Ct);
    }
}
