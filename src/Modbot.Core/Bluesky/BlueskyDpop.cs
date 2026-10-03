using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modbot.Core.Bluesky;

/// <summary>
/// DPoP proofs (RFC 9449), which Bluesky's sign-in asks for on every call made with its tokens: a
/// small JWT signed with the sign-in's own key, naming the call, so a token copied elsewhere is no use
/// without the key.
/// </summary>
public static class BlueskyDpop
{
    /// <summary>The header a proof goes in, and the one a server's nonce comes back in.</summary>
    public const string Header = "DPoP";

    /// <summary>The header a server's nonce comes back in.</summary>
    public const string NonceHeader = "DPoP-Nonce";

    /// <summary>The error a server answers with when it wants a (new) nonce in the proof.</summary>
    public const string UseNonce = "use_dpop_nonce";

    /// <summary>
    /// A proof for one call: <c>htm</c> and <c>htu</c> (the address without its query), <c>iat</c> from
    /// Modbot's clock, a fresh <c>jti</c>, the server's last nonce, and with a token the hash of it
    /// (<c>ath</c>). The header carries the key's public half only.
    /// </summary>
    public static string Proof(string dpopKey, HttpMethod method, Uri url, string? nonce, string? accessToken, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(url);

        var header = new JsonObject
        {
            ["typ"] = "dpop+jwt",
            ["jwk"] = BlueskyKeys.PublicJwk(dpopKey, withUse: false),
        };

        var payload = new JsonObject
        {
            ["jti"] = BlueskyKeys.Random(16),
            ["htm"] = method.Method,
            ["htu"] = url.GetLeftPart(UriPartial.Path),
            ["iat"] = now.ToUnixTimeSeconds(),
        };

        if (!string.IsNullOrEmpty(nonce))
            payload["nonce"] = nonce;

        if (!string.IsNullOrEmpty(accessToken))
            payload["ath"] = BlueskyKeys.Sha256(accessToken);

        return BlueskyKeys.Sign(dpopKey, header, payload);
    }

    /// <summary>
    /// Whether the server refused the call only for want of a nonce: an account server says so in
    /// <c>WWW-Authenticate</c> with a 401, a sign-in server in the body's <c>error</c> with a 400.
    /// </summary>
    public static bool AsksForNonce(HttpResponseMessage response, string? body)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest))
            return false;

        foreach (var challenge in response.Headers.WwwAuthenticate)
        {
            if (string.Equals(challenge.Scheme, Header, StringComparison.OrdinalIgnoreCase)
                && challenge.Parameter?.Contains(UseNonce, StringComparison.Ordinal) == true)
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() == UseNonce;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// The last DPoP nonce each server gave, by its origin, kept in memory: a nonce lives five minutes at
/// most, and a server that wants a newer one says so and the call is made once more with it.
/// </summary>
public sealed class BlueskyNonces
{
    private readonly ConcurrentDictionary<string, string> _nonces = new(StringComparer.Ordinal);

    /// <summary>The last nonce <paramref name="url"/>'s server gave, or null.</summary>
    public string? For(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return _nonces.TryGetValue(url.GetLeftPart(UriPartial.Authority), out var nonce) ? nonce : null;
    }

    /// <summary>Keeps the nonce in <paramref name="response"/>, if it carries one. Answers it, or null.</summary>
    public string? Keep(Uri url, HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(response);

        if (!response.Headers.TryGetValues(BlueskyDpop.NonceHeader, out var values)
            || values.FirstOrDefault() is not { Length: > 0 and <= 512 } nonce)
        {
            return null;
        }

        _nonces[url.GetLeftPart(UriPartial.Authority)] = nonce;
        return nonce;
    }
}
