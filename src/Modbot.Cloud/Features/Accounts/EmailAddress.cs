using System.Diagnostics.CodeAnalysis;

namespace Modbot.Cloud.Features.Accounts;

/// <summary>
/// The one rule for an email address, so every table and every lookup holds it the same way.
/// </summary>
/// <remarks>
/// Deliberately loose. An address is checked by sending mail to it, not by a regular expression: the
/// grammar in RFC 5321 admits addresses that look wrong and rejects nothing a real mail server would
/// accept, and every clever pattern anybody writes turns away somebody's real address. All this does
/// is insist on one <c>@</c> with something either side and no spaces, and fold the whole thing to
/// lower case so that two people cannot register the same address in different cases.
/// </remarks>
public static class EmailAddress
{
    public static bool TryNormalise(string? raw, [NotNullWhen(true)] out string? address)
    {
        address = null;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        if (trimmed.Length > Account.MaxEmailLength || trimmed.Any(char.IsWhiteSpace) || trimmed.Any(char.IsControl))
            return false;

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
            return false;

        // A domain with no dot is a local name, not something mail from the internet reaches.
        var domain = trimmed[(at + 1)..];
        if (!domain.Contains('.', StringComparison.Ordinal) || domain.StartsWith('.') || domain.EndsWith('.'))
            return false;

        address = trimmed.ToLowerInvariant();
        return true;
    }
}
