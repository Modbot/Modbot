using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Proxy;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Proxy;

namespace Modbot.Api.Tests.Features.Proxy;

/// <summary>
/// The VRChat proxy (VRChat proxy design): off is 404; a Modbot key in the header or in the
/// <c>auth</c> cookie goes out as the service account, with the permission; anything else in the
/// <c>auth</c> cookie goes out as the caller; nothing at all is 401; VRChat's answer comes back as
/// it was; and the switch has its settings endpoints.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class VRChatProxyTests
{
    private const string Path = "/api/proxy/vrchat/api/1/users/usr_test";
    private const string SettingsPath = "/api/settings/vrchat-proxy";

    private readonly PostgresFixture _db;

    public VRChatProxyTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The switch ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Off_TheRouteAnswers404_WhoeverAsks()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var anonymous = await host.Client.GetAsync(Path, Ct);
        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
        Assert.Equal(VRChatProxyEndpoints.SwitchedOff, (await ApiTestHost.BodyOf(anonymous, Ct)).GetProperty("error").GetString());

        var signedIn = await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct);
        Assert.Equal(HttpStatusCode.NotFound, signedIn.StatusCode);

        var theirs = await SendAsync(host, HttpMethod.Get, Path, cookie: "auth=authcookie_theirs");
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);

        Assert.Empty(host.VRChat.Forwarded);
    }

    [Fact]
    public async Task TheSettings_StartOff_AndTheSwitchIsRecorded()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var before = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, SettingsPath, null, cookie, Ct), Ct);
        Assert.False(before.GetProperty("enabled").GetBoolean());
        Assert.Equal("https://localhost/api/proxy/vrchat/", before.GetProperty("baseUrl").GetString());
        Assert.False(before.GetProperty("publicAddressSet").GetBoolean());

        // The whole settings resource, as the real client always sends it (VRChatProxySettingsUpdate
        // has no optional fields): leaving imagesProxied out would read as turning it off too, since
        // it defaults to true, and record a second, unwanted fact for that switch.
        var after = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Put, SettingsPath, new { enabled = true, imagesProxied = true }, cookie, Ct), Ct);
        Assert.True(after.GetProperty("enabled").GetBoolean());

        var fact = Assert.Single(await host.FactsAsync(FactType.SettingsChanged, "settings", Ct));
        Assert.Equal(user.Id.ToString(), fact.ActorId);
        Assert.Equal("vrchatProxyEnabled", ApiTestHost.DataOf(fact).GetProperty("setting").GetString());

        // Somebody without Change settings gets nothing here.
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, SettingsPath, null, viewer, Ct)).StatusCode);
    }

    // ── Who is calling ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoCredentialAtAll_Is401()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);

        var response = await host.Client.GetAsync(Path, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.Empty(host.VRChat.Forwarded);
    }

    [Fact]
    public async Task AKeyInTheHeader_GoesOutAsTheServiceAccount_WithThePermission()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var key = await KeyAsync(host, ModbotPermissions.UseVRChatProxy);

        var response = await SendAsync(host, HttpMethod.Get, Path + "?n=1", bearer: key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("service", response.Headers.GetValues(VRChatProxyEndpoints.AccountHeader).Single());

        var (endpoint, request, account) = Assert.Single(host.VRChat.Forwarded);
        Assert.Equal(VRChatProxyAccount.Service, account);
        Assert.Equal(VRChatEndpointClass.Proxy, endpoint.Class);
        Assert.Equal("api/1/users/usr_test", request.Path);
        Assert.Equal("?n=1", request.Query);
        Assert.Equal("GET", request.Method);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task AKeyInTheAuthCookie_IsTheSameAsAKeyInTheHeader()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var key = await KeyAsync(host, ModbotPermissions.UseVRChatProxy);

        var response = await SendAsync(host, HttpMethod.Get, Path, cookie: $"auth={key}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("service", response.Headers.GetValues(VRChatProxyEndpoints.AccountHeader).Single());
        Assert.Equal(VRChatProxyAccount.Service, Assert.Single(host.VRChat.Forwarded).Account);

        // Used, and so stamped, like a key in the header.
        await using var db = _db.NewContext();
        var row = await db.ApiKeys.AsNoTracking().SingleAsync(k => k.KeyHash == ApiKeySecrets.Hash(key), Ct);
        Assert.NotNull(row.LastUsedAt);
    }

    [Fact]
    public async Task AKeyWithoutThePermission_Is403_InTheHeaderOrTheCookie()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var key = await KeyAsync(host, ModbotPermissions.ViewAuditLog);

        var header = await SendAsync(host, HttpMethod.Get, Path, bearer: key);
        Assert.Equal(HttpStatusCode.Forbidden, header.StatusCode);
        Assert.Equal(VRChatProxyCallers.NeedsPermission, (await ApiTestHost.BodyOf(header, Ct)).GetProperty("error").GetString());

        var cookie = await SendAsync(host, HttpMethod.Get, Path, cookie: $"auth={key}");
        Assert.Equal(HttpStatusCode.Forbidden, cookie.StatusCode);

        Assert.Empty(host.VRChat.Forwarded);
    }

    [Fact]
    public async Task ARevokedOrUnknownKeyInTheCookie_Is401_NotAPassThrough()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);

        var unknown = ApiKeySecrets.NewKey();
        var response = await SendAsync(host, HttpMethod.Get, Path, cookie: $"auth={unknown}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.VRChat.Forwarded);
    }

    [Fact]
    public async Task ABadKeyInTheHeader_Is401_EvenWithSomebodysCookieBesideIt()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);

        var response = await SendAsync(host, HttpMethod.Get, Path, bearer: ApiKeySecrets.NewKey(), cookie: "auth=authcookie_theirs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.VRChat.Forwarded);
    }

    [Fact]
    public async Task SomebodysOwnVRChatCookie_GoesOutAsThem_UntouchedAndUnread()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);

        var response = await SendAsync(host, HttpMethod.Get, Path, cookie: "auth=authcookie_theirs; twoFactorAuth=abc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("own", response.Headers.GetValues(VRChatProxyEndpoints.AccountHeader).Single());

        var (endpoint, request, account) = Assert.Single(host.VRChat.Forwarded);
        Assert.Equal(VRChatProxyAccount.Caller, account);
        Assert.Equal(VRChatEndpointClass.ProxyPassthrough, endpoint.Class);

        // The cookie header reaches the gate as it was sent; the gate is what forwards it.
        var cookieHeader = Assert.Single(request.Headers, h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("auth=authcookie_theirs; twoFactorAuth=abc", cookieHeader.Value);
    }

    [Fact]
    public async Task TheSignedInSession_UsesTheProxy_WithTheSamePermissionAKeyNeeds()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);

        var (_, allowed) = await host.SignedInAsync(ModbotPermissions.ManageSettings | ModbotPermissions.UseVRChatProxy, Ct);
        var ok = await host.SendJsonAsync(HttpMethod.Get, Path, null, allowed, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(VRChatProxyAccount.Service, Assert.Single(host.VRChat.Forwarded).Account);

        var (_, refused) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var forbidden = await host.SendJsonAsync(HttpMethod.Get, Path, null, refused, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Single(host.VRChat.Forwarded);
    }

    [Fact]
    public async Task AnUnlinkedAccount_Is403_LikeEverywhereElse()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct, linked: false);
        var response = await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(VRChatProxyCallers.NeedsLink, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
    }

    // ── What comes back ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VRChatsAnswerComesBackAsItWas_StatusHeadersAndBody()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();

        var gate = new FakeVRChatGate
        {
            Forward = VRChatResult<VRChatProxyResponse>.Ok(
                new VRChatProxyResponse(
                    404,
                    [new KeyValuePair<string, string>("ETag", "\"abc\"")],
                    Encoding.UTF8.GetBytes("""{"error":{"message":"no such user","status_code":404}}"""),
                    "application/json; charset=utf-8"),
                404),
        };

        await using var host = await ApiTestHost.StartAsync(_db, gate);
        var key = await KeyAsync(host, ModbotPermissions.UseVRChatProxy);

        var response = await SendAsync(host, HttpMethod.Get, Path, bearer: key);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("\"abc\"", response.Headers.ETag?.ToString());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no such user", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ABodyIsForwardedOnAPost()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var key = await KeyAsync(host, ModbotPermissions.UseVRChatProxy);

        var response = await SendAsync(
            host, HttpMethod.Post, "/api/proxy/vrchat/api/1/groups/grp_test/bans", bearer: key,
            body: """{"userId":"usr_test"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var (_, request, _) = Assert.Single(host.VRChat.Forwarded);
        Assert.Equal("POST", request.Method);
        Assert.Equal("""{"userId":"usr_test"}""", Encoding.UTF8.GetString(request.Body!));
        Assert.StartsWith("application/json", request.ContentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenModbotsOwnPacingRefuses_ItIs429_AndWhenVRChatIsUnreachable_503()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();

        var gate = new FakeVRChatGate
        {
            Forward = VRChatResult<VRChatProxyResponse>.Failure(
                0, "proxy is rate limited (ColdStop); nothing will be sent on it for another 0:14:59.", kind: VRChatFailureKind.RateLimited),
        };

        await using var host = await ApiTestHost.StartAsync(_db, gate);
        var key = await KeyAsync(host, ModbotPermissions.UseVRChatProxy);

        var limited = await SendAsync(host, HttpMethod.Get, Path, bearer: key);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("rate limited", (await ApiTestHost.BodyOf(limited, Ct)).GetProperty("error").GetString(), StringComparison.Ordinal);

        gate.Forward = VRChatResult<VRChatProxyResponse>.Failure(
            0, "No VRChat account is configured. Complete onboarding first.", kind: VRChatFailureKind.NotConfigured);

        var down = await SendAsync(host, HttpMethod.Get, Path, bearer: key);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
    }

    [Fact]
    public async Task ABodyLargerThanTheProxyTakes_Is413()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var key = await KeyAsync(host, ModbotPermissions.UseVRChatProxy);

        var response = await SendAsync(
            host, HttpMethod.Post, "/api/proxy/vrchat/api/1/groups/grp_test/bans", bearer: key,
            body: new string('x', VRChatProxyEndpoints.MaxRequestBodyBytes + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(host.VRChat.Forwarded);
    }

    /// <summary>The rule itself, apart from the pipeline.</summary>
    [Theory]
    [InlineData(true, true, VRChatProxyCallerKind.ServiceAccount)]
    [InlineData(true, false, VRChatProxyCallerKind.Forbidden)]
    [InlineData(false, true, VRChatProxyCallerKind.Forbidden)]
    public void TheLinkThenThePermission(bool linked, bool permitted, VRChatProxyCallerKind expected)
    {
        var held = permitted ? ModbotPermissions.UseVRChatProxy : ModbotPermissions.ViewAuditLog;

        Assert.Equal(expected, VRChatProxyCallers.ForAccount(held, linked).Kind);
        Assert.Equal(VRChatProxyCallerKind.ServiceAccount, VRChatProxyCallers.ForAccount(ModbotPermissions.Administrator, linked: true).Kind);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private async Task TurnOnAsync()
    {
        await using var db = _db.NewContext();
        var settings = await db.GetSettingsAsync(Ct);
        settings.VRChatProxyEnabled = true;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A key holding exactly these permissions, made by an account that holds them and may make keys.</summary>
    private static async Task<string> KeyAsync(ApiTestHost host, ModbotPermissions permissions)
    {
        var (_, cookie) = await host.SignedInAsync(permissions | ModbotPermissions.ManageApiKeys, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/api-keys",
            new { name = "Proxy", permissions = PermissionCatalog.NamesOf(permissions), expiresAt = (DateTimeOffset?)null },
            cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("key").GetString()!;
    }

    private static Task<HttpResponseMessage> SendAsync(
        ApiTestHost host, HttpMethod method, string path, string? bearer = null, string? cookie = null, string? body = null)
    {
        var request = new HttpRequestMessage(method, path);

        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);

        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        return host.Client.SendAsync(request, Ct);
    }
}
