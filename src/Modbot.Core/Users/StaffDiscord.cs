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
    /// The Discord id this account's own row allows at <paramref name="now"/>: proven, or typed and
    /// before <see cref="TypedIdsEnd"/>. Does not look at other accounts, so a typed id another
    /// account holds too still passes here; anything that acts or sends uses
    /// <see cref="CountedIdAsync"/>, <see cref="CountedIdsAsync"/> or <see cref="AccountForAsync"/>,
    /// which do.
    /// </summary>
    public static string? IdOf(ModbotUser user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        return IdOf(user.DiscordUserId, user.DiscordVerifiedAt, now);
    }

    private static string? IdOf(string? discordUserId, DateTimeOffset? verifiedAt, DateTimeOffset now)
    {
        if (discordUserId is not { Length: > 0 } id)
            return null;

        return verifiedAt is not null || TypedIdsCount(now) ? id : null;
    }

    /// <summary>
    /// The Discord id that counts for this account at <paramref name="now"/>, or null: proven; or
    /// typed, before <see cref="TypedIdsEnd"/>, and held by no other account.
    /// </summary>
    public static async Task<string?> CountedIdAsync(ModbotContext db, ModbotUser user, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        var counted = await CountedIdsAsync(db, [(user.Id, user.DiscordUserId, user.DiscordVerifiedAt)], now, ct).ConfigureAwait(false);
        return counted.GetValueOrDefault(user.Id);
    }

    /// <summary>
    /// <see cref="CountedIdAsync"/> for many accounts in one query: the id that counts for each one
    /// that has one. Accounts without one are left out.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> CountedIdsAsync(
        ModbotContext db,
        IEnumerable<(Guid Id, string? DiscordUserId, DateTimeOffset? DiscordVerifiedAt)> accounts,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(accounts);

        var counted = new Dictionary<Guid, string>();
        var typed = new List<(Guid Id, string DiscordUserId)>();

        foreach (var (id, discordUserId, verifiedAt) in accounts)
        {
            if (IdOf(discordUserId, verifiedAt, now) is not { } allowed)
                continue;

            if (verifiedAt is not null)
                counted[id] = allowed;
            else
                typed.Add((id, allowed));
        }

        if (typed.Count == 0)
            return counted;

        // A typed id another account holds as well, typed or proven, is nobody's: the proven one
        // is the person, and between two typed ones nothing says which.
        var typedIds = typed.Select(t => t.DiscordUserId).Distinct(StringComparer.Ordinal).ToList();
        var shared = await db.Users.AsNoTracking()
            .Where(u => u.DeletedAt == null && u.DiscordUserId != null && typedIds.Contains(u.DiscordUserId))
            .GroupBy(u => u.DiscordUserId!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var sharedSet = shared.ToHashSet(StringComparer.Ordinal);
        foreach (var (id, discordUserId) in typed)
        {
            if (!sharedSet.Contains(discordUserId))
                counted[id] = discordUserId;
        }

        return counted;
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
