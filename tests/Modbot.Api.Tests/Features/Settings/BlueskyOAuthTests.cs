using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Signing in with Bluesky (posts design §4.2c, step 3b) against a scripted sign-in server: offered
/// only with an https public address, Modbot's client document with only the public key, PAR with
/// PKCE and a DPoP proof sent again once with the server's nonce, the narrow scopes and the wide one
/// when they are refused, and a callback that finishes only for the same person, the same state and
/// sign-in server, and the account the handle named.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class BlueskyOAuthTests(PostgresFixture db)
{
    private const string Path = "/api/settings/bluesky";
    private const string Public = "https://modbot.example.com";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ApiTestHost Host, FakeBluesky Bluesky, string Cookie)> StartAsync(string? publicAddress = Public)
    {
        await ApiTestHost.ResetDeploymentAsync(db, Ct);

        await using (var context = db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.PublicAddress = publicAddress;
            await context.SaveChangesAsync(Ct);
        }

        ApiTestHost? started = null;
        var bluesky = new FakeBluesky(() => started?.Clock.UtcNow ?? DateTimeOffset.UnixEpoch);

        started = await ApiTestHost.StartAsync(db, configure: services =>
            services.AddHttpClient(BlueskyClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => bluesky));

        var (_, cookie) = await started.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        return (started, bluesky, cookie);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static async Task<Uri> SignInAsync(ApiTestHost host, string cookie, string handle = FakeBluesky.Handle)
    {
        var started = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/sign-in", new { handle = "@" + handle }, cookie, Ct));
        return new Uri(started.GetProperty("url").GetString()!);
    }

    private static Task<HttpResponseMessage> CallbackAsync(ApiTestHost host, string? cookie, string code, string state, string? iss = FakeBluesky.AuthServer)
    {
        var query = $"?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}"
            + (iss is null ? string.Empty : $"&iss={Uri.EscapeDataString(iss)}");

        return host.SendJsonAsync(HttpMethod.Get, BlueskyOAuth.CallbackPath + query, null, cookie, Ct);
    }

    private static string Back(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://modbot.example.com")]
    [InlineData("https://modbot.example.com:8443")]
    public async Task WithoutAnHttpsPublicAddress_SignInWithBlueskyIsNotOffered(string? publicAddress)
    {
        var (host, bluesky, cookie) = await StartAsync(publicAddress);
        await using var running = host;

        var view = await OkAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));
        Assert.False(view.GetProperty("signInWithBluesky").GetBoolean());

        var document = await host.SendJsonAsync(HttpMethod.Get, BlueskyOAuth.MetadataPath, null, null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);

        var start = await host.SendJsonAsync(HttpMethod.Post, Path + "/sign-in", new { handle = FakeBluesky.Handle }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
        Assert.Empty(bluesky.Requests);
    }

    [Fact]
    public async Task WithAnHttpsPublicAddress_ItIsOffered()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var view = await OkAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));

        Assert.True(view.GetProperty("signInWithBluesky").GetBoolean());
        Assert.False(view.GetProperty("signedInWithBluesky").GetBoolean());
    }

    [Fact]
    public async Task TheClientDocumentIsServedToAnyone_FromThePublicAddress_WithTheKeyKeptEncrypted()
    {
        var (host, _, _) = await StartAsync();
        await using var running = host;

        var response = await host.SendJsonAsync(HttpMethod.Get, BlueskyOAuth.MetadataPath, null, null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
        Assert.Equal(Public + BlueskyOAuth.MetadataPath, document["client_id"]!.GetValue<string>());
        Assert.Equal(Public + BlueskyOAuth.CallbackPath, document["redirect_uris"]![0]!.GetValue<string>());
        Assert.Equal("private_key_jwt", document["token_endpoint_auth_method"]!.GetValue<string>());
        Assert.True(document["dpop_bound_access_tokens"]!.GetValue<bool>());

        var listed = document["jwks"]!["keys"]![0]!.AsObject();
        Assert.False(listed.ContainsKey("d"));

        await using var context = db.NewContext();
        var stored = (await context.GetSettingsAsync(Ct)).BlueskyOAuthKeyEncrypted;
        Assert.NotNull(stored);
        var key = host.Services.GetRequiredService<ISecretProtector>().Unprotect(stored)!;
        Assert.DoesNotContain(key, stored, StringComparison.Ordinal);
        Assert.Equal(BlueskyKeys.KidOf(key), listed["kid"]!.GetValue<string>());

        // The same key every time: a sign-in is bound to it.
        var again = JsonNode.Parse(await (await host.SendJsonAsync(HttpMethod.Get, BlueskyOAuth.MetadataPath, null, null, Ct)).Content.ReadAsStringAsync(Ct))!;
        Assert.Equal(listed["kid"]!.GetValue<string>(), again["jwks"]!["keys"]![0]!["kid"]!.GetValue<string>());
    }

    [Fact]
    public async Task SigningInPushesTheRequest_WithPkceAndAClientAssertion_AndADpopProofSentOnceMoreWithTheNonce()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        var url = await SignInAsync(host, cookie);

        Assert.Equal(FakeBluesky.AuthorizeEndpoint, url.GetLeftPart(UriPartial.Path));
        Assert.Contains("request_uri=", url.Query, StringComparison.Ordinal);
        Assert.Contains("client_id=" + Uri.EscapeDataString(Public + BlueskyOAuth.MetadataPath), url.Query, StringComparison.Ordinal);

        // The first proof had no nonce and was refused; the second carried the one the server gave.
        var pushes = bluesky.Requests.Where(r => r.Uri.AbsoluteUri == FakeBluesky.ParEndpoint).ToList();
        Assert.Equal(2, pushes.Count);
        Assert.Null(FakeBluesky.ClaimsOf(pushes[0].Dpop)!["nonce"]);
        Assert.Equal(bluesky.AuthNonce, FakeBluesky.ClaimsOf(pushes[1].Dpop)!["nonce"]!.GetValue<string>());

        var par = Assert.Single(bluesky.Pars);
        Assert.Equal("S256", par["code_challenge_method"]);
        Assert.Equal(BlueskyOAuth.NarrowScope, par["scope"]);
        Assert.Equal(FakeBluesky.Handle, par["login_hint"]);
        Assert.Equal(Public + BlueskyOAuth.CallbackPath, par["redirect_uri"]);
        Assert.False(string.IsNullOrEmpty(par["state"]));

        // The client assertion is signed with the key in Modbot's client document.
        var document = JsonNode.Parse(await (await host.SendJsonAsync(HttpMethod.Get, BlueskyOAuth.MetadataPath, null, null, Ct)).Content.ReadAsStringAsync(Ct))!;
        Assert.True(FakeBluesky.SignedBy(par["client_assertion"], document["jwks"]!["keys"]![0]!.AsObject()));

        // The handle was found both ways first, as Check does.
        Assert.Contains(bluesky.Requests, r => r.Method == "com.atproto.identity.resolveHandle");
        Assert.Contains(bluesky.Requests, r => r.Uri.Host == "plc.directory");

        // Nothing is signed in yet, and the waiting sign-in is kept encrypted.
        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        Assert.False(settings.BlueskyOAuthSignedIn);
        Assert.NotNull(settings.BlueskyOAuthPendingEncrypted);
        Assert.DoesNotContain(par["state"], settings.BlueskyOAuthPendingEncrypted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NarrowScopesRefused_AreAskedForOnceMoreAsTheWideOne()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        bluesky.RefuseNarrowScope = true;

        await SignInAsync(host, cookie);

        Assert.Equal([BlueskyOAuth.NarrowScope, BlueskyOAuth.WideScope], bluesky.Pars.Select(p => p["scope"]));
    }

    [Fact]
    public async Task TheCallbackSignsIn_ForgetsTheAppPassword_AndChecksTheAccount()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        // An app password saved before: one way of signing in at a time.
        await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { handle = FakeBluesky.Handle, appPassword = FakeBluesky.AppPassword }, cookie, Ct));

        await SignInAsync(host, cookie);
        var (code, state) = bluesky.Approve();

        var response = await CallbackAsync(host, cookie, code, state);

        Assert.Equal("/settings?bluesky=signed-in#bluesky", Back(response));

        var token = Assert.Single(bluesky.TokenRequests);
        Assert.Equal("authorization_code", token["grant_type"]);
        Assert.False(string.IsNullOrEmpty(token["code_verifier"]));

        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        Assert.True(settings.BlueskyOAuthSignedIn);
        Assert.Null(settings.BlueskyAppPasswordEncrypted);
        Assert.Null(settings.BlueskyOAuthPendingEncrypted);
        Assert.Equal(FakeBluesky.Did, settings.BlueskyDid);
        Assert.Equal(FakeBluesky.Server, settings.BlueskyServer);
        Assert.NotNull(settings.BlueskyCheckedAt);
        Assert.Equal("Our group", settings.BlueskyDisplayName);
        Assert.Null(settings.BlueskyProblem);

        var session = host.Services.GetRequiredService<BlueskySession>();
        var tokens = session.Read(settings.BlueskySessionEncrypted)!;
        Assert.Equal(bluesky.AccessJwt, tokens.AccessJwt);
        Assert.Equal(bluesky.OAuthRefresh, tokens.RefreshJwt);
        Assert.Equal(FakeBluesky.AuthServer, tokens.OAuth!.Issuer);
        Assert.DoesNotContain(bluesky.OAuthRefresh!, settings.BlueskySessionEncrypted, StringComparison.Ordinal);

        var view = await OkAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));
        Assert.True(view.GetProperty("signedInWithBluesky").GetBoolean());
        Assert.False(view.GetProperty("appPasswordStored").GetBoolean());
        Assert.True(view.GetProperty("canPost").GetBoolean());
        Assert.DoesNotContain(bluesky.AccessJwt!, view.GetRawText(), StringComparison.Ordinal);

        // Check works on the sign-in, with a DPoP proof, and never signs in with a password.
        var check = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));
        Assert.Null(check.GetProperty("check").GetProperty("problem").GetString());
        var proved = bluesky.Requests.Last(r => r.Method == "com.atproto.server.getSession");
        Assert.Equal("DPoP", proved.Scheme);
        Assert.Equal(0, bluesky.SignIns);
    }

    [Fact]
    public async Task ACallbackForAnotherAccountThanTheHandleIsRefused()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        bluesky.TokenSub = "did:plc:someoneelseentirely000000";

        await SignInAsync(host, cookie);
        var (code, state) = bluesky.Approve();

        Assert.Equal("/settings?bluesky=wrong-account#bluesky", Back(await CallbackAsync(host, cookie, code, state)));

        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        Assert.False(settings.BlueskyOAuthSignedIn);
        Assert.Null(settings.BlueskySessionEncrypted);
        Assert.Null(settings.BlueskyOAuthPendingEncrypted);
    }

    [Fact]
    public async Task ACallbackFromAnotherPersonIsRefused_AndTheSignInIsGoneAfter()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        var (_, other) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SignInAsync(host, cookie);
        var (code, state) = bluesky.Approve();

        Assert.Equal("/settings?bluesky=signed-out#bluesky", Back(await CallbackAsync(host, other, code, state)));
        Assert.Empty(bluesky.TokenRequests);

        // Good once: the person who started cannot finish it now either.
        Assert.Equal("/settings?bluesky=expired#bluesky", Back(await CallbackAsync(host, cookie, code, state)));
        Assert.Empty(bluesky.TokenRequests);
    }

    [Fact]
    public async Task ACallbackWithNoSessionIsRefused()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        await SignInAsync(host, cookie);
        var (code, state) = bluesky.Approve();

        Assert.Equal("/settings?bluesky=signed-out#bluesky", Back(await CallbackAsync(host, null, code, state)));
        Assert.Empty(bluesky.TokenRequests);
    }

    [Theory]
    [InlineData("the-wrong-state", FakeBluesky.AuthServer, "expired")]
    [InlineData(null, "https://evil.example.com", "refused")]
    [InlineData(null, null, "refused")]
    public async Task ACallbackWithTheWrongStateOrSignInServerIsRefused(string? state, string? iss, string word)
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        await SignInAsync(host, cookie);
        var (code, rightState) = bluesky.Approve();

        Assert.Equal($"/settings?bluesky={word}#bluesky", Back(await CallbackAsync(host, cookie, code, state ?? rightState, iss)));
        Assert.Empty(bluesky.TokenRequests);
    }

    [Fact]
    public async Task ASignInThatTookLongerThanTenMinutesIsRefused()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        await SignInAsync(host, cookie);
        var (code, state) = bluesky.Approve();
        host.Clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal("/settings?bluesky=expired#bluesky", Back(await CallbackAsync(host, cookie, code, state)));
    }

    [Fact]
    public async Task SavingAnAppPasswordEndsTheSignInWithBluesky()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        await SignInAsync(host, cookie);
        var (code, state) = bluesky.Approve();
        Back(await CallbackAsync(host, cookie, code, state));

        var view = await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { appPassword = FakeBluesky.AppPassword }, cookie, Ct));

        Assert.False(view.GetProperty("signedInWithBluesky").GetBoolean());
        Assert.True(view.GetProperty("appPasswordStored").GetBoolean());

        await using var context = db.NewContext();
        Assert.Null((await context.GetSettingsAsync(Ct)).BlueskySessionEncrypted);
    }
}
