using System.Net;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Auth;

[Collection(nameof(PostgresCollection))]
public class RequiresFlagTests
{
    private readonly PostgresFixture _db;

    public RequiresFlagTests(PostgresFixture db) => _db = db;

    private static string UniqueName() => $"u_{Guid.NewGuid():N}";

    private async Task<(ApiTestHost Host, string Cookie)> SignedInAsync(
        ModbotPermissions permissions, CancellationToken ct)
    {
        var host = await ApiTestHost.StartAsync(_db);
        var name = UniqueName();
        await host.CreateUserAsync(name, "hunter2", permissions, ct);
        return (host, await host.LoginAsync(name, "hunter2", ct));
    }

    [Fact]
    public async Task AnUnauthenticatedCaller_Gets401()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);

        var response = await host.Client.GetAsync(ApiTestHost.AuditProbe, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnAuthenticatedCallerWithoutTheFlag_Gets403()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, cookie) = await SignedInAsync(ModbotPermissions.ViewMembers, ct);
        await using var _ = host;

        var response = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, ApiTestHost.AuditProbe, cookie), ct);

        // 403, not 401: the caller is who they say they are, they are simply not allowed. A 401
        // here would send the SPA back to a login form that cannot fix anything.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnAuthenticatedCallerWithTheFlag_GetsThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, cookie) = await SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        await using var _ = host;

        var response = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, ApiTestHost.AuditProbe, cookie), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MultipleFlags_AreRequiredTogetherNotEither()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, cookie) = await SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        await using var _ = host;

        var partial = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, ApiTestHost.TwoFlagProbe, cookie), ct);

        Assert.Equal(HttpStatusCode.Forbidden, partial.StatusCode);
    }

    [Fact]
    public async Task AllRequestedFlags_Pass()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, cookie) = await SignedInAsync(
            ModbotPermissions.ViewAuditLog | ModbotPermissions.ManageSettings, ct);
        await using var _ = host;

        var response = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, ApiTestHost.TwoFlagProbe, cookie), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Administrator_SatisfiesAFlagItWasNeverExplicitlyGranted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, cookie) = await SignedInAsync(ModbotPermissions.Administrator, ct);
        await using var _ = host;

        var response = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, ApiTestHost.TwoFlagProbe, cookie), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnUnguardedEndpoint_StaysOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);

        var response = await host.Client.GetAsync(ApiTestHost.OpenProbe, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
