namespace Modbot.Core.Users;

/// <summary>
/// The one place that decides what an email address looks like and how it is stored.
/// </summary>
/// <remarks>
/// <para>
/// Every account must have one (accounts and access design §4.5), so the check and the stored form
/// have to agree everywhere an account is made — the wizard's first administrator, the users page,
/// an invite being accepted, and the account page. One file, four callers.
/// </para>
/// <para>
/// <strong>The check is deliberately loose:</strong> something, an <c>@</c>, something with a dot
/// in it. A strict RFC 5322 pattern rejects addresses that real relays deliver to, and an address
/// that is wrong in a way this cannot see is discovered the first time mail to it bounces — which
/// is what actually finds typos. The check exists to catch a Discord handle typed into an email
/// box, not to prove deliverability.
/// </para>
/// </remarks>
public static class EmailAddress
{
    public const int MaximumLength = 256;

    /// <summary>Null for nothing at all; otherwise trimmed and lower-cased.</summary>
    /// <remarks>
    /// Lower-cased because uniqueness is case-insensitive (design §4.5) and the stored value
    /// <em>is</em> the unique index. Normalising on the way in means the index needs no expression
    /// and no second column, and a lookup is an equality test against what the person typed,
    /// lower-cased the same way.
    /// </remarks>
    public static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>Something, an <c>@</c>, then something with a dot in it.</summary>
    public static bool LooksLike(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength)
            return false;

        if (value.Contains(' ', StringComparison.Ordinal))
            return false;

        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != value.LastIndexOf('@'))
            return false;

        var domain = value[(at + 1)..];

        // A dot with something on both sides of it. "alice@localhost" is a real address on a real
        // network and not one a volunteer moderator is ever reachable at.
        var dot = domain.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && dot < domain.Length - 1;
    }

    /// <summary>The stored form, or the sentence to hand back. Both never at once.</summary>
    public static (string? Email, string? Problem) Read(string? value)
    {
        var email = Normalize(value);

        if (email is null)
            return (null, Required);

        return LooksLike(email) ? (email, null) : (null, NotAnAddress);
    }

    public const string Required = "An email address is required.";

    public const string NotAnAddress = "That email address does not look like one.";

    /// <summary>What a second account claiming an address already in use is told.</summary>
    public const string Taken = "That email address is already used by another account.";
}
