using System.Security.Cryptography;
using System.Text;
using Modbot.Api.Auth;

namespace Modbot.Api.Features.Webhooks;

/// <summary>
/// Signing webhook deliveries (API keys design §6.5).
/// </summary>
/// <remarks>
/// HMAC-SHA256 over <c>"{timestamp}.{body}"</c>, keyed with the secret's UTF-8 bytes, sent as
/// <c>v1=</c> and lowercase hex. The timestamp is inside the signed text so a captured request
/// cannot be replayed with a new one; <c>v1=</c> leaves room to change the scheme later.
/// </remarks>
public static class WebhookSignature
{
    public const string SecretPrefix = "whsec_";

    public const string EventIdHeader = "Modbot-Event-Id";
    public const string EventTypeHeader = "Modbot-Event-Type";
    public const string TimestampHeader = "Modbot-Timestamp";
    public const string SignatureHeader = "Modbot-Signature";

    public static string NewSecret() => SecretPrefix + ApiKeySecrets.Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Sign(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var prefix = Encoding.UTF8.GetBytes(timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));

        return "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed));
    }
}
