using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Users;

/// <summary>
/// Makes and finds invite links and reset links (accounts and access design §4.1).
/// </summary>
/// <remarks>
/// The token is 32 random bytes; only its SHA-256 is stored, so a database read yields no working
/// link. The full URL is built from the request that asked for it, because the SPA and the API
/// share an origin and the deployment has no other idea what its public address is.
/// </remarks>
public sealed class OneTimeLinkService
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(72);
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(24);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public OneTimeLinkService(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    /// <summary>An invite carrying these roles. Saves; the caller owns any transaction.</summary>
    public async Task<(OneTimeLink Link, string Token)> CreateInviteAsync(
        Guid createdBy, IReadOnlyCollection<Guid> roleIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        var token = NewToken();
        var now = _clock.UtcNow;

        var link = new OneTimeLink
        {
            Kind = OneTimeLinkKind.Invite,
            TokenHash = Hash(token),
            CreatedByUserId = createdBy,
            CreatedAt = now,
            ExpiresAt = now + InviteLifetime,
            RoleIds = roleIds.Distinct().ToList(),
        };

        _db.OneTimeLinks.Add(link);
        await _db.SaveChangesAsync(ct);

        return (link, token);
    }

    /// <summary>
    /// A reset link for one account. Earlier unused reset links for that account are removed:
    /// a new link is a statement that the old ones should not work.
    /// </summary>
    public async Task<(OneTimeLink Link, string Token)> CreateResetAsync(
        Guid createdBy, Guid userId, CancellationToken ct)
    {
        await _db.OneTimeLinks
            .Where(l => l.Kind == OneTimeLinkKind.PasswordReset && l.UserId == userId && l.UsedAt == null)
            .ExecuteDeleteAsync(ct);

        var token = NewToken();
        var now = _clock.UtcNow;

        var link = new OneTimeLink
        {
            Kind = OneTimeLinkKind.PasswordReset,
            TokenHash = Hash(token),
            CreatedByUserId = createdBy,
            CreatedAt = now,
            ExpiresAt = now + ResetLifetime,
            UserId = userId,
        };

        _db.OneTimeLinks.Add(link);
        await _db.SaveChangesAsync(ct);

        return (link, token);
    }

    /// <summary>The link this token names, of this kind, whatever state it is in. Null if none.</summary>
    public Task<OneTimeLink?> FindAsync(string? token, OneTimeLinkKind kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult<OneTimeLink?>(null);

        var hash = Hash(token.Trim());
        return _db.OneTimeLinks.FirstOrDefaultAsync(l => l.TokenHash == hash && l.Kind == kind, ct);
    }

    /// <summary>The path a person opens, relative to wherever Modbot lives. The path says what the link is for.</summary>
    public static string PathFor(OneTimeLink link, string token)
    {
        ArgumentNullException.ThrowIfNull(link);

        var path = link.Kind == OneTimeLinkKind.Invite ? "join" : "reset";
        return $"/{path}/{token}";
    }

    /// <summary>
    /// The full address, built from the saved public address and nothing else. Null when none is
    /// saved.
    /// </summary>
    /// <remarks>
    /// <strong>Never from the request.</strong> <c>Request.Host</c> and the <c>X-Forwarded-*</c>
    /// headers are chosen by whoever sent the request, and a forgot-password request can be sent
    /// by anyone for anyone: a forged host would put the attacker's address in the victim's
    /// genuine reset email, and the token would go to the attacker when the victim clicked it.
    /// The administrator's copyable links do not need this at all -- the browser showing them
    /// knows its own address -- so those carry only the path.
    /// </remarks>
    public static string? UrlFor(string? publicAddress, OneTimeLink link, string token)
        => string.IsNullOrEmpty(publicAddress) ? null : publicAddress + PathFor(link, token);

    /// <summary>The saved public address, or null. Read fresh each time; it is a setting.</summary>
    public async Task<string?> PublicAddressAsync(CancellationToken ct)
        => await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.PublicAddress)
            .FirstOrDefaultAsync(ct);

    public static string NewToken()
        => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
