using System.Diagnostics.CodeAnalysis;

namespace Modbot.Cloud.Common;

/// <summary>
/// The one rule for a Modbot server's address, shared by the registry and the selector's saves so
/// that both hold addresses in the same shape and can be compared.
/// </summary>
public static class ServerUrl
{
    public const int MaxLength = 2048;

    /// <summary>
    /// Accepts an absolute https URL with no credentials in it, and keeps only its origin:
    /// <c>https://modbot.example/settings?x=1</c> becomes <c>https://modbot.example</c>.
    /// </summary>
    public static bool TryNormalise(string? raw, [NotNullWhen(true)] out string? origin)
    {
        origin = null;

        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxLength)
            return false;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;

        // A URL with a username or password in it is refused rather than stored.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
