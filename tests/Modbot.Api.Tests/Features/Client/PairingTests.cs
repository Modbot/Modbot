using System.Net;
using System.Net.Http.Json;
using Modbot.Api.Features.Client.Devices;
using Modbot.Api.Features.Client.Pair;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Client;

/// <summary>
/// Pairing is the only way a client ever gets a credential, so the interesting cases are all the
/// ways it must refuse: a code that has expired, one already spent, one that never existed, and a
/// deployment that has nothing to pair against yet.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PairingTests
{
    private readonly PostgresFixture _db;

    public PairingTests(PostgresFixture db) => _db = db;

    private const string Group = "grp_cats";

    private static PairRequest Request(string code) =>
        new(code, "Rin's desktop", "2026.9.0", "windows");

    private async Task<(ClientApiTestHost Host, string Code)> ReadyAsync(CancellationToken ct)
    {
        await ClientApiTestHost.ResetAsync(_db, ct);
        var host = await ClientApiTestHost.StartAsync(_db);
        await host.ConfigureGroupAsync(_db, Group, ct);

        var code = DeviceTokens.NewPairingCode();
        await host.Devices.IssueCodeAsync(
            PairingCodeLifetime.Issue(host.Clock, Guid.NewGuid(), code), ct);

        return (host, code);
    }

    [Fact]
    public async Task TradesACodeForATokenAndTheManagedGroup()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        var response = await host.Client.PostAsJsonAsync("/api/v1/client/pair", Request(code), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paired = await response.Content.ReadFromJsonAsync<PairResponse>(ct);
        Assert.NotNull(paired);
        Assert.False(string.IsNullOrWhiteSpace(paired.DeviceToken));

        // The group comes back so the client can route locally and never has to ask a server
        // which group an instance belongs to -- asking is itself the cross-group leak.
        Assert.Equal(Group, paired.ManagedGroupId);
        Assert.Equal(host.Clock.UtcNow, paired.ServerTime);
    }

    [Fact]
    public async Task TheTokenItReturnsActuallyWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        var paired = await (await host.Client.PostAsJsonAsync("/api/v1/client/pair", Request(code), ct))
            .Content.ReadFromJsonAsync<PairResponse>(ct);

        var response = await host.Client.SendAsync(
            host.WithToken(HttpMethod.Get, "/api/v1/client/context?instanceId=39911", paired!.DeviceToken), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ACodeIsSingleUse()
    {
        // The whole reason a short code is safe: it is worth one pairing, once. A code that could
        // be replayed would be worth guessing at thirty-eight bits.
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        Assert.Equal(
            HttpStatusCode.OK,
            (await host.Client.PostAsJsonAsync("/api/v1/client/pair", Request(code), ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.Client.PostAsJsonAsync("/api/v1/client/pair", Request(code), ct)).StatusCode);
    }

    [Fact]
    public async Task ACodeExpires()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        host.Clock.Advance(PairingCodeLifetime.Default + TimeSpan.FromSeconds(1));

        var response = await host.Client.PostAsJsonAsync("/api/v1/client/pair", Request(code), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("ZZZZ-ZZZZ")]
    [InlineData("")]
    [InlineData("nonsense")]
    public async Task AWrongCodeIsRefusedTheSameWayAsAnExpiredOne(string wrong)
    {
        // Expired, already used and never existed all answer identically. Telling them apart would
        // help only somebody guessing.
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await ReadyAsync(ct);
        await using var __ = host;

        var response = await host.Client.PostAsJsonAsync("/api/v1/client/pair", Request(wrong), ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("pairing_code", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ab12cd34")]
    [InlineData("AB12 CD34")]
    [InlineData(" ab12-cd34 ")]
    public async Task TranscriptionSlipsAreNotFailures(string variation)
    {
        // The code is for a person to read off a screen and type. Case, spacing and a missing
        // dash are transcription, not authentication.
        var ct = TestContext.Current.CancellationToken;
        await ClientApiTestHost.ResetAsync(_db, ct);
        var host = await ClientApiTestHost.StartAsync(_db);
        await using var _ = host;

        await host.ConfigureGroupAsync(_db, Group, ct);
        await host.Devices.IssueCodeAsync(
            PairingCodeLifetime.Issue(host.Clock, Guid.NewGuid(), "AB12-CD34"), ct);

        var response = await host.Client.PostAsJsonAsync("/api/v1/client/pair", Request(variation), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ADeploymentWithNoManagedGroupSaysSoRatherThanPairing()
    {
        // 503, so the client backs off and tries later: the operator is mid-setup, and the
        // address is not wrong.
        var ct = TestContext.Current.CancellationToken;
        await ClientApiTestHost.ResetAsync(_db, ct);
        var host = await ClientApiTestHost.StartAsync(_db);
        await using var _ = host;

        await host.ConfigureGroupAsync(_db, groupId: null!, ct);

        var code = DeviceTokens.NewPairingCode();
        await host.Devices.IssueCodeAsync(PairingCodeLifetime.Issue(host.Clock, Guid.NewGuid(), code), ct);

        var response = await host.Client.PostAsJsonAsync("/api/v1/client/pair", Request(code), ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task AnApiVersionThisServerDoesNotSpeakIsA409SoTheClientRenegotiates()
    {
        // Not a 400, which the client would treat as permanent, and not a 5xx, which it would
        // treat as transient and retry forever. 409 means "renegotiate".
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        var response = await host.Client.PostAsJsonAsync("/api/v99/client/pair", Request(code), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("api_version_unsupported", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTokenIsNeverStoredInAFormAnybodyCanReadBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        var paired = await (await host.Client.PostAsJsonAsync("/api/v1/client/pair", Request(code), ct))
            .Content.ReadFromJsonAsync<PairResponse>(ct);

        var stored = Assert.Single(await host.Devices.ListDevicesAsync(ct));

        Assert.NotEqual(paired!.DeviceToken, stored.TokenHash);
        Assert.Equal(DeviceTokens.Hash(paired.DeviceToken), stored.TokenHash);
    }

    [Fact]
    public async Task ADeviceNameBuiltToHideInASettingsListIsCleanedRatherThanStored()
    {
        // A right-to-left override in a device name makes one device render as another, which is
        // how a compromised client hides behind a colleague's entry when somebody goes looking
        // for which one to revoke.
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        await host.Client.PostAsJsonAsync(
            "/api/v1/client/pair",
            new PairRequest(code, "desk top\r\n‮", "2026.9.0", "windows"),
            ct);

        var stored = Assert.Single(await host.Devices.ListDevicesAsync(ct));

        Assert.Equal("desk top", stored.DeviceName);
        Assert.DoesNotContain('\n', stored.DeviceName);
        Assert.True(stored.DeviceName.Length <= PairHandler.MaxDeviceNameLength);
    }
}
