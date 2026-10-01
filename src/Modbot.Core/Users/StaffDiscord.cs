using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Users;

/// <summary>
/// Which Discord account a staff account is, as far as Modbot will act on it (accounts and access
/// design §4.6). The one place that decides; every command, direct message and Discord action goes
/// through it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Proven ids count.</strong> A person proves their Discord account by signing in to
/// Discord from their account page; the id Discord gave back is stored with the time.
/// </para>
/// <para>
/// <strong>Typed ids count until <see cref="TypedIdsEnd"/>, then never.</strong> Before proving
/// existed, the id was a field anyone could type into their own account, and an administrator into
/// anybody's. Nothing checked it, two accounts could hold the same one, and the bot treated whoever
/// held that Discord account as the Modbot account. Those ids are kept, shown as not proven, and go
/// on working for a month so nobody's commands stop without warning; the date is on the account
/// page. Where two accounts typed the same id, neither counts: the bot cannot tell which is meant.
/// </para>
/// </remarks>
public static class StaffDiscord
{
    /// <summary>The day typed-in Discord ids stop counting.</summary>
    public static readonly DateTimeOffset TypedIdsEnd = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Whether an id that was typed in rather than proven still counts at <paramref name="now"/>.</summary>
    public static bool TypedIdsCount(DateTimeOffset now) => now < TypedIdsEnd;

    /// <summary>
    /// The Discord id Modbot may treat as this person at <paramref name="now"/>, or null. Says
    /// nothing about whether another account typed the same id; use <see cref="AccountForAsync"/>
    /// to go from Discord to an account.
    /// </summary>
    public static string? IdOf(ModbotUser user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.DiscordUserId is not { Length: > 0 } id)
            return null;

        return user.DiscordVerifiedAt is not null || TypedIdsCount(now) ? id : null;
    }

    /// <summary>
    /// For an id that was typed in and not proven, the day it stops counting; null for a proven id
    /// or none. What the account page and the users page show beside it.
    /// </summary>
    public static DateTimeOffset? TypedIdWorksUntil(ModbotUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.DiscordUserId is { Length: > 0 } && user.DiscordVerifiedAt is null ? TypedIdsEnd : null;
    }

    /// <summary>
    /// The staff account this Discord account is, with its roles, or null when there is none it can
    /// be. Not tracked. A disabled account is returned: the caller says it is disabled.
    /// </summary>
    /// <remarks>
    /// The proven account wins over any that typed the same id. Without one, a typed id counts
    /// only while typed ids do, and only when a single account typed it.
    /// </remarks>
    public static async Task<ModbotUser?> AccountForAsync(
        ModbotContext db, string discordUserId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (string.IsNullOrEmpty(discordUserId))
            return null;

        var accounts = await db.Users.AsNoTracking()
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .Where(u => u.DiscordUserId == discordUserId && u.DeletedAt == null)
            .OrderBy(u => u.DiscordVerifiedAt == null)
            .Take(3)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (accounts.Count == 0)
            return null;

        if (accounts[0].DiscordVerifiedAt is not null)
            return accounts[0];

        return accounts.Count == 1 && TypedIdsCount(now) ? accounts[0] : null;
    }
}
