using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modbot.Core.Google;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Google;

/// <summary>
/// The service account sign-in (Google Calendar design §3.1): the JWT is signed with the key and
/// carries the clock's times, the token request goes only to Google's fixed address, and the token
/// is kept, with at most one request a minute.
/// </summary>
public class GoogleSignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static (GoogleCredentials Key, RSA Rsa) TestKey(string tokenUri = "https://oauth2.googleapis.com/token")
    {
        var (json, rsa) = FakeGoogle.KeyFile(tokenUri: tokenUri);
        var file = GoogleKeyFile.Parse(json, out var error)!;
        Assert.Null(error);
        return (new GoogleCredentials(file.ClientEmail, file.KeyId, file.PrivateKeyPem), rsa);
    }

    [Fact]
    public void TheAssertionIsSignedWithTheKeyAndCarriesTheClocksTimes()
    {
        var (key, rsa) = TestKey();
        using var _ = rsa;

        var assertion = GoogleSignIn.Assertion(key, Noon);
        var parts = assertion.Split('.');
        Assert.Equal(3, parts.Length);

        // The signature checks out with the public half of the same key.
        Assert.True(rsa.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        using var head = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]));
        Assert.Equal("RS256", head.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", head.RootElement.GetProperty("typ").GetString());
        Assert.Equal(key.KeyId, head.RootElement.GetProperty("kid").GetString());

        using var body = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
        var claims = body.RootElement;
        Assert.Equal(key.ClientEmail, claims.GetProperty("iss").GetString());
        Assert.Equal("https://oauth2.googleapis.com/token", claims.GetProperty("aud").GetString());
        Assert.Equal(GoogleSignIn.Scopes, claims.GetProperty("scope").GetString());
        Assert.Equal(Noon.ToUnixTimeSeconds(), claims.GetProperty("iat").GetInt64());
        Assert.Equal(Noon.AddMinutes(55).ToUnixTimeSeconds(), claims.GetProperty("exp").GetInt64());
    }

    [Fact]
    public void AnotherKeysSignatureDoesNotCheckOut()
    {
        var (key, rsa) = TestKey();
        using var _ = rsa;
        using var other = RSA.Create(2048);

        var parts = GoogleSignIn.Assertion(key, Noon).Split('.');

        Assert.False(other.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task TheTokenRequestGoesToGooglesOwnAddress_WhateverTheKeyFileSays()
    {
        var (key, rsa) = TestKey(tokenUri: "https://collector.example/steal");
        using var _ = rsa;
        var google = new FakeGoogle();
        var signIn = new GoogleSignIn(new OneHandlerClients(google), new FakeClock(Noon));

        var token = await signIn.TokenAsync(key, fresh: false, Ct);

        Assert.Equal(google.AccessToken, token.Value);
        var request = Assert.Single(google.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://oauth2.googleapis.com/token"), request.Uri);
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", request.Form["grant_type"]);
        Assert.Equal(GoogleSignIn.Assertion(key, Noon), request.Form["assertion"]);
    }

    [Fact]
    public async Task TheTokenIsKeptUntilFiveMinutesBeforeItEnds()
    {
        var (key, rsa) = TestKey();
        using var _ = rsa;
        var google = new FakeGoogle();
        var clock = new FakeClock(Noon);
        var signIn = new GoogleSignIn(new OneHandlerClients(google), clock);

        await signIn.TokenAsync(key, fresh: false, Ct);
        clock.Advance(TimeSpan.FromMinutes(54));
        await signIn.TokenAsync(key, fresh: false, Ct);
        Assert.Single(google.Requests);

        // Google's token lasts 3,599 seconds; under five minutes left is time for a new one.
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await signIn.TokenAsync(key, fresh: false, Ct);
        Assert.Equal(2, google.Requests.Count);
    }

    [Fact]
    public async Task AFreshTokenIsAskedForAtMostOnceAMinute()
    {
        var (key, rsa) = TestKey();
        using var _ = rsa;
        var google = new FakeGoogle();
        var clock = new FakeClock(Noon);
        var signIn = new GoogleSignIn(new OneHandlerClients(google), clock);

        await signIn.TokenAsync(key, fresh: true, Ct);
        clock.Advance(TimeSpan.FromSeconds(30));
        await signIn.TokenAsync(key, fresh: true, Ct);
        Assert.Single(google.Requests);

        clock.Advance(TimeSpan.FromSeconds(31));
        await signIn.TokenAsync(key, fresh: true, Ct);
        Assert.Equal(2, google.Requests.Count);
    }

    [Fact]
    public async Task ARefusedKeyIsNotAskedAboutAgainInsideTheMinute()
    {
        var (key, rsa) = TestKey();
        using var _ = rsa;
        var google = new FakeGoogle { TokenStatus = HttpStatusCode.BadRequest };
        var clock = new FakeClock(Noon);
        var signIn = new GoogleSignIn(new OneHandlerClients(google), clock);

        var first = await signIn.TokenAsync(key, fresh: true, Ct);
        clock.Advance(TimeSpan.FromSeconds(20));
        var second = await signIn.TokenAsync(key, fresh: true, Ct);

        Assert.Equal(GoogleProblem.KeyRefused, first.Failure?.Problem);
        Assert.Equal(GoogleProblem.KeyRefused, second.Failure?.Problem);
        Assert.Single(google.Requests);
    }

    [Fact]
    public async Task ANewKeyIsNotHandedTheOldKeysToken()
    {
        var (first, rsa1) = TestKey();
        using var _1 = rsa1;
        var (json, rsa2) = FakeGoogle.KeyFile(keyId: "fedcba9876543210");
        using var _2 = rsa2;
        var file = GoogleKeyFile.Parse(json, out _)!;
        var second = new GoogleCredentials(file.ClientEmail, file.KeyId, file.PrivateKeyPem);

        var google = new FakeGoogle();
        var signIn = new GoogleSignIn(new OneHandlerClients(google), new FakeClock(Noon));

        await signIn.TokenAsync(first, fresh: false, Ct);
        await signIn.TokenAsync(second, fresh: false, Ct);

        Assert.Equal(2, google.Requests.Count);
    }

    [Fact]
    public async Task ATokenRequestRefusedForTheClockSaysTheClockIsOff()
    {
        var (key, rsa) = TestKey();
        using var _ = rsa;
        var google = new FakeGoogle
        {
            TokenStatus = HttpStatusCode.BadRequest,
            TokenError = """{"error":"invalid_grant","error_description":"Invalid JWT: Token must be a short-lived token (60 minutes) and in a reasonable timeframe. Check your iat and exp values in the JWT claim."}""",
        };
        var signIn = new GoogleSignIn(new OneHandlerClients(google), new FakeClock(Noon));

        var token = await signIn.TokenAsync(key, fresh: true, Ct);

        Assert.Equal(GoogleProblem.ClockOff, token.Failure?.Problem);
        Assert.Equal("The server's clock is off.", GoogleErrors.Sentence(token.Failure!));
    }

    [Fact]
    public void TheCredentialsNeverPrintTheirKey()
    {
        var (key, rsa) = TestKey();
        using var _ = rsa;

        Assert.DoesNotContain("PRIVATE KEY", key.ToString(), StringComparison.Ordinal);
    }
}
