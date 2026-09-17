using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.ApiKeys;

/// <summary>
/// API keys (API keys design §3): stored as hashes, capped by their account on creation and on
/// every request, accepted by existing endpoints with exactly their permissions, and refused on
/// the endpoints that belong to a person.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ApiKeyTests
{
    private const string Path = "/api/api-keys";

    private readonly PostgresFixture _db;

    public ApiKeyTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(Guid Id, string Key)> CreateKeyAsync(
        ApiTestHost host, string cookie, string[] permissions, DateTimeOffset? expiresAt = null)
    {
        var response = await host.SendJsonAsync(
            HttpMethod.Post, Path, new { name = "Bot", permissions, expiresAt }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);
        return (body.GetProperty("apiKey").GetProperty("id").GetGuid(), body.GetProperty("key").GetString()!);
    }

    private static Task<HttpResponseMessage> WithKeyAsync(ApiTestHost host, HttpMethod method, string path, string key, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        if (body is not null)
        {
            request.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        }

        return host.Client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task ACreatedKey_IsShownOnce_AndOnlyItsHashIsStored()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);

        var (id, key) = await CreateKeyAsync(host, cookie, ["ViewAuditLog"]);

        Assert.StartsWith(ApiKeySecrets.Prefix, key, StringComparison.Ordinal);

        await using var db = _db.NewContext();
        var row = await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == id, Ct);
        Assert.Equal(ApiKeySecrets.Hash(key), row.KeyHash);
        Assert.Equal(key[..ApiKeySecrets.StartLength], row.Start);
        Assert.Equal(user.Id, row.CreatedByUserId);
        Assert.Equal(ModbotPermissions.ViewAuditLog, row.Permissions);

        var list = await (await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(key, list, StringComparison.Ordinal);
        Assert.Contains(row.Start, list, StringComparison.Ordinal);

        var fact = Assert.Single(await host.FactsAsync(FactType.ApiKeyCreated, id.ToString(), Ct));
        Assert.Equal(user.Id.ToString(), fact.ActorId);
        Assert.DoesNotContain(key, fact.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(row.KeyHash, fact.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKey_OpensExistingEndpoints_WithExactlyItsPermissions()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(
            ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog | ModbotPermissions.ManageSettings, Ct);

        var (_, auditOnly) = await CreateKeyAsync(host, cookie, ["ViewAuditLog"]);

        Assert.Equal(HttpStatusCode.OK, (await WithKeyAsync(host, HttpMethod.Get, ApiTestHost.AuditProbe, auditOnly)).StatusCode);

        // The account holds ManageSettings; the key was not given it.
        Assert.Equal(HttpStatusCode.Forbidden, (await WithKeyAsync(host, HttpMethod.Get, ApiTestHost.TwoFlagProbe, auditOnly)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await WithKeyAsync(host, HttpMethod.Get, "/api/settings/ai", auditOnly)).StatusCode);

        // Nor ManageApiKeys: a key cannot manage keys unless it was given that.
        Assert.Equal(HttpStatusCode.Forbidden, (await WithKeyAsync(host, HttpMethod.Get, Path, auditOnly)).StatusCode);

        // A real endpoint with hand-written permission logic sees the key's permissions too.
        Assert.Equal(HttpStatusCode.OK, (await WithKeyAsync(host, HttpMethod.Get, "/api/audit/", auditOnly)).StatusCode);
    }

    [Fact]
    public async Task AnUnknownKey_AndNoKey_Are401()
    {
        await using var host = await ApiTestHost.StartAsync(_db);

        var unknown = await WithKeyAsync(host, HttpMethod.Get, ApiTestHost.AuditProbe, ApiKeySecrets.NewKey());
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal("Bearer", unknown.Headers.WwwAuthenticate.ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(ApiTestHost.AuditProbe, Ct)).StatusCode);
    }

    [Fact]
    public async Task AKeyCannotHoldAPermissionItsCreatorLacks()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);

        foreach (var wanted in new[] { "ManageSettings", "Administrator", "ManageUsers" })
        {
            var response = await host.SendJsonAsync(
                HttpMethod.Post, Path, new { name = "Too much", permissions = new[] { wanted } }, cookie, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task ADemotedCreator_TakesTheKeysPermissionWithThem_OnTheNextRequest()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (_, key) = await CreateKeyAsync(host, cookie, ["ViewAuditLog"]);

        Assert.Equal(HttpStatusCode.OK, (await WithKeyAsync(host, HttpMethod.Get, ApiTestHost.AuditProbe, key)).StatusCode);

        await using (var db = _db.NewContext())
        {
            var lesser = await TestAccounts.RoleForAsync(db, ModbotPermissions.ManageApiKeys, Ct);
            await db.UserRoles.Where(ur => ur.UserId == user.Id).ExecuteDeleteAsync(Ct);
            db.UserRoles.Add(new ModbotUserRole { UserId = user.Id, RoleId = lesser });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await WithKeyAsync(host, HttpMethod.Get, ApiTestHost.AuditProbe, key)).StatusCode);
    }

    [Fact]
    public void AnAdministratorKey_UnderANonAdministrator_MeansWhatTheAccountHolds()
    {
        Assert.Equal(
            ModbotPermissions.ViewAuditLog,
            ApiKeyPermissions.Cap(ModbotPermissions.Administrator, ModbotPermissions.ViewAuditLog));

        Assert.Equal(
            ModbotPermissions.ViewAuditLog | ModbotPermissions.Ban,
            ApiKeyPermissions.Cap(ModbotPermissions.ViewAuditLog | ModbotPermissions.Ban, ModbotPermissions.Administrator));

        Assert.Equal(
            ModbotPermissions.ViewAuditLog,
            ApiKeyPermissions.Cap(ModbotPermissions.ViewAuditLog | ModbotPermissions.Ban, ModbotPermissions.ViewAuditLog | ModbotPermissions.Kick));
    }

    [Fact]
    public async Task ARevokedKey_StopsWorking_AndRevokingIsRecordedOnce()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (id, key) = await CreateKeyAsync(host, cookie, ["ViewAuditLog"]);

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/{id}", null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/{id}", null, cookie, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await WithKeyAsync(host, HttpMethod.Get, ApiTestHost.AuditProbe, key)).StatusCode);
        Assert.Single(await host.FactsAsync(FactType.ApiKeyRevoked, id.ToString(), Ct));

        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        var view = list.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("id").GetGuid() == id);
        Assert.Equal("revoked", view.GetProperty("state").GetString());
    }

    [Fact]
    public async Task AnExpiredKey_StopsWorking()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (_, key) = await CreateKeyAsync(host, cookie, ["ViewAuditLog"], host.Clock.UtcNow.AddDays(1));

        Assert.Equal(HttpStatusCode.OK, (await WithKeyAsync(host, HttpMethod.Get, ApiTestHost.AuditProbe, key)).StatusCode);

        host.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await WithKeyAsync(host, HttpMethod.Get, ApiTestHost.AuditProbe, key)).StatusCode);

        var past = await host.SendJsonAsync(
            HttpMethod.Post, Path, new { name = "Old", permissions = new[] { "ViewAuditLog" }, expiresAt = host.Clock.UtcNow.AddMinutes(-1) }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
    }

    [Fact]
    public async Task ADisabledCreatorsKey_StopsWorking()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (_, key) = await CreateKeyAsync(host, cookie, ["ViewAuditLog"]);

        await using (var db = _db.NewContext())
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDisabled, true), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await WithKeyAsync(host, HttpMethod.Get, ApiTestHost.AuditProbe, key)).StatusCode);
    }

    [Fact]
    public async Task AKey_IsRefusedOnAPersonsOwnAccountEndpoints_ButMayAskWhoItBelongsTo()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (_, key) = await CreateKeyAsync(host, cookie, ["Administrator"]);

        var password = await WithKeyAsync(host, HttpMethod.Put, "/api/auth/password", key,
            new { currentPassword = TestAccounts.Password, newPassword = "a-new-long-password", confirmPassword = "a-new-long-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, password.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await WithKeyAsync(host, HttpMethod.Post, "/api/auth/sign-out-everywhere", key)).StatusCode);

        var me = await WithKeyAsync(host, HttpMethod.Get, "/api/auth/me", key);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(user.Username, (await ApiTestHost.BodyOf(me, Ct)).GetProperty("username").GetString());

        // The password was not changed: the old one still signs in.
        await host.LoginAsync(user.Username, TestAccounts.Password, Ct);
    }

    [Theory]
    [InlineData("GET", "/api/auth/me", false)]
    [InlineData("PUT", "/api/auth/me", true)]
    [InlineData("PUT", "/api/auth/password", true)]
    [InlineData("POST", "/api/auth/logout", true)]
    [InlineData("POST", "/api/auth/vrchat-link/start", true)]
    [InlineData("POST", "/api/onboarding/group", true)]
    [InlineData("POST", "/api/companion-devices/pairing-code", true)]
    [InlineData("GET", "/api/companion-devices", false)]
    [InlineData("POST", "/api/v1/companion/events", true)]
    [InlineData("GET", "/api/version", false)]
    [InlineData("GET", "/api/audit", false)]
    [InlineData("DELETE", "/api/api-keys/x", false)]
    public void ThePlacesAKeyMayNotGo(string method, string path, bool refused)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(refused, ApiKeyAuthentication.KeysMayNotUse(context.Request));
    }

    [Fact]
    public async Task ManagingKeys_NeedsThePermission()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(
            HttpMethod.Post, Path, new { name = "x", permissions = new[] { "ViewAuditLog" } }, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/{Guid.NewGuid()}", null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendJsonAsync(HttpMethod.Get, Path, null, null, Ct)).StatusCode);
    }

    [Fact]
    public async Task LastUsed_IsWrittenAtMostOnceAMinute()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog, Ct);
        var (id, key) = await CreateKeyAsync(host, cookie, ["ViewAuditLog"]);

        async Task<DateTimeOffset?> LastUsedAsync()
        {
            await using var db = _db.NewContext();
            return await db.ApiKeys.AsNoTracking().Where(k => k.Id == id).Select(k => k.LastUsedAt).SingleAsync(Ct);
        }

        Assert.Null(await LastUsedAsync());

        var first = host.Clock.UtcNow;
        await WithKeyAsync(host, HttpMethod.Get, ApiTestHost.AuditProbe, key);
        Assert.Equal(first, await LastUsedAsync());

        host.Clock.Advance(TimeSpan.FromSeconds(30));
        await WithKeyAsync(host, HttpMethod.Get, ApiTestHost.AuditProbe, key);
        Assert.Equal(first, await LastUsedAsync());

        host.Clock.Advance(TimeSpan.FromSeconds(31));
        await WithKeyAsync(host, HttpMethod.Get, ApiTestHost.AuditProbe, key);
        Assert.Equal(host.Clock.UtcNow, await LastUsedAsync());
    }

    [Fact]
    public async Task AKeyWithManageApiKeys_MakesKeysOwnedByTheSameAccount_CappedByItself()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(
            ModbotPermissions.ManageApiKeys | ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewOperationalLog, Ct);
        var (_, manager) = await CreateKeyAsync(host, cookie, ["ManageApiKeys", "ViewAuditLog"]);

        var refused = await WithKeyAsync(host, HttpMethod.Post, Path, manager, new { name = "wider", permissions = new[] { "ViewOperationalLog" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var made = await WithKeyAsync(host, HttpMethod.Post, Path, manager, new { name = "narrow", permissions = new[] { "ViewAuditLog" } });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var body = await ApiTestHost.BodyOf(made, Ct);
        Assert.Equal(user.Id, body.GetProperty("apiKey").GetProperty("ownerId").GetGuid());
    }
}
