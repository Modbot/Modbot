using System.Globalization;
using System.Security.Cryptography;

namespace Modbot.Core.Users;

/// <summary>
/// The name a deleted account is left under (username rules and deleting accounts design §3.2).
/// </summary>
/// <remarks>
/// <para>
/// Deleting an account replaces everything that says who it belonged to and keeps everything it
/// did, so the row has to keep a name — a case file whose author is a blank is worse to read than
/// one whose author is <c>deleted_user_9fb1c47a</c>. The name is worked out from the account's own
/// id, so it is the same every time it is computed and two deleted accounts never land on the same
/// one.
/// </para>
/// <para>
/// <strong>Eight characters of a hash, not of the id.</strong> Version 7 ids start with the time
/// they were made, so two accounts created in the same session share their first characters; the
/// hash spreads them. Eight hex characters is short enough to read out and to tell two rows apart
/// at a glance, and it is only a first choice anyway — <see cref="NameFor"/>'s caller falls back to
/// <see cref="LongNameFor"/>, which carries the whole id and so cannot collide at all.
/// </para>
/// <para>
/// It is lower case with underscores because that is a name the ordinary rule
/// (<see cref="UsernameRules"/>) already accepts: the stored username and the name on the screen
/// are one string, and nothing has to make an exception for it.
/// </para>
/// </remarks>
public static class DeletedAccount
{
    public const string Prefix = "deleted_user_";

    private const int SuffixLength = 8;

    /// <summary>The short name for this account, the one a screen normally shows.</summary>
    public static string NameFor(Guid id)
    {
        var hash = SHA256.HashData(id.ToByteArray());
        return Prefix + Convert.ToHexString(hash, 0, SuffixLength / 2).ToLowerInvariant();
    }

    /// <summary>
    /// The name that cannot collide, for the case where something already holds the short one.
    /// </summary>
    public static string LongNameFor(Guid id)
        => Prefix + id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Whether this is a name a deletion left behind.</summary>
    public static bool IsOne(string? username)
        => username is not null && username.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A password nobody typed and nobody will, to be hashed in place of the real one.
    /// </summary>
    /// <remarks>
    /// Hashed rather than blanked, the way the demo's accounts are: a blank hash is a shape the
    /// hasher never produced, and what it does with one is an implementation detail rather than a
    /// promise. Thirty-two random bytes are a password no typed password can match.
    /// </remarks>
    public static string UnguessablePassword()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
