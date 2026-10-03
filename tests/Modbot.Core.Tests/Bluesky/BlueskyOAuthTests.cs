using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Modbot.Core.Bluesky;
using Modbot.Core.Security;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Bluesky;

/// <summary>
/// Signing in with Bluesky (posts design §4.2c, step 3b), the parts with no database: when it is
/// offered, the client document Bluesky's server reads, the signing key that only ever shows its public
/// half, DPoP proofs, and a sign-in kept whole in the session.
/// </summary>
public class BlueskyOAuthTests
{
    private const string Public = "https://modbot.example.com";

    [Theory]
    [InlineData("https://modbot.example.com", true)]
    [InlineData("https://modbot.example.com/", true)]
    [InlineData("https://modbot.example.com:443", true)]
    [InlineData("http://modbot.example.com", false)]
    [InlineData("https://modbot.example.com:8443", false)]
    [InlineData("https://localhost", false)]
    [InlineData("https://192.0.2.10", false)]
    [InlineData("https://modbot.example.com/app", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void SignInWithBlueskyIsOfferedOnlyForAnHttpsAddressWithNoPort(string? publicAddress, bool offered)
    {
        Assert.Equal(offered, BlueskyOAuth.Offered(publicAddress));
        Assert.Equal(offered, BlueskyOAuth.ClientIdFor(publicAddress) is not null);
        Assert.Equal(offered, BlueskyOAuth.RedirectFor(publicAddress) is not null);
    }

    [Fact]
    public void TheClientDocumentIsAConfidentialWebClient_WithOnlyThePublicHalfOfTheKey()
    {
        var key = BlueskyKeys.NewPrivateJwk();

        var document = BlueskyOAuth.Metadata(Public, key)!;

        Assert.Equal(Public + "/oauth/bluesky/client-metadata.json", document["client_id"]!.GetValue<string>());
        Assert.Equal("web", document["application_type"]!.GetValue<string>());
        Assert.Equal(Public + "/api/bluesky/callback", Assert.Single(document["redirect_uris"]!.AsArray())!.GetValue<string>());
        Assert.Equal("private_key_jwt", document["token_endpoint_auth_method"]!.GetValue<string>());
        Assert.Equal("ES256", document["token_endpoint_auth_signing_alg"]!.GetValue<string>());
        Assert.True(document["dpop_bound_access_tokens"]!.GetValue<bool>());
        Assert.Equal(["authorization_code", "refresh_token"], document["grant_types"]!.AsArray().Select(g => g!.GetValue<string>()));
        Assert.Equal(["code"], document["response_types"]!.AsArray().Select(g => g!.GetValue<string>()));

        var scopes = document["scope"]!.GetValue<string>().Split(' ');
        Assert.Contains("atproto", scopes);
        Assert.Contains("repo:app.bsky.feed.post?action=create&action=update&action=delete", scopes);
        Assert.Contains("blob:image/*", scopes);
        Assert.Contains("transition:generic", scopes);

        var listed = Assert.Single(document["jwks"]!["keys"]!.AsArray())!.AsObject();
        Assert.False(listed.ContainsKey("d"));
        Assert.Equal("EC", listed["kty"]!.GetValue<string>());
        Assert.Equal("P-256", listed["crv"]!.GetValue<string>());
        Assert.Equal("ES256", listed["alg"]!.GetValue<string>());
        Assert.Equal(BlueskyKeys.KidOf(key), listed["kid"]!.GetValue<string>());

        // The private part is nowhere in the document.
        var d = JsonNode.Parse(key)!["d"]!.GetValue<string>();
        Assert.DoesNotContain(d, document.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnHttpsAddressThereIsNoClientDocument()
    {
        Assert.Null(BlueskyOAuth.Metadata("http://modbot.example.com", BlueskyKeys.NewPrivateJwk()));
        Assert.Null(BlueskyOAuth.Metadata(null, BlueskyKeys.NewPrivateJwk()));
    }

    [Fact]
    public void AClientAssertionNamesModbotAndTheSignInServer_AndIsSignedWithTheKeyItNames()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        var oauth = new BlueskyOAuth(new OneHandlerClients(new FakeBluesky(() => clock.UtcNow)), new PlainProtector(), clock);
        var key = BlueskyKeys.NewPrivateJwk();
        var clientId = BlueskyOAuth.ClientIdFor(Public)!;

        var assertion = oauth.Assertion(key, clientId, FakeBluesky.AuthServer);

        var header = FakeBluesky.HeaderOf(assertion)!;
        var claims = FakeBluesky.ClaimsOf(assertion)!;
        Assert.Equal("ES256", header["alg"]!.GetValue<string>());
        Assert.Equal(BlueskyKeys.KidOf(key), header["kid"]!.GetValue<string>());
        Assert.Equal(clientId, claims["iss"]!.GetValue<string>());
        Assert.Equal(clientId, claims["sub"]!.GetValue<string>());
        Assert.Equal(FakeBluesky.AuthServer, claims["aud"]!.GetValue<string>());
        Assert.Equal(clock.UtcNow.ToUnixTimeSeconds(), claims["iat"]!.GetValue<long>());
        Assert.True(claims["exp"]!.GetValue<long>() > claims["iat"]!.GetValue<long>());
        Assert.True(FakeBluesky.SignedBy(assertion, BlueskyKeys.PublicJwk(key, withUse: false)!));

        // A fresh jti every time: the server refuses one it has seen.
        Assert.NotEqual(claims["jti"]!.GetValue<string>(), FakeBluesky.ClaimsOf(oauth.Assertion(key, clientId, FakeBluesky.AuthServer))!["jti"]!.GetValue<string>());
    }

    [Fact]
    public void ADpopProofNamesTheCall_CarriesTheTokensHash_AndShowsOnlyThePublicKey()
    {
        var key = BlueskyKeys.NewPrivateJwk();
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var url = new Uri(FakeBluesky.Server + "xrpc/com.atproto.repo.getRecord?repo=x");

        var proof = BlueskyDpop.Proof(key, HttpMethod.Post, url, "nonce-7", "the-access-token", now);

        var header = FakeBluesky.HeaderOf(proof)!;
        var claims = FakeBluesky.ClaimsOf(proof)!;
        Assert.Equal("dpop+jwt", header["typ"]!.GetValue<string>());
        Assert.False(header["jwk"]!.AsObject().ContainsKey("d"));
        Assert.True(FakeBluesky.SignedBy(proof, header["jwk"]!.AsObject()));
        Assert.Equal("POST", claims["htm"]!.GetValue<string>());
        Assert.Equal(FakeBluesky.Server + "xrpc/com.atproto.repo.getRecord", claims["htu"]!.GetValue<string>());
        Assert.Equal("nonce-7", claims["nonce"]!.GetValue<string>());
        Assert.Equal(now.ToUnixTimeSeconds(), claims["iat"]!.GetValue<long>());
        Assert.Equal(
            Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes("the-access-token"))),
            claims["ath"]!.GetValue<string>());
    }

    [Fact]
    public void ASignInWithBlueskyIsKeptWhole_AndItsKeyNeverShowsInAString()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        var http = new OneHandlerClients(new FakeBluesky(() => clock.UtcNow));
        var session = new BlueskySession(new BlueskyClient(http, clock), new PlainProtector(), clock, new BlueskyOAuth(http, new PlainProtector(), clock));
        var dpopKey = BlueskyKeys.NewPrivateJwk();
        var grant = new BlueskyOAuthGrant(FakeBluesky.AuthServer, FakeBluesky.TokenEndpoint, BlueskyOAuth.ClientIdFor(Public)!, dpopKey, clock.UtcNow.AddMinutes(5), BlueskyOAuth.NarrowScope);
        var tokens = new BlueskyTokens("access", "refresh", FakeBluesky.Did, FakeBluesky.Handle, grant);

        var read = session.Read(session.Protect(tokens))!;

        Assert.Equal(tokens.AccessJwt, read.AccessJwt);
        Assert.Equal(tokens.RefreshJwt, read.RefreshJwt);
        Assert.Equal(grant, read.OAuth);
        Assert.DoesNotContain(dpopKey, grant.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("access", tokens.ToString(), StringComparison.Ordinal);

        // A five-minute token is used, and renewed only in its last minute.
        Assert.False(BlueskySession.NeedsRenewing(read, clock.UtcNow));
        Assert.True(BlueskySession.NeedsRenewing(read, clock.UtcNow.AddMinutes(4.5)));
    }

    /// <summary>Stored as it is: the protector's own tests cover the encryption.</summary>
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string? Unprotect(string? ciphertext) => ciphertext;
    }
}
