using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Core.Users;

/// <summary>
/// Creates staff accounts and checks passwords.
/// </summary>
/// <remarks>
/// Shared kernel rather than a feature slice: both the login slice and the onboarding wizard's
/// create-administrator step need it, and spec 2.8 says shared logic moves into
/// <c>Modbot.Core</c> rather than being reached into from another slice.
/// </remarks>
public sealed class UserAccountService
{
    /// <summary>
    /// A real hash of a throwaway password, verified against when no account matches, so an
    /// unknown username costs the same time as a known one. Without it, response latency is a
    /// user-enumeration oracle on the login endpoint.
    /// </summary>
    private static readonly string DecoyHash =
        new PasswordHasher<ModbotUser>().HashPassword(new ModbotUser(), "no account matched");

    private readonly ModbotContext _db;
    private readonly IPasswordHasher<ModbotUser> _hasher;
    private readonly IModbotClock _clock;

    public UserAccountService(ModbotContext db, IPasswordHasher<ModbotUser> hasher, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _hasher = hasher;
        _clock = clock;
    }

    /// <summary>
    /// True when no staff account exists yet. Spec 7.1: that is the condition for first run.
    /// </summary>
    public Task<bool> AnyUsersAsync(CancellationToken ct = default)
        => _db.Users.AnyAsync(ct);

    public async Task<ModbotUser> CreateAsync(
        string username,
        string password,
        ModbotPermissions permissions,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var user = new ModbotUser
        {
            Username = username.Trim(),
            UsernameNormalized = Normalize(username),
            Permissions = permissions,
            CreatedAt = _clock.UtcNow,
        };

        user.PasswordHash = _hasher.HashPassword(user, password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        return user;
    }

    /// <summary>
    /// Returns the account when the password is right, and null otherwise -- never a reason.
    /// The caller cannot tell "no such user" from "wrong password" from "disabled", because a
    /// login form that distinguishes them is a membership oracle for anyone who finds it.
    /// </summary>
    public async Task<ModbotUser?> VerifyCredentialsAsync(
        string username,
        string password,
        CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(username) ? string.Empty : Normalize(username);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.UsernameNormalized == normalized, ct);

        if (user is null || user.IsDisabled)
        {
            _hasher.VerifyHashedPassword(new ModbotUser(), DecoyHash, password ?? string.Empty);
            return null;
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? string.Empty);
        if (result == PasswordVerificationResult.Failed)
            return null;

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = _hasher.HashPassword(user, password!);

        user.LastLoginAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);

        return user;
    }

    public async Task SetPasswordAsync(ModbotUser user, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrEmpty(password);

        user.PasswordHash = _hasher.HashPassword(user, password);
        await _db.SaveChangesAsync(ct);
    }

    private static string Normalize(string username) => username.Trim().ToUpperInvariant();
}
