using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Auth;
using Modbot.Cloud.Data;

namespace Modbot.Cloud.Features.Admin;

/// <summary>
/// Starts, checks and ends <c>/admin</c> sessions.
/// </summary>
/// <remarks>
/// <para>
/// A token is <c>&lt;random&gt;.&lt;signature&gt;</c>: 32 random bytes, and an HMAC-SHA256 of them under
/// a key derived from <c>ROOT_API_KEY</c>. A session is good only while both hold: the signature
/// matches the current key, and a row for the random part exists and has not expired.
/// </para>
/// <para>
/// The signature is what makes changing <c>ROOT_API_KEY</c> end every session at once, with no list
/// to clear. The row is what makes logging out end one.
/// </para>
/// </remarks>
public sealed class AdminSessions(RootApiKey rootKey, TimeProvider time)
{
    public const string CookieName = "modbot_admin";

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private const string Purpose = "Modbot Cloud admin session";
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

    public async Task<(string Token, DateTimeOffset ExpiresAt)> StartAsync(CloudContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var key = rootKey.DeriveKey(Purpose)
            ?? throw new InvalidOperationException("No ROOT_API_KEY is set, so no session can start.");

        var now = time.GetUtcNow();
        var random = RandomNumberGenerator.GetBytes(RandomBytes);

        await db.AdminSessions.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        db.AdminSessions.Add(new AdminSession
        {
            TokenHash = Hash(random),
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });
        await db.SaveChangesAsync(ct);

        var token = $"{Base64Url.EncodeToString(random)}.{Base64Url.EncodeToString(HMACSHA256.HashData(key, random))}";
        return (token, now + Lifetime);
    }

    public async Task<bool> IsValidAsync(CloudContext db, string? token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (RandomPart(token) is not { } random)
            return false;

        var hash = Hash(random);
        var now = time.GetUtcNow();
        return await db.AdminSessions.AnyAsync(s => s.TokenHash == hash && s.ExpiresAt > now, ct);
    }

    public async Task EndAsync(CloudContext db, string? token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (RandomPart(token) is not { } random)
            return;

        var hash = Hash(random);
        await db.AdminSessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync(ct);
    }

    /// <summary>The token's random part, when its signature matches the current key.</summary>
    private byte[]? RandomPart(string? token)
    {
        if (string.IsNullOrEmpty(token) || rootKey.DeriveKey(Purpose) is not { } key)
            return null;

        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
            return null;

        byte[] random, signature;
        try
        {
            random = Base64Url.DecodeFromChars(token.AsSpan(0, dot));
            signature = Base64Url.DecodeFromChars(token.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            return null;
        }

        if (random.Length != RandomBytes)
            return null;

        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, random), signature) ? random : null;
    }

    private static string Hash(byte[] random) => Convert.ToHexStringLower(SHA256.HashData(random));
}
