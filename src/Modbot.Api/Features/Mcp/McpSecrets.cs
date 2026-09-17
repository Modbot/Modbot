using System.Security.Cryptography;
using System.Text;
using Modbot.Api.Auth;

namespace Modbot.Api.Features.Mcp;

/// <summary>Making, recognising and hashing the secrets the MCP server hands out (MCP server design).</summary>
/// <remarks>
/// Every secret is 32 random bytes in base64url behind a short prefix that says what it is, so a
/// token pasted into the wrong box is recognised rather than tried. Only SHA-256 hashes are
/// stored, for the same reason API keys store only theirs: the input is 256 random bits, so a
/// slow hash would cost every request and protect nothing.
/// </remarks>
public static class McpSecrets
{
    /// <summary>An access token: what an AI app sends as <c>Authorization: Bearer</c>.</summary>
    public const string AccessPrefix = "mcpa_";

    /// <summary>A refresh token: what an AI app trades for a new access token.</summary>
    public const string RefreshPrefix = "mcpr_";

    /// <summary>An authorization code: what the sign-in page hands back, once.</summary>
    public const string CodePrefix = "mcpc_";

    /// <summary>A client secret, for an app that asked for one at registration.</summary>
    public const string ClientSecretPrefix = "mcps_";

    /// <summary>The one scope the server offers: use the tools as yourself.</summary>
    public const string Scope = "mcp";

    public static readonly TimeSpan AccessTokenLife = TimeSpan.FromHours(1);

    /// <summary>A connection nobody used for this long is over. Sliding: each refresh starts it again.</summary>
    public static readonly TimeSpan RefreshTokenLife = TimeSpan.FromDays(90);

    public static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(10);

    /// <summary>A registered app nobody signed in through is deleted after this.</summary>
    public static readonly TimeSpan UnusedClientLife = TimeSpan.FromDays(1);

    public static string NewAccessToken() => AccessPrefix + Random();

    public static string NewRefreshToken() => RefreshPrefix + Random();

    public static string NewCode() => CodePrefix + Random();

    public static string NewClientSecret() => ClientSecretPrefix + Random();

    /// <summary>Lowercase hex SHA-256, as stored. The same function API keys use.</summary>
    public static string Hash(string secret) => ApiKeySecrets.Hash(secret);

    public static bool LooksLikeAccessToken(string? token)
        => token is { Length: > 16 } && token.StartsWith(AccessPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="verifier"/> answers <paramref name="challenge"/> the S256 way
    /// (RFC 7636): base64url of its SHA-256, compared in constant time.
    /// </summary>
    public static bool VerifierMatches(string? verifier, string challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        if (verifier is null || verifier.Length is < 43 or > 128)
            return false;

        var expected = Encoding.ASCII.GetBytes(ApiKeySecrets.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        var given = Encoding.ASCII.GetBytes(challenge);

        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>Whether a stored hash matches a presented secret, compared in constant time.</summary>
    public static bool HashMatches(string? secret, string? hash)
    {
        if (secret is null || hash is null)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(secret)),
            Encoding.ASCII.GetBytes(hash));
    }

    private static string Random() => ApiKeySecrets.Base64Url(RandomNumberGenerator.GetBytes(32));
}
