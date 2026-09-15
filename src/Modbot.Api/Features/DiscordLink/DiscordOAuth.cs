using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace Modbot.Api.Features.DiscordLink;

/// <summary>The Discord account a sign-in proved.</summary>
public sealed record DiscordIdentity(string UserId, string Username);

/// <summary>What exchanging a sign-in code came back with.</summary>
public sealed record DiscordSignInResult(DiscordIdentity? Identity, string? Problem);

/// <summary>
/// "Sign in with Discord" for the link page: OAuth2 authorization code with the <c>identify</c>
/// scope, a <c>state</c> value and PKCE (Discord account linking design §2, §3.2).
/// </summary>
/// <remarks>
/// <para>
/// The access token proves one thing -- which Discord account signed in -- and is used for exactly
/// one read of <c>users/@me</c>. It is then revoked and never stored. The client secret is sent
/// with the exchange as well as the PKCE verifier: Modbot is a confidential client, and PKCE on top
/// means a code taken from the redirect cannot be exchanged without the verifier that stayed in the
/// browser's encrypted cookie.
/// </para>
/// <para>
/// Its own named <see cref="HttpClient"/>, so tests can put a fake handler under it and so these
/// requests never go through the VRChat egress proxy.
/// </para>
/// </remarks>
public sealed class DiscordOAuth
{
    public const string HttpClientName = "DiscordOAuth";

    public const string AuthorizeUrl = "https://discord.com/oauth2/authorize";
    public const string ApiBase = "https://discord.com/api/v10/";

    private readonly IHttpClientFactory _http;

    public DiscordOAuth(IHttpClientFactory http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>A fresh random value for <c>state</c> or the PKCE verifier: 32 bytes, base64url, 43 characters.</summary>
    public static string NewRandomValue() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 challenge for a verifier.</summary>
    public static string ChallengeFor(string verifier)
        => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Where the member's browser is sent to sign in.</summary>
    public static string AuthorizeUrlFor(string clientId, string redirectUrl, string state, string verifier)
        => QueryHelpers.AddQueryString(AuthorizeUrl, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["scope"] = "identify",
            ["redirect_uri"] = redirectUrl,
            ["state"] = state,
            ["code_challenge"] = ChallengeFor(verifier),
            ["code_challenge_method"] = "S256",
            ["prompt"] = "none",
        });

    /// <summary>Exchanges the code for a token, reads who signed in, and revokes the token.</summary>
    public async Task<DiscordSignInResult> SignInAsync(
        string clientId,
        string clientSecret,
        string redirectUrl,
        string code,
        string verifier,
        CancellationToken ct)
    {
        using var client = _http.CreateClient(HttpClientName);

        TokenResponse? token;

        try
        {
            using var exchange = await client.PostAsync(
                ApiBase + "oauth2/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = redirectUrl,
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["code_verifier"] = verifier,
                }),
                ct);

            if (!exchange.IsSuccessStatusCode)
                return new DiscordSignInResult(null, $"Discord refused the sign-in code ({(int)exchange.StatusCode}).");

            token = await exchange.Content.ReadFromJsonAsync<TokenResponse>(ct);
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new DiscordSignInResult(null, $"Could not reach Discord: {e.Message}");
        }

        if (string.IsNullOrEmpty(token?.AccessToken))
            return new DiscordSignInResult(null, "Discord sent no access token.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "users/@me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            using var me = await client.SendAsync(request, ct);
            if (!me.IsSuccessStatusCode)
                return new DiscordSignInResult(null, $"Discord would not say who signed in ({(int)me.StatusCode}).");

            var user = await me.Content.ReadFromJsonAsync<UserResponse>(ct);
            if (string.IsNullOrWhiteSpace(user?.Id))
                return new DiscordSignInResult(null, "Discord would not say who signed in.");

            return new DiscordSignInResult(new DiscordIdentity(user.Id, user.Username ?? user.Id), null);
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new DiscordSignInResult(null, $"Could not reach Discord: {e.Message}");
        }
        finally
        {
            await RevokeAsync(client, clientId, clientSecret, token.AccessToken);
        }
    }

    /// <summary>Best effort: a token nobody holds is harmless, but one that no longer works is better.</summary>
    private static async Task RevokeAsync(HttpClient client, string clientId, string clientSecret, string accessToken)
    {
        try
        {
            using var revoked = await client.PostAsync(
                ApiBase + "oauth2/token/revoke",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = accessToken,
                    ["token_type_hint"] = "access_token",
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                }));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // The token expires by itself in a week; nothing else to do.
        }
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);

    private sealed record UserResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("username")] string? Username);
}
