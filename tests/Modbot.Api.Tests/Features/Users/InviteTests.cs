using System.Net;
using System.Text.Json;
using Modbot.Api.Features.Users;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Users;

/// <summary>Invite links (accounts and access design §4.1): once, for 72 hours, while the inviter stands.</summary>
[Collection(nameof(PostgresCollection))]
public class InviteTests
{
    private readonly PostgresFixture _db;

    public InviteTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueName() => $"u_{Guid.NewGuid():N}";

    /// <summary>Every account gets one, and no two share one (server info and account email design §4).</summary>
    private static string UniqueEmail() => $"u_{Guid.NewGuid():N}@example.com";

    private static async Task<(Guid Id, string Path)> CreateInviteAsync(
        ApiTestHost host, string cookie, params Guid[] roleIds)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/invites", new { roleIds }, cookie, Ct);
        response.EnsureSuccessStatusCode();
        var body = await ApiTestHost.BodyOf(response, Ct);
        return (body.GetProperty("id").GetGuid(), body.GetProperty("path").GetString()!);
    }

    private static string ApiPath(string path) => "/api" + path;

    [Fact]
    public async Task AnInvite_CanBeUsedOnce_AndSignsTheNewPersonIn()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (inviter, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (id, path) = await CreateInviteAsync(host, cookie, BuiltInRoles.ModeratorId);

        var described = await ApiTestHost.BodyOf(await host.Client.GetAsync(ApiPath(path), Ct), Ct);
        Assert.True(described.GetProperty("usable").GetBoolean());
        Assert.Equal(inviter.Username, described.GetProperty("invitedBy").GetString());
        Assert.Equal(["Moderator"], described.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        var name = UniqueName();
        var accepted = await host.SendJsonAsync(
            HttpMethod.Post, ApiPath(path),
            new
            {
                username = name,
                password = "a-long-enough-password",
                confirmPassword = "a-long-enough-password",
                email = UniqueEmail(),
            },
            null, Ct);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var session = ApiTestHost.SessionCookie(accepted);
        var me = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, session, Ct), Ct);
        Assert.Equal(name, me.GetProperty("username").GetString());
        Assert.Equal(["Moderator"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        // Once. The second person to open it gets nothing.
        var again = await host.SendJsonAsync(
            HttpMethod.Post, ApiPath(path),
            new { username = UniqueName(), password = "a-long-enough-password", email = UniqueEmail() },
            null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        var newId = me.GetProperty("id").GetString()!;
        var created = Assert.Single(await host.FactsAsync(FactType.UserCreated, newId, Ct));
        Assert.Equal(inviter.Id.ToString(), created.ActorId);
        Assert.Equal("invite", ApiTestHost.DataOf(created).GetProperty("how").GetString());
        Assert.Single(await host.FactsAsync(FactType.UserInviteUsed, newId, Ct));
        Assert.Single(await host.FactsAsync(FactType.UserInvited, id.ToString(), Ct));
    }

    [Fact]
    public async Task ANewAccount_IsSignedInButBlockedUntilItLinksItsVRChatAccount()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (_, path) = await CreateInviteAsync(host, cookie, BuiltInRoles.ViewerId);

        var accepted = await host.SendJsonAsync(
            HttpMethod.Post, ApiPath(path),
            new { username = UniqueName(), password = "a-long-enough-password", email = UniqueEmail() },
            null, Ct);
        var session = ApiTestHost.SessionCookie(accepted);

        var me = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, session, Ct), Ct);
        Assert.False(me.GetProperty("vrChatLinked").GetBoolean());

        // Viewer may read roles -- once linked. Until then, the door is shut (design §4.3).
        var roles = await host.SendJsonAsync(HttpMethod.Get, "/api/roles", null, session, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, roles.StatusCode);

        var link = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/vrchat-link", null, session, Ct);
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
    }

    [Fact]
    public async Task AnExpiredInvite_IsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (_, path) = await CreateInviteAsync(host, cookie, BuiltInRoles.ViewerId);

        host.Clock.Advance(OneTimeLinkService.InviteLifetime + TimeSpan.FromMinutes(1));

        var described = await ApiTestHost.BodyOf(await host.Client.GetAsync(ApiPath(path), Ct), Ct);
        Assert.False(described.GetProperty("usable").GetBoolean());
        Assert.Contains("expired", described.GetProperty("reason").GetString(), StringComparison.Ordinal);

        var accepted = await host.SendJsonAsync(
            HttpMethod.Post, ApiPath(path),
            new { username = UniqueName(), password = "a-long-enough-password", email = UniqueEmail() },
            null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, accepted.StatusCode);
    }

    [Fact]
    public async Task AnInviteFromAnAccountSinceDisabled_IsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        // Holds what Viewer grants as well, because an invite may only hand out what its maker has.
        var (inviter, inviterCookie) = await host.SignedInAsync(
            ModbotPermissions.ManageUsers | BuiltInRoles.ViewerPermissions, Ct);
        var (_, path) = await CreateInviteAsync(host, inviterCookie, BuiltInRoles.ViewerId);

        await host.SendJsonAsync(HttpMethod.Post, $"/api/users/{inviter.Id}/disable", null, admin, Ct);

        // The invite was that person's standing offer, and a disabled account makes no offers.
        var described = await ApiTestHost.BodyOf(await host.Client.GetAsync(ApiPath(path), Ct), Ct);
        Assert.False(described.GetProperty("usable").GetBoolean());
        Assert.Contains("disabled", described.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARevokedInvite_IsGone()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (id, path) = await CreateInviteAsync(host, cookie, BuiltInRoles.ViewerId);

        var pending = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/invites", null, cookie, Ct), Ct);
        Assert.Contains(pending.EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);

        var revoked = await host.SendJsonAsync(HttpMethod.Delete, $"/api/invites/{id}", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        var described = await ApiTestHost.BodyOf(await host.Client.GetAsync(ApiPath(path), Ct), Ct);
        Assert.False(described.GetProperty("usable").GetBoolean());

        Assert.Single(await host.FactsAsync(FactType.UserInviteRevoked, id.ToString(), Ct));
    }

    [Fact]
    public async Task AnUnknownToken_IsNotValid_AndSaysNothingMore()
    {
        await using var host = await ApiTestHost.StartAsync(_db);

        var described = await ApiTestHost.BodyOf(await host.Client.GetAsync("/api/join/not-a-real-token", Ct), Ct);

        Assert.False(described.GetProperty("usable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, described.GetProperty("invitedBy").ValueKind);
    }

    [Fact]
    public async Task TheLinkCarriesOnlyAPath_UntilAPublicAddressIsSaved()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/invites", new { roleIds = Array.Empty<Guid>() }, cookie, Ct);
        var body = await ApiTestHost.BodyOf(response, Ct);

        // The browser showing it knows its own address; the server does not guess one from the
        // request, because a request's host is chosen by whoever sent it.
        Assert.StartsWith("/join/", body.GetProperty("path").GetString(), StringComparison.Ordinal);
    }
}
