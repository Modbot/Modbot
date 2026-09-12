namespace Modbot.Core.Security;

/// <summary>
/// Encrypts the secret-bearing columns of <see cref="Data.Entities.Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// Foundation spec section 8.3. The key is generated on first boot and stored in the database, so
/// there is no environment variable to lose and no way to permanently brick an install by
/// redeploying.
/// </para>
/// <para>
/// This protects against casual reading of a database dump and NOTHING MORE -- an attacker with
/// full database access has the key too. That is the correct trade for community groups on
/// managed hosting, where Railway already stores environment variables in plaintext and shows
/// them in its dashboard. Operators who need real encryption at rest should encrypt the database.
/// This is documented plainly in docs/security.md; do not overstate it elsewhere.
/// </para>
/// </remarks>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Returns null for null input, so callers need not null-check every column.</summary>
    string? Unprotect(string? ciphertext);
}
