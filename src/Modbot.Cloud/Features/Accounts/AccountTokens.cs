using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;

namespace Modbot.Cloud.Features.Accounts;

/// <summary>
/// Makes and spends the one-time tokens Cloud emails.
/// </summary>
/// <remarks>
/// A token is 32 random bytes as base64url and is stored only as a SHA-256. Spending one reads the
/// row and then deletes it, and the delete is the gate: whichever of two requests carrying the same
/// token deletes the row is the one that succeeds, and the other is told the token is no good.
/// </remarks>
public sealed class AccountTokens(TimeProvider time)
{
    /// <summary>Long enough that a mail delayed by a spam filter still works.</summary>
    public static readonly TimeSpan VerifyLifetime = TimeSpan.FromHours(24);

    /// <summary>Short, because it is the one token that sets a password without knowing the old one.</summary>
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    private const int RandomBytes = 32;

    /// <summary>
    /// Replaces any live token of this purpose for the account, and returns the new one in the clear.
    /// It is never readable again.
    /// </summary>
    /// <param name="email">The address a change-of-address token moves the account to. Null otherwise.</param>
    public async Task<string> IssueAsync(
        CloudContext db,
        Guid accountId,
        string purpose,
        TimeSpan lifetime,
        string? email,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var now = time.GetUtcNow();
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));

        // One live token per purpose: asking for a second verification mail must not leave the first
        // one working, or a token read from an old mail on a shared machine stays good for a day.
        await db.AccountTokens
            .Where(t => t.AccountId == accountId && t.Purpose == purpose)
            .ExecuteDeleteAsync(ct);

        await db.AccountTokens.Where(t => t.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        db.AccountTokens.Add(new AccountToken
        {
            TokenHash = Hash(token),
            AccountId = accountId,
            Purpose = purpose,
            Email = email,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        });
        await db.SaveChangesAsync(ct);

        return token;
    }

    /// <summary>
    /// Takes the token, removing it, and hands back what it said. Null when it is unknown, expired,
    /// for another purpose, or already spent.
    /// </summary>
    public async Task<AccountToken?> SpendAsync(CloudContext db, string? token, string purpose, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (string.IsNullOrEmpty(token) || token.Length > 128)
            return null;

        var hash = Hash(token);
        var now = time.GetUtcNow();

        var row = await db.AccountTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == purpose && t.ExpiresAt > now, ct);

        if (row is null)
            return null;

        // The delete decides. Two requests can both read the row; only one can remove it.
        var removed = await db.AccountTokens.Where(t => t.TokenHash == hash).ExecuteDeleteAsync(ct);

        return removed == 1 ? row : null;
    }

    internal static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
