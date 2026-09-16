using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;

namespace Modbot.Cloud.Features.Accounts;

/// <summary>
/// Starts, reads and ends account sessions.
/// </summary>
/// <remarks>
/// <para>
/// A token is 32 random bytes as base64url. Cloud keeps only a SHA-256 of it, with an expiry, in
/// <c>account_session</c>. There is no signature over it, unlike the <c>/admin</c> session: that
/// signature exists so that changing <c>ROOT_API_KEY</c> ends every admin session at once, and an
/// account session has no such key to change. A row and an expiry are the whole of it.
/// </para>
/// <para>
/// The cookie is <c>HttpOnly</c>, <c>Secure</c>, <c>SameSite=Strict</c> — the same rules as the admin
/// cookie, for the same reasons: script cannot read it, it never travels in the clear, and it is not
/// sent on a request another site started.
/// </para>
/// </remarks>
public sealed class AccountSessions(TimeProvider time)
{
    public const string CookieName = "modbot_account";

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private const int RandomBytes = 32;

    public static CookieOptions CookieOptions(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expiresAt,
        IsEssential = true,
    };

    public async Task<(string Token, DateTimeOffset ExpiresAt)> StartAsync(
        CloudContext db, Guid accountId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var now = time.GetUtcNow();
        var random = RandomNumberGenerator.GetBytes(RandomBytes);

        await db.AccountSessions.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        db.AccountSessions.Add(new AccountSession
        {
            TokenHash = Hash(Base64Url.EncodeToString(random)),
            AccountId = accountId,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });
        await db.SaveChangesAsync(ct);

        return (Base64Url.EncodeToString(random), now + Lifetime);
    }

    /// <summary>The account this token belongs to, or null when it is unknown or has lapsed.</summary>
    public async Task<Account?> ReadAsync(CloudContext db, string? token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (string.IsNullOrEmpty(token) || token.Length > 128)
            return null;

        var hash = Hash(token);
        var now = time.GetUtcNow();

        return await db.AccountSessions
            .Where(s => s.TokenHash == hash && s.ExpiresAt > now)
            .Join(db.Accounts, s => s.AccountId, a => a.Id, (_, a) => a)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
    }

    public async Task EndAsync(CloudContext db, string? token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (string.IsNullOrEmpty(token) || token.Length > 128)
            return;

        var hash = Hash(token);
        await db.AccountSessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync(ct);
    }

    /// <summary>Ends every session for an account. A password change is what calls this.</summary>
    public Task EndAllAsync(CloudContext db, Guid accountId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.AccountSessions.Where(s => s.AccountId == accountId).ExecuteDeleteAsync(ct);
    }

    internal static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
