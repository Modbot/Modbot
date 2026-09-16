using System.Text.RegularExpressions;

namespace Modbot.Core.Logging.Store;

/// <summary>
/// Keeps secrets out of the stored log.
/// </summary>
/// <remarks>
/// <para>
/// The log store is read in the browser by anyone holding <c>ViewOperationalLog</c>, and it is sent
/// to Modbot Cloud. Both are wider audiences than the console and the files, which need a shell on
/// the host. So a property that looks like a credential is replaced with <see cref="Replacement"/>
/// before it is written, and the message is checked for the same words too.
/// </para>
/// <para>
/// <strong>This is a net, not a promise.</strong> The rule Modbot actually relies on is that nothing
/// logs a secret in the first place; the secrets it holds live in encrypted settings columns and are
/// decrypted for the length of one call. This catches the mistake anyway, because the cost of the
/// mistake here is a password on a screen a moderator can open.
/// </para>
/// <para>
/// Matching is on the property <em>name</em>, plus two value shapes that are unmistakable whatever
/// they are called: an <c>Authorization</c>-style bearer or basic credential, and a connection
/// string carrying a password. Names are matched as substrings, case-insensitively, so
/// <c>SmtpPassword</c>, <c>apiKey</c> and <c>vrchat_auth_cookie</c> are all caught.
/// </para>
/// </remarks>
public static partial class LogSecrets
{
    /// <summary>What a redacted value is replaced with.</summary>
    public const string Replacement = "[removed]";

    /// <summary>
    /// Name fragments that make a property a secret. Deliberately broad: a property wrongly
    /// redacted costs one unhelpful log line, and one missed costs a leaked credential.
    /// </summary>
    private static readonly string[] SecretNames =
    [
        "password", "passwd", "secret", "token", "apikey", "api_key", "authorization",
        "credential", "cookie", "privatekey", "private_key", "accesskey", "access_key",
        "sessionid", "session_id", "bearer", "signature", "passphrase",
    ];

    /// <summary>Whether a property with this name must never have its value stored.</summary>
    public static bool IsSecretName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var fragment in SecretNames)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The value with anything that looks like a credential taken out, whatever it was called.
    /// </summary>
    public static string Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        // Ordered cheapest-first: most lines contain none of these words, and IndexOf over a short
        // string beats running three regular expressions on every log line Modbot writes.
        if (value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            value = AuthorizationValue().Replace(value, "$1 " + Replacement);
        }

        if (value.Contains("password", StringComparison.OrdinalIgnoreCase)
            || value.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || value.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || value.Contains("apikey", StringComparison.OrdinalIgnoreCase))
        {
            value = KeywordAssignment().Replace(value, "$1=" + Replacement);
        }

        return value;
    }

    /// <summary>A bearer or basic credential, however it was embedded.</summary>
    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9\-._~+/=]{8,}", RegexOptions.IgnoreCase, 200)]
    private static partial Regex AuthorizationValue();

    /// <summary>
    /// <c>Password=…</c> in a connection string, and the same shape for the other three words.
    /// Stops at <c>;</c>, a quote or whitespace, which is where every one of these ends.
    /// </summary>
    [GeneratedRegex(
        @"\b(password|secret|api[_-]?key)\s*[=:]\s*(""[^""]*""|'[^']*'|[^;,\s""']+)",
        RegexOptions.IgnoreCase,
        200)]
    private static partial Regex KeywordAssignment();
}
