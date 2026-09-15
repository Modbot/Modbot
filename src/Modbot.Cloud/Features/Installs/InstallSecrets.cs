using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Modbot.Cloud.Features.Installs;

/// <summary>
/// Making, storing and checking an install's secret.
/// </summary>
/// <remarks>
/// <para>
/// A secret is 32 random bytes, written as base64url: 43 characters. It is handed to the client
/// once, at registration, and Cloud keeps only its SHA-256. The client keeps it encrypted to the
/// Windows account (cloud event backup spec 3.2).
/// </para>
/// <para>
/// A request carries <c>Authorization: Bearer &lt;installId&gt;.&lt;secret&gt;</c>. The id says which
/// row to check, so checking is one primary-key read and one hash comparison.
/// </para>
/// </remarks>
public static class InstallSecrets
{
    private const int SecretBytes = 32;
    private const string BearerPrefix = "Bearer ";

    public static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));

    public static string Hash(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Compares in the same time whatever the guess.</summary>
    public static bool Matches(string storedHash, string candidate) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash),
            Encoding.ASCII.GetBytes(Hash(candidate)));

    /// <summary>Reads <c>Bearer &lt;id&gt;.&lt;secret&gt;</c>. False for anything else.</summary>
    public static bool TryRead(string? authorization, out Guid installId, out string secret)
    {
        installId = Guid.Empty;
        secret = string.Empty;

        if (authorization is null || !authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = authorization[BearerPrefix.Length..].Trim();
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == value.Length - 1 || value.Length > 128)
            return false;

        if (!Guid.TryParseExact(value[..dot], "D", out installId))
            return false;

        secret = value[(dot + 1)..];
        return true;
    }
}
