using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Api.Features.Companion.Pair;
using Modbot.Api.Features.Companion.PairingCodes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.RateLimiting;

namespace Modbot.Api.Tests.Features.Companion;

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
        new(code, "2026.9.0", "windows");

    private sealed class RecordingDelay : IDelayScheduler
    {
        public List<TimeSpan> Waits { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken ct = default)
        {
            Waits.Add(delay);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A host with a group, and a code made by an account that holds "Pair a companion" -- a code
    /// from anybody else is refused when it is used.
    /// </summary>
    private async Task<(CompanionApiTestHost Host, string Code)> ReadyAsync(
        CancellationToken ct, Action<IServiceCollection>? configure = null)
    {
        await CompanionApiTestHost.ResetAsync(_db, ct);
        var host = await CompanionApiTestHost.StartAsync(_db, configure);
        await host.ConfigureGroupAsync(_db, Group, ct);

        var owner = await host.CreateOwnerAsync(ModbotPermissions.PairCompanion, ct);
        var code = DeviceTokens.NewPairingCode();
        await host.Devices.IssueCodeAsync(
            PairingCodeLifetime.Issue(host.Clock, owner.Id, code), ct);

        return (host, code);
    }

    [Fact]
    public async Task TradesACodeForATokenAndTheManagedGroup()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        var response = await host.Client.PostAsJsonAsync("/api/v1/companion/pair", Request(code), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paired = await response.Content.ReadFromJsonAsync<PairResponse>(ct);
        Assert.NotNull(paired);
        Assert.False(string.IsNullOrWhiteSpace(paired.DeviceToken));

        // The group comes back so the client can route locally and never has to ask a server
        // which group an instance belongs to -- asking is itself the cross-group leak.
        Assert.Equal(Group, paired.ManagedGroupId);
        Assert.False(string.IsNullOrWhiteSpace(paired.ManagedGroupName));
        Assert.Equal(host.Clock.UtcNow, paired.ServerTime);
    }

    [Fact]
    public async Task TheTokenItReturnsActuallyWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        var paired = await (await host.Client.PostAsJsonAsync("/api/v1/companion/pair", Request(code), ct))
            .Content.ReadFromJsonAsync<PairResponse>(ct);

        var response = await host.Client.SendAsync(
            host.WithToken(HttpMethod.Get, "/api/v1/companion/context?instanceId=39911", paired!.DeviceToken), ct);

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
            (await host.Client.PostAsJsonAsync("/api/v1/companion/pair", Request(code), ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.Client.PostAsJsonAsync("/api/v1/companion/pair", Request(code), ct)).StatusCode);
    }

    [Fact]
    public async Task ACodeExpires()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        host.Clock.Advance(PairingCodeLifetime.Default + TimeSpan.FromSeconds(1));

        var response = await host.Client.PostAsJsonAsync("/api/v1/companion/pair", Request(code), ct);

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

        var response = await host.Client.PostAsJsonAsync("/api/v1/companion/pair", Request(wrong), ct);
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
        await CompanionApiTestHost.ResetAsync(_db, ct);
        var host = await CompanionApiTestHost.StartAsync(_db);
        await using var _ = host;

        await host.ConfigureGroupAsync(_db, Group, ct);
        var owner = await host.CreateOwnerAsync(ModbotPermissions.PairCompanion, ct);
        await host.Devices.IssueCodeAsync(
            PairingCodeLifetime.Issue(host.Clock, owner.Id, "AB12-CD34"), ct);

        var response = await host.Client.PostAsJsonAsync("/api/v1/companion/pair", Request(variation), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ADeploymentWithNoManagedGroupSaysSoRatherThanPairing()
    {
        // 503, so the client backs off and tries later: the operator is mid-setup, and the
        // address is not wrong.
        var ct = TestContext.Current.CancellationToken;
        await CompanionApiTestHost.ResetAsync(_db, ct);
        var host = await CompanionApiTestHost.StartAsync(_db);
        await using var _ = host;

        await host.ConfigureGroupAsync(_db, groupId: null!, ct);

        var code = DeviceTokens.NewPairingCode();
        await host.Devices.IssueCodeAsync(PairingCodeLifetime.Issue(host.Clock, Guid.NewGuid(), code), ct);

        var response = await host.Client.PostAsJsonAsync("/api/v1/companion/pair", Request(code), ct);

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

        var response = await host.Client.PostAsJsonAsync("/api/v99/companion/pair", Request(code), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("api_version_unsupported", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTokenIsNeverStoredInAFormAnybodyCanReadBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        var paired = await (await host.Client.PostAsJsonAsync("/api/v1/companion/pair", Request(code), ct))
            .Content.ReadFromJsonAsync<PairResponse>(ct);

        var stored = Assert.Single(await host.Devices.ListDevicesAsync(ct));

        Assert.NotEqual(paired!.DeviceToken, stored.TokenHash);
        Assert.Equal(DeviceTokens.Hash(paired.DeviceToken), stored.TokenHash);
    }

    [Fact]
    public async Task APlatformStringBuiltToHideInASettingsListIsCleanedRatherThanStored()
    {
        // A right-to-left override in a value the settings list shows makes one row render as
        // another, which is how a compromised client hides behind a colleague's entry when
        // somebody goes looking for which one to revoke.
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        await host.Client.PostAsJsonAsync(
            "/api/v1/companion/pair",
            new PairRequest(code, "2026.9.0", "win dows\r\n‮"),
            ct);

        var stored = Assert.Single(await host.Devices.ListDevicesAsync(ct));

        Assert.Equal("win dows", stored.Platform);
        Assert.DoesNotContain('\n', stored.Platform);
        Assert.True(stored.Platform.Length <= PairHandler.MaxFieldLength);
    }

    [Fact]
    public async Task ADeviceNameSentByAnOlderClientIsIgnoredNotRefused()
    {
        // Device names were dropped: a moderator's own label for their machine told an operator
        // nothing they could act on. A client built before that still sends one, and must still
        // pair -- the field is simply not read.
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        var response = await host.Client.PostAsJsonAsync(
            "/api/v1/companion/pair",
            new { code, deviceName = "Rin's desktop", companionVersion = "2026.8.0", platform = "windows" },
            ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ACodeFromAnAccountWithoutThePermissionIsRefusedLikeABadCode()
    {
        // The code was made while its account could pair, and the role changed before it was used.
        // No device is made for somebody who could not make the code now.
        var ct = TestContext.Current.CancellationToken;
        await CompanionApiTestHost.ResetAsync(_db, ct);
        var host = await CompanionApiTestHost.StartAsync(_db);
        await using var _ = host;
        await host.ConfigureGroupAsync(_db, Group, ct);

        var owner = await host.CreateOwnerAsync(ModbotPermissions.ViewMembers, ct);
        var code = DeviceTokens.NewPairingCode();
        await host.Devices.IssueCodeAsync(PairingCodeLifetime.Issue(host.Clock, owner.Id, code), ct);

        var response = await host.Client.PostAsJsonAsync("/api/v1/companion/pair", Request(code), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("pairing_code", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Empty(await host.Devices.ListDevicesAsync(ct));
    }

    [Fact]
    public async Task ACodeFromADisabledAccountIsRefusedLikeABadCode()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, code) = await ReadyAsync(ct);
        await using var _ = host;

        await using (var db = host.Database)
        {
            var codeOwner = db.CompanionPairingCodes.Single(c => c.CodeHash == DeviceTokens.Hash(code)).IssuedToUserId;
            db.Users.Single(u => u.Id == codeOwner).IsDisabled = true;
            await db.SaveChangesAsync(ct);
        }

        var response = await host.Client.PostAsJsonAsync("/api/v1/companion/pair", Request(code), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RepeatedWrongCodesFromOneAddressAreSlowed_ButARightCodeStillPairs()
    {
        // A slowdown, not a lockout: each wrong code doubles the wait before the next attempt is
        // checked, and a right code after the wait still pairs.
        var ct = TestContext.Current.CancellationToken;
        var delay = new RecordingDelay();
        var (host, code) = await ReadyAsync(ct, s => s.AddSingleton<IDelayScheduler>(delay));
        await using var _ = host;

        for (var i = 0; i < 3; i++)
            await host.Client.PostAsJsonAsync("/api/v1/companion/pair", Request("ZZZZ-ZZZZ"), ct);

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delay.Waits);

        var right = await host.Client.PostAsJsonAsync("/api/v1/companion/pair", Request(code), ct);

        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(4), delay.Waits[^1]);
    }

    // ── The staff side: making codes, the list, removing (signed in, in a browser) ──────────────

    private static async Task<(string Token, Guid Id)> PairToAsync(ApiTestHost host, Guid ownerId, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var store = new DatabaseCompanionDeviceStore(scope.ServiceProvider.GetRequiredService<ModbotContext>());

        var token = DeviceTokens.NewToken();
        var id = Guid.NewGuid();
        await store.AddDeviceAsync(
            new CompanionDevice(id, DeviceTokens.Hash(token), "2026.9.0", "windows", ownerId, host.Clock.UtcNow), ct);

        return (token, id);
    }

    private static async Task<List<PairedDevice>> ListAsync(ApiTestHost host, string cookie, CancellationToken ct)
    {
        var response = await host.SendJsonAsync(HttpMethod.Get, "/api/companion-devices", null, cookie, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<PairedDevice>>(ct))!;
    }

    [Fact]
    public async Task MakingACode_NeedsPairACompanion()
    {
        // Until the permission existed, an account holding nothing at all could pair, and the
        // device it got could read who is flagged in every instance.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db, companion: true);

        var (_, nothing) = await host.SignedInAsync(ModbotPermissions.None, ct);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewMembers, ct);
        var (_, pairer) = await host.SignedInAsync(ModbotPermissions.PairCompanion, ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(
            HttpMethod.Post, "/api/companion-devices/pairing-code", null, nothing, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(
            HttpMethod.Post, "/api/companion-devices/pairing-code", null, viewer, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(
            HttpMethod.Post, "/api/companion-devices/pairing-code", null, pairer, ct)).StatusCode);
    }

    [Fact]
    public async Task TheList_ShowsYourOwnDevices_AndEveryonesWithManageUsers()
    {
        var ct = TestContext.Current.CancellationToken;
        await CompanionApiTestHost.ResetAsync(_db, ct);
        await using var host = await ApiTestHost.StartAsync(_db, companion: true);

        var (moderator, moderatorCookie) = await host.SignedInAsync(ModbotPermissions.PairCompanion, ct);
        var (colleague, _) = await host.SignedInAsync(ModbotPermissions.PairCompanion, ct);
        var (_, managerCookie) = await host.SignedInAsync(ModbotPermissions.ManageUsers, ct);

        var (_, mine) = await PairToAsync(host, moderator.Id, ct);
        var (_, theirs) = await PairToAsync(host, colleague.Id, ct);

        var own = await ListAsync(host, moderatorCookie, ct);
        var row = Assert.Single(own);
        Assert.Equal(mine, row.Id);
        Assert.Equal(moderator.Username, row.OwnerName);

        var everyone = await ListAsync(host, managerCookie, ct);
        Assert.Contains(everyone, d => d.Id == mine && d.OwnerName == moderator.Username);
        Assert.Contains(everyone, d => d.Id == theirs && d.OwnerName == colleague.Username);
    }

    [Fact]
    public async Task RemovingSomebodyElsesDevice_NeedsManageUsers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db, companion: true);

        var (owner, ownerCookie) = await host.SignedInAsync(ModbotPermissions.PairCompanion, ct);
        var (_, colleagueCookie) = await host.SignedInAsync(ModbotPermissions.PairCompanion, ct);

        // One permission more than the owner: the same number is the same rank, and a manager
        // only reaches the companions of people below them (accounts and access design §3.5).
        var (_, managerCookie) = await host.SignedInAsync(ModbotPermissions.ManageUsers | ModbotPermissions.ViewMembers, ct);

        var (_, first) = await PairToAsync(host, owner.Id, ct);
        var (_, second) = await PairToAsync(host, owner.Id, ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(
            HttpMethod.Delete, $"/api/companion-devices/{first}", null, colleagueCookie, ct)).StatusCode);

        // The owner removes their own; a manager removes anybody's.
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(
            HttpMethod.Delete, $"/api/companion-devices/{first}", null, ownerCookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(
            HttpMethod.Delete, $"/api/companion-devices/{second}", null, managerCookie, ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await host.SendJsonAsync(
            HttpMethod.Delete, $"/api/companion-devices/{Guid.NewGuid()}", null, managerCookie, ct)).StatusCode);
    }

    [Fact]
    public async Task AManager_RemovesOnlyTheDevicesOfPeopleBelowThem_AndTheirOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db, companion: true);

        // Three permissions: above the one-permission owner, the same as the peer, below the four
        // of the other (accounts and access design §3.5).
        var manager = ModbotPermissions.ManageUsers | ModbotPermissions.PairCompanion | ModbotPermissions.ViewMembers;
        var (managerUser, managerCookie) = await host.SignedInAsync(manager, ct);
        var below = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.PairCompanion, ct);
        var peer = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, manager, ct);
        var above = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, manager | ModbotPermissions.ViewProfile, ct);
        var administrator = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.Administrator, ct);

        var sentence = "You can only change accounts below your highest role.";

        foreach (var owner in new[] { peer, above, administrator })
        {
            var (_, device) = await PairToAsync(host, owner.Id, ct);
            var refused = await host.SendJsonAsync(HttpMethod.Delete, $"/api/companion-devices/{device}", null, managerCookie, ct);

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains(sentence, await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        var (_, belowDevice) = await PairToAsync(host, below.Id, ct);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(
            HttpMethod.Delete, $"/api/companion-devices/{belowDevice}", null, managerCookie, ct)).StatusCode);

        var (_, ownDevice) = await PairToAsync(host, managerUser.Id, ct);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(
            HttpMethod.Delete, $"/api/companion-devices/{ownDevice}", null, managerCookie, ct)).StatusCode);

        // The list tells the page whose rank each owner has, so it can grey Remove.
        var list = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/companion-devices", null, managerCookie, ct), ct);
        var ranks = list.EnumerateArray().ToDictionary(
            d => d.GetProperty("ownerId").GetGuid(), d => d.GetProperty("ownerRank").GetInt32());
        Assert.Equal(TestAccounts.PositionFor(manager | ModbotPermissions.ViewProfile), ranks[above.Id]);
    }

    [Fact]
    public async Task DisablingAnAccount_RevokesItsCompanions_AndEnablingItDoesNotBringThemBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db, companion: true);

        // An administrator, because an account cannot be disabled while no enabled administrator would
        // be left, and this test's other account is not one.
        var (_, managerCookie) = await host.SignedInAsync(ModbotPermissions.Administrator, ct);
        var (moderator, _) = await host.SignedInAsync(ModbotPermissions.PairCompanion, ct);
        var (token, deviceId) = await PairToAsync(host, moderator.Id, ct);

        var alerts = new HttpRequestMessage(HttpMethod.Get, "/api/v1/companion/alerts?wait=1");
        alerts.Headers.Add("Authorization", $"Bearer {token}");
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(alerts, ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(
            HttpMethod.Post, $"/api/users/{moderator.Id}/disable", null, managerCookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(
            HttpMethod.Post, $"/api/users/{moderator.Id}/enable", null, managerCookie, ct)).StatusCode);

        using (var scope = host.Services.CreateScope())
        {
            var store = new DatabaseCompanionDeviceStore(scope.ServiceProvider.GetRequiredService<ModbotContext>());
            Assert.NotNull((await store.FindByIdAsync(deviceId, ct))!.RevokedAt);
        }

        var after = new HttpRequestMessage(HttpMethod.Get, "/api/v1/companion/alerts?wait=1");
        after.Headers.Add("Authorization", $"Bearer {token}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(after, ct)).StatusCode);
    }
}
