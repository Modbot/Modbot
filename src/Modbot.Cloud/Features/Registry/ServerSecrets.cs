using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Modbot.Cloud.Features.Registry;

/// <summary>
/// Making, storing and checking a registered server's secret, and the link code it shows its owner.
/// </summary>
/// <remarks>
/// <para>
/// The same scheme as a desktop install's secret, deliberately: 32 random bytes as base64url, handed
/// over once, kept only as a SHA-256, and sent back as
/// <c>Authorization: Bearer &lt;serverId&gt;.&lt;secret&gt;</c>. The id says which row to check, so
/// checking is one primary-key read and one hash comparison.
/// </para>
/// <para>
/// The link code is a different thing and is short on purpose: a person reads it off one screen and
/// types it into another. Eight characters from a 32-character alphabet with no letter that can be
/// mistaken for a digit, one live code per server, fifteen minutes, single use.
/// </para>
/// </remarks>
public static class ServerSecrets
{
    private const int SecretBytes = 32;
    private const string BearerPrefix = "Bearer ";

    /// <summary>How long a link code lasts.</summary>
    public static readonly TimeSpan LinkCodeLifetime = TimeSpan.FromMinutes(15);

    public const int LinkCodeLength = 8;

    /// <summary>
    /// Crockford's alphabet: no <c>I</c>, <c>L</c>, <c>O</c> or <c>U</c>, so nothing reads as a one,
    /// a zero, or a word nobody wants on their screen.
    /// </summary>
    public const string LinkCodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));

    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Compares in the same time whatever the guess.</summary>
    public static bool Matches(string storedHash, string candidate) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash),
            Encoding.ASCII.GetBytes(Hash(candidate)));

    /// <summary>A fresh link code, in the alphabet above.</summary>
    public static string NewLinkCode() =>
        RandomNumberGenerator.GetString(LinkCodeAlphabet, LinkCodeLength);

    /// <summary>
    /// The code as it is compared: upper case, with spaces and dashes a person may have typed
    /// removed. Null when it is not a code at all.
    /// </summary>
    public static string? CleanLinkCode(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed) || typed.Length > 32)
            return null;

        var cleaned = new string([.. typed.ToUpperInvariant().Where(LinkCodeAlphabet.Contains)]);
        return cleaned.Length == LinkCodeLength ? cleaned : null;
    }

    /// <summary>Reads <c>Bearer &lt;id&gt;.&lt;secret&gt;</c>. False for anything else.</summary>
    public static bool TryRead(string? authorization, out Guid serverId, out string secret)
    {
        serverId = Guid.Empty;
        secret = string.Empty;

        if (authorization is null || !authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = authorization[BearerPrefix.Length..].Trim();
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == value.Length - 1 || value.Length > 128)
            return false;

        if (!Guid.TryParseExact(value[..dot], "D", out serverId))
            return false;

        secret = value[(dot + 1)..];
        return true;
    }
}
