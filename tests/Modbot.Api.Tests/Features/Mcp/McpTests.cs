using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Mcp;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using ModelContextProtocol.Client;
using McpClient = ModelContextProtocol.Client.McpClient;
using ModelContextProtocol.Protocol;

namespace Modbot.Api.Tests.Features.Mcp;

/// <summary>
/// The MCP server (MCP server design): off is 404; the metadata an AI app reads; registration,
/// sign-in with PKCE, tokens and refresh; tool calls over Streamable HTTP with an API key and with
/// a token; the same permission rules as Chat; and disconnecting from either side.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class McpTests
{
    private const string Redirect = "http://127.0.0.1:9/callback";

    private readonly PostgresFixture _db;

    public McpTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The switch and the metadata ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Off_EverythingMcp_Answers404()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/.well-known/oauth-authorization-server", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/.well-known/oauth-protected-resource/mcp", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync("/mcp/register", new StringContent("{}", Encoding.UTF8, "application/json"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"), Ct)).StatusCode);

        await TurnOnAsync();

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/.well-known/oauth-authorization-server", Ct)).StatusCode);
    }

    [Fact]
    public async Task Metadata_TellsAnApp_WhereToSignIn_AndTheServerChallenges_WithIt()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);

        var server = await ApiTestHost.BodyOf(await host.Client.GetAsync("/.well-known/oauth-authorization-server", Ct), Ct);
        Assert.Equal("https://localhost", server.GetProperty("issuer").GetString());
        Assert.Equal("https://localhost/mcp/authorize", server.GetProperty("authorization_endpoint").GetString());
        Assert.Equal("https://localhost/mcp/token", server.GetProperty("token_endpoint").GetString());
        Assert.Equal("https://localhost/mcp/register", server.GetProperty("registration_endpoint").GetString());
        Assert.Equal("https://localhost/mcp/revoke", server.GetProperty("revocation_endpoint").GetString());
        Assert.Equal("S256", server.GetProperty("code_challenge_methods_supported")[0].GetString());
        Assert.Contains("refresh_token", server.GetProperty("grant_types_supported").EnumerateArray().Select(g => g.GetString()));
        Assert.Contains("none", server.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(g => g.GetString()));

        foreach (var path in new[] { "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp" })
        {
            var resource = await ApiTestHost.BodyOf(await host.Client.GetAsync(path, Ct), Ct);
            Assert.Equal("https://localhost/mcp", resource.GetProperty("resource").GetString());
            Assert.Equal("https://localhost", resource.GetProperty("authorization_servers")[0].GetString());
            Assert.Equal("mcp", resource.GetProperty("scopes_supported")[0].GetString());
        }

        // The 401 that starts the whole flow names the resource metadata.
        var refused = await host.Client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("resource_metadata=\"https://localhost/.well-known/oauth-protected-resource/mcp\"", refused.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePublicAddress_WhenSaved_IsTheAddressAnAppIsTold()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync(s => s.PublicAddress = "https://modbot.example.com");
        await using var host = await ApiTestHost.StartAsync(_db);

        var server = await ApiTestHost.BodyOf(await host.Client.GetAsync("/.well-known/oauth-authorization-server", Ct), Ct);
        Assert.Equal("https://modbot.example.com", server.GetProperty("issuer").GetString());

        var resource = await ApiTestHost.BodyOf(await host.Client.GetAsync("/.well-known/oauth-protected-resource/mcp", Ct), Ct);
        Assert.Equal("https://modbot.example.com/mcp", resource.GetProperty("resource").GetString());
    }

    // ── Registration ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registration_MakesAClient_PublicOrWithASecret_AndRefusesBadAddresses()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);

        var open = await RegisterRawAsync(host, new { redirect_uris = new[] { Redirect }, client_name = "Claude", token_endpoint_auth_method = "none" });
        Assert.Equal(HttpStatusCode.Created, open.StatusCode);
        var body = await ApiTestHost.BodyOf(open, Ct);
        Assert.True(Guid.TryParse(body.GetProperty("client_id").GetString(), out _));
        Assert.Equal("Claude", body.GetProperty("client_name").GetString());
        Assert.False(body.TryGetProperty("client_secret", out _));
        Assert.Equal("code", body.GetProperty("response_types")[0].GetString());

        var confidential = await RegisterRawAsync(host, new { redirect_uris = new[] { "https://chatgpt.com/connector_platform_oauth_redirect" }, client_name = "ChatGPT", token_endpoint_auth_method = "client_secret_post" });
        Assert.Equal(HttpStatusCode.Created, confidential.StatusCode);
        var secretBody = await ApiTestHost.BodyOf(confidential, Ct);
        var secret = secretBody.GetProperty("client_secret").GetString()!;
        Assert.StartsWith(McpSecrets.ClientSecretPrefix, secret, StringComparison.Ordinal);

        await using (var db = _db.NewContext())
        {
            var row = await db.McpClients.SingleAsync(c => c.Id == secretBody.GetProperty("client_id").GetGuid(), Ct);
            Assert.Equal(McpSecrets.Hash(secret), row.SecretHash);
            Assert.DoesNotContain(secret, row.SecretHash, StringComparison.Ordinal);
        }

        // A plain http address that is not the machine itself could be anyone's.
        var bad = await RegisterRawAsync(host, new { redirect_uris = new[] { "http://evil.example.com/cb" }, client_name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_redirect_uri", (await ApiTestHost.BodyOf(bad, Ct)).GetProperty("error").GetString());

        var none = await RegisterRawAsync(host, new { client_name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    // ── The sign-in flow ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SignIn_WithPkce_IssuesTokens_ThatOpenTheServer_AndACodeWorksOnce()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewProfile | ModbotPermissions.ViewLiveRooms, Ct);

        var clientId = await RegisterAsync(host);
        var (verifier, challenge) = Pkce();

        // The app sends the browser here; it lands on the web app's sign-in page with the same query.
        var query = $"?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&code_challenge={challenge}&code_challenge_method=S256&state=xyz&scope=mcp&resource={Uri.EscapeDataString("https://localhost/mcp")}";
        var sent = await host.Client.GetAsync("/mcp/authorize" + query, Ct);
        Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
        Assert.Equal("/connect" + query, sent.Headers.Location!.ToString());

        // The page asks what to show: the app's name and the tools this person is offered.
        var describe = await host.SendJsonAsync(HttpMethod.Get, "/api/mcp/authorize" + query, null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, describe.StatusCode);
        var view = await ApiTestHost.BodyOf(describe, Ct);
        Assert.Equal("Test app", view.GetProperty("clientName").GetString());
        var offered = view.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("find_person", offered);
        Assert.Contains("list_live_rooms", offered);
        Assert.DoesNotContain("search_audit_log", offered);

        // Refusing sends the app an error and no code.
        var refused = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, "/api/mcp/authorize", Decision(clientId, challenge, approve: false), cookie, Ct), Ct);
        var refusedTo = new Uri(refused.GetProperty("redirectTo").GetString()!);
        Assert.Equal("access_denied", QueryHelpers.ParseQuery(refusedTo.Query)["error"].ToString());
        Assert.Equal("xyz", QueryHelpers.ParseQuery(refusedTo.Query)["state"].ToString());

        var code = await ApproveAsync(host, cookie, clientId, challenge);

        // The wrong verifier gets nothing, and does not burn the code.
        var wrong = await TokenAsync(host, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect, ["client_id"] = clientId, ["code_verifier"] = new string('x', 43),
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("invalid_grant", (await ApiTestHost.BodyOf(wrong, Ct)).GetProperty("error").GetString());

        var tokens = await ExchangeAsync(host, clientId, code, verifier);
        Assert.StartsWith(McpSecrets.AccessPrefix, tokens.Access, StringComparison.Ordinal);
        Assert.StartsWith(McpSecrets.RefreshPrefix, tokens.Refresh, StringComparison.Ordinal);

        // Once.
        var again = await TokenAsync(host, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect, ["client_id"] = clientId, ["code_verifier"] = verifier,
        });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        await using (var db = _db.NewContext())
        {
            var grant = await db.McpGrants.AsNoTracking().SingleAsync(g => g.UserId == user.Id, Ct);
            Assert.Equal(McpSecrets.Hash(tokens.Access), grant.AccessTokenHash);
            Assert.Equal(McpSecrets.Hash(tokens.Refresh), grant.RefreshTokenHash);
            Assert.Single(await host.FactsAsync(FactType.McpConnected, grant.Id.ToString(), Ct));
        }

        // The token opens the server, and the tools are the ones the page listed.
        await using var mcp = await ConnectAsync(host, tokens.Access);
        var tools = await mcp.ListToolsAsync(cancellationToken: Ct);
        Assert.Equal(offered.OrderBy(n => n), tools.Select(t => t.Name).OrderBy(n => n));

        var rooms = await mcp.CallToolAsync("list_live_rooms", new Dictionary<string, object?>(), cancellationToken: Ct);
        Assert.NotEqual(true, rooms.IsError);
        Assert.Contains("rooms", Assert.IsType<TextContentBlock>(rooms.Content[0]).Text, StringComparison.OrdinalIgnoreCase);

        // The connection is listed for its person, under the app's name.
        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/mcp/connections", null, cookie, Ct), Ct);
        var connection = Assert.Single(list.GetProperty("connections").EnumerateArray());
        Assert.Equal("Test app", connection.GetProperty("clientName").GetString());
    }

    [Fact]
    public async Task Authorize_RefusesAnUnregisteredRedirect_InPlace_AndSendsOtherProblemsBack()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var clientId = await RegisterAsync(host);
        var (_, challenge) = Pkce();

        // Never a redirect to an address the client did not register.
        var elsewhere = await host.Client.GetAsync($"/mcp/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString("https://evil.example.com/cb")}&code_challenge={challenge}&code_challenge_method=S256", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);

        var unknown = await host.Client.GetAsync($"/mcp/authorize?response_type=code&client_id={Guid.NewGuid()}&redirect_uri={Uri.EscapeDataString(Redirect)}&code_challenge={challenge}&code_challenge_method=S256", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // PKCE is not optional: a request without a challenge goes back with an error.
        var noPkce = await host.Client.GetAsync($"/mcp/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&state=s1", Ct);
        Assert.Equal(HttpStatusCode.Redirect, noPkce.StatusCode);
        var back = QueryHelpers.ParseQuery(noPkce.Headers.Location!.Query);
        Assert.Equal("invalid_request", back["error"].ToString());
        Assert.Equal("s1", back["state"].ToString());

        // A token for some other server is not issued.
        var elsewhereResource = await host.Client.GetAsync($"/mcp/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&code_challenge={challenge}&code_challenge_method=S256&resource={Uri.EscapeDataString("https://other.example.com/mcp")}", Ct);
        Assert.Equal(HttpStatusCode.Redirect, elsewhereResource.StatusCode);
        Assert.Equal("invalid_target", QueryHelpers.ParseQuery(elsewhereResource.Headers.Location!.Query)["error"].ToString());
    }

    [Fact]
    public async Task SigningIn_NeedsUseAiChat()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewProfile | ModbotPermissions.ViewAuditLog, Ct);
        var clientId = await RegisterAsync(host);
        var (_, challenge) = Pkce();

        var query = $"?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&code_challenge={challenge}&code_challenge_method=S256";
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/mcp/authorize" + query, null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, "/api/mcp/authorize", Decision(clientId, challenge, approve: true), cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendJsonAsync(HttpMethod.Get, "/api/mcp/authorize" + query, null, null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Refresh_RotatesBothTokens_AndTheOldOnesStopWorking()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewLiveRooms, Ct);
        var clientId = await RegisterAsync(host);
        var (verifier, challenge) = Pkce();
        var first = await ExchangeAsync(host, clientId, await ApproveAsync(host, cookie, clientId, challenge), verifier);

        var refreshed = await TokenAsync(host, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = first.Refresh, ["client_id"] = clientId,
        });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = Tokens(await ApiTestHost.BodyOf(refreshed, Ct));
        Assert.NotEqual(first.Access, second.Access);
        Assert.NotEqual(first.Refresh, second.Refresh);

        // The old refresh token is dead; the old access token too.
        var reused = await TokenAsync(host, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = first.Refresh, ["client_id"] = clientId,
        });
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(host, first.Access)).StatusCode);

        await using var mcp = await ConnectAsync(host, second.Access);
        Assert.NotEmpty(await mcp.ListToolsAsync(cancellationToken: Ct));

        // An access token expires on its own; the refresh token still works after that.
        host.Clock.Advance(McpSecrets.AccessTokenLife + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(host, second.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(host, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = second.Refresh, ["client_id"] = clientId,
        })).StatusCode);

        // Another client cannot use this client's refresh token.
        var other = await RegisterAsync(host);
        var stolen = await TokenAsync(host, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = second.Refresh, ["client_id"] = other,
        });
        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);
    }

    [Fact]
    public async Task AConfidentialClient_MustShowItsSecret()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);

        var registered = await ApiTestHost.BodyOf(await RegisterRawAsync(host, new
        {
            redirect_uris = new[] { Redirect }, client_name = "ChatGPT", token_endpoint_auth_method = "client_secret_basic",
        }), Ct);
        var clientId = registered.GetProperty("client_id").GetString()!;
        var secret = registered.GetProperty("client_secret").GetString()!;
        var (verifier, challenge) = Pkce();
        var code = await ApproveAsync(host, cookie, clientId, challenge);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        };

        var withoutSecret = await TokenAsync(host, new Dictionary<string, string>(form) { ["client_id"] = clientId });
        Assert.Equal(HttpStatusCode.Unauthorized, withoutSecret.StatusCode);

        var wrongSecret = await TokenAsync(host, form, basic: (clientId, McpSecrets.NewClientSecret()));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);

        var right = await TokenAsync(host, form, basic: (clientId, secret));
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    // ── Tools, and who may call which ────────────────────────────────────────────────────────

    [Fact]
    public async Task AnApiKey_OpensTheServer_WithItsOwnPermissions_AndACallIsRecorded()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(
            ModbotPermissions.ManageApiKeys | ModbotPermissions.UseAiChat | ModbotPermissions.ViewProfile | ModbotPermissions.ViewAuditLog, Ct);

        await SeedPersonAsync(host, "usr_mcp_1", "Alex Example");

        // A key without Use AI chat is refused at the door.
        var (_, noChat) = await CreateKeyAsync(host, cookie, ["ViewProfile", "ViewAuditLog"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await ProbeAsync(host, noChat)).StatusCode);

        // A key with it sees the tools its permissions allow: profiles, not the audit log.
        var (keyId, key) = await CreateKeyAsync(host, cookie, ["UseAiChat", "ViewProfile"]);
        await using var mcp = await ConnectAsync(host, key);
        var names = (await mcp.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();
        Assert.Contains("find_person", names);
        Assert.DoesNotContain("search_audit_log", names);

        var found = await mcp.CallToolAsync("find_person", new Dictionary<string, object?> { ["query"] = "Alex" }, cancellationToken: Ct);
        Assert.NotEqual(true, found.IsError);
        Assert.Contains("usr_mcp_1", Assert.IsType<TextContentBlock>(found.Content[0]).Text, StringComparison.Ordinal);

        // The lookup is in the person's history, attributed to the key's owner, naming the key.
        var fact = Assert.Single(await host.FactsAsync(FactType.ChatLookup, "usr_mcp_1", Ct));
        Assert.Equal(user.Id.ToString(), fact.ActorId);
        var data = ApiTestHost.DataOf(fact);
        Assert.Equal("mcp", data.GetProperty("via").GetString());
        Assert.Equal("API key Bot", data.GetProperty("client").GetString());
        Assert.Equal("find_person", data.GetProperty("tools")[0].GetString());

        // A tool the key was not offered does not run, whatever the account holds.
        var refused = await mcp.CallToolAsync("search_audit_log", new Dictionary<string, object?> { ["query"] = "x" }, cancellationToken: Ct);
        Assert.True(refused.IsError);
        Assert.Contains("No such tool", Assert.IsType<TextContentBlock>(refused.Content[0]).Text, StringComparison.Ordinal);

        Assert.Single(await host.FactsAsync(FactType.ChatLookup, "usr_mcp_1", Ct));
        Assert.NotEqual(Guid.Empty, keyId);
    }

    [Fact]
    public async Task AModeratorsToken_CannotCallAnOperatorTool_AndLosesToolsWhenDemoted()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewProfile | ModbotPermissions.ViewAnalytics, Ct);
        var clientId = await RegisterAsync(host);
        var (verifier, challenge) = Pkce();
        var tokens = await ExchangeAsync(host, clientId, await ApproveAsync(host, cookie, clientId, challenge), verifier);

        await using (var mcp = await ConnectAsync(host, tokens.Access))
        {
            var names = (await mcp.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();
            Assert.Contains("group_analytics", names);
            Assert.DoesNotContain("search_audit_log", names);
            Assert.DoesNotContain("search_discord_messages", names);

            var refused = await mcp.CallToolAsync("search_audit_log", new Dictionary<string, object?>(), cancellationToken: Ct);
            Assert.True(refused.IsError);
        }

        // Demoted: the token is the person, read again on the next request.
        await using (var db = _db.NewContext())
        {
            var lesser = await TestAccounts.RoleForAsync(db, ModbotPermissions.UseAiChat, Ct);
            await db.UserRoles.Where(ur => ur.UserId == user.Id).ExecuteDeleteAsync(Ct);
            db.UserRoles.Add(new ModbotUserRole { UserId = user.Id, RoleId = lesser });
            await db.SaveChangesAsync(Ct);
        }

        await using (var mcp = await ConnectAsync(host, tokens.Access))
            Assert.Empty(await mcp.ListToolsAsync(cancellationToken: Ct));

        // And without Use AI chat at all, the door is shut.
        await using (var db = _db.NewContext())
        {
            var none = await TestAccounts.RoleForAsync(db, ModbotPermissions.ViewProfile, Ct);
            await db.UserRoles.Where(ur => ur.UserId == user.Id).ExecuteDeleteAsync(Ct);
            db.UserRoles.Add(new ModbotUserRole { UserId = user.Id, RoleId = none });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await ProbeAsync(host, tokens.Access)).StatusCode);
    }

    [Fact]
    public async Task ATool_SwitchedOffForChat_IsOffForMcpToo()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync(s => s.AiChatToolSwitches = """{"find_person":false}""");
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (_, key) = await CreateKeyAsync(host, cookie, ["Administrator"]);

        await using var mcp = await ConnectAsync(host, key);
        var names = (await mcp.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();
        Assert.DoesNotContain("find_person", names);
        Assert.Contains("get_person", names);
    }

    // ── Disconnecting ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePerson_CanDisconnectAnApp_AndItsTokensStop()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewLiveRooms, Ct);
        var (_, other) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);
        var clientId = await RegisterAsync(host);
        var (verifier, challenge) = Pkce();
        var tokens = await ExchangeAsync(host, clientId, await ApproveAsync(host, cookie, clientId, challenge), verifier);

        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/mcp/connections", null, cookie, Ct), Ct);
        var id = Assert.Single(list.GetProperty("connections").EnumerateArray()).GetProperty("id").GetGuid();

        // Somebody else's connection is not theirs to end.
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendJsonAsync(HttpMethod.Delete, $"/api/mcp/connections/{id}", null, other, Ct)).StatusCode);
        Assert.Empty((await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/mcp/connections", null, other, Ct), Ct)).GetProperty("connections").EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Delete, $"/api/mcp/connections/{id}", null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Delete, $"/api/mcp/connections/{id}", null, cookie, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(host, tokens.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TokenAsync(host, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.Refresh, ["client_id"] = clientId,
        })).StatusCode);

        Assert.Single(await host.FactsAsync(FactType.McpDisconnected, id.ToString(), Ct));
        Assert.Empty((await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/mcp/connections", null, cookie, Ct), Ct)).GetProperty("connections").EnumerateArray());
    }

    [Fact]
    public async Task TheApp_CanRevokeItsOwnTokens()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await TurnOnAsync();
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);
        var clientId = await RegisterAsync(host);
        var (verifier, challenge) = Pkce();
        var tokens = await ExchangeAsync(host, clientId, await ApproveAsync(host, cookie, clientId, challenge), verifier);

        // Another client revoking this one's token changes nothing, and is told nothing.
        var other = await RegisterAsync(host);
        var response = await host.Client.PostAsync("/mcp/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = tokens.Refresh, ["client_id"] = other }), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ProbeAsync(host, tokens.Access)).StatusCode);

        response = await host.Client.PostAsync("/mcp/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = tokens.Refresh, ["client_id"] = clientId }), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(host, tokens.Access)).StatusCode);
    }

    // ── Settings ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Settings_NeedManageSettings_AndSayWhatAKeyForMcpNeeds()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        var (_, operatorCookie) = await host.SignedInAsync(
            ModbotPermissions.ManageSettings | ModbotPermissions.UseAiChat | ModbotPermissions.ViewProfile | ModbotPermissions.Ban, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/mcp/settings", null, viewer, Ct)).StatusCode);

        var before = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/mcp/settings", null, operatorCookie, Ct), Ct);
        Assert.False(before.GetProperty("enabled").GetBoolean());
        Assert.Equal("https://localhost/mcp", before.GetProperty("serverUrl").GetString());
        Assert.False(before.GetProperty("publicAddressSet").GetBoolean());

        // The key permissions are the door plus the tools' needs, narrowed to what this person holds.
        var keyPermissions = before.GetProperty("keyPermissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(["ViewProfile", "UseAiChat"], keyPermissions);
        Assert.NotEmpty(before.GetProperty("tools").EnumerateArray());

        var after = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Put, "/api/mcp/settings", new { enabled = true }, operatorCookie, Ct), Ct);
        Assert.True(after.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/.well-known/oauth-authorization-server", Ct)).StatusCode);

        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);
        Assert.Contains(facts, f => ApiTestHost.DataOf(f).GetProperty("setting").GetString() == "mcpServerEnabled");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private async Task TurnOnAsync(Action<Core.Data.Entities.Settings>? also = null)
    {
        await using var db = _db.NewContext();
        var settings = await db.GetSettingsAsync(Ct);
        settings.McpServerEnabled = true;
        also?.Invoke(settings);
        await db.SaveChangesAsync(Ct);
    }

    private static Task<HttpResponseMessage> RegisterRawAsync(ApiTestHost host, object body)
        => host.Client.PostAsync("/mcp/register", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), Ct);

    /// <summary>A public client called "Test app" that listens on the loopback address.</summary>
    private static async Task<string> RegisterAsync(ApiTestHost host)
    {
        var response = await RegisterRawAsync(host, new { redirect_uris = new[] { Redirect }, client_name = "Test app", token_endpoint_auth_method = "none" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("client_id").GetString()!;
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static object Decision(string clientId, string challenge, bool approve) => new
    {
        clientId,
        redirectUri = Redirect,
        state = "xyz",
        codeChallenge = challenge,
        codeChallengeMethod = "S256",
        scope = "mcp",
        resource = "https://localhost/mcp",
        approve,
    };

    /// <summary>The signed-in person allows the app; the code the browser would carry back.</summary>
    private static async Task<string> ApproveAsync(ApiTestHost host, string cookie, string clientId, string challenge)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/mcp/authorize", Decision(clientId, challenge, approve: true), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var to = new Uri((await ApiTestHost.BodyOf(response, Ct)).GetProperty("redirectTo").GetString()!);
        Assert.StartsWith(Redirect, to.ToString(), StringComparison.Ordinal);

        var query = QueryHelpers.ParseQuery(to.Query);
        Assert.Equal("xyz", query["state"].ToString());
        return query["code"].ToString();
    }

    private static Task<HttpResponseMessage> TokenAsync(ApiTestHost host, Dictionary<string, string> form, (string Id, string Secret)? basic = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp/token") { Content = new FormUrlEncodedContent(form) };
        if (basic is { } b)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{b.Id}:{b.Secret}")));
        return host.Client.SendAsync(request, Ct);
    }

    private sealed record IssuedTokens(string Access, string Refresh);

    private static IssuedTokens Tokens(JsonElement body)
    {
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.Equal("mcp", body.GetProperty("scope").GetString());
        Assert.Equal((int)McpSecrets.AccessTokenLife.TotalSeconds, body.GetProperty("expires_in").GetInt32());
        return new IssuedTokens(body.GetProperty("access_token").GetString()!, body.GetProperty("refresh_token").GetString()!);
    }

    private static async Task<IssuedTokens> ExchangeAsync(ApiTestHost host, string clientId, string code, string verifier)
    {
        var response = await TokenAsync(host, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = Redirect,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
            ["resource"] = "https://localhost/mcp",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return Tokens(await ApiTestHost.BodyOf(response, Ct));
    }

    /// <summary>An MCP client over the test server, sending this bearer token.</summary>
    private static async Task<McpClient> ConnectAsync(ApiTestHost host, string bearer)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(host.Client.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer },
            },
            host.Client);

        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    /// <summary>One raw MCP request with this bearer, for the status code alone.</summary>
    private static Task<HttpResponseMessage> ProbeAsync(ApiTestHost host, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return host.Client.SendAsync(request, Ct);
    }

    private static async Task<(Guid Id, string Key)> CreateKeyAsync(ApiTestHost host, string cookie, string[] permissions)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/api-keys", new { name = "Bot", permissions }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);
        return (body.GetProperty("apiKey").GetProperty("id").GetGuid(), body.GetProperty("key").GetString()!);
    }

    private static async Task SeedPersonAsync(ApiTestHost host, string id, string name)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        db.VRChatUsers.Add(new VRChatUser
        {
            UserId = id,
            DisplayName = name,
            RawProfile = "{}",
            FirstSeenAt = host.Clock.UtcNow.AddDays(-1),
            LastSeenAt = host.Clock.UtcNow,
        });

        await db.SaveChangesAsync(Ct);
    }
}
