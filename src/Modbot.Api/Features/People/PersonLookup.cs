using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Api.Features.People;

/// <summary>Which account a caller arrived by. Exactly one is set.</summary>
public readonly record struct PersonAsk(string? VRChatUserId, string? DiscordUserId, Guid? AccountId);

/// <summary>
/// Which parts of a person one caller may be told about.
/// </summary>
/// <remarks>
/// Each answers the same permission the screen that already shows that part answers, so tying a
/// person's accounts together hands out nothing new — it only saves opening three screens.
/// </remarks>
/// <param name="DiscordSide">Which Discord account belongs to a VRChat account, and the other way round.</param>
/// <param name="Account">Which person holds which Modbot account.</param>
/// <param name="VRChatName">The VRChat display name Modbot has stored.</param>
/// <param name="DiscordName">The name the Discord server shows.</param>
public readonly record struct PersonSight(bool DiscordSide, bool Account, bool VRChatName, bool DiscordName)
{
    public static PersonSight Of(ModbotPermissions held) => new(
        // The link between the two accounts is what GET /api/discord-links answers.
        ModbotAuth.Allows(held, ModbotPermissions.ViewProfile),
        // The operational log's modbot.user.vrchat.link facts and the users page both say this.
        ModbotAuth.Allows(held, ModbotPermissions.ViewOperationalLog)
            || ModbotAuth.Allows(held, ModbotPermissions.ManageUsers),
        ModbotAuth.Allows(held, ModbotPermissions.ViewProfile),
        ModbotAuth.Allows(held, ModbotPermissions.ViewMembers));
}

/// <summary>
/// Ties a person's VRChat account, Discord account and Modbot account together from whichever one
/// a link named (one view per person design §3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only what is written down.</strong> Two things tie accounts together and nothing else
/// does: the proved link in <c>discord_account_link</c>, and the VRChat id and Discord id recorded
/// on a <c>modbot_user</c> row. Display names are never compared, and an id's shape is never read
/// (foundation §3.1.1). An account that ties to nothing resolves to itself, and the view opens on
/// that alone.
/// </para>
/// <para>
/// <strong>A side is revealed only where the caller could already read it</strong> — see
/// <see cref="PersonSight"/>. The account a link named is always answered, because the caller had
/// its id in the address already.
/// </para>
/// </remarks>
public sealed class PersonLookup(ModbotContext db)
{
    public async Task<PersonView> ResolveAsync(PersonAsk ask, PersonSight sight, CancellationToken ct)
    {
        if (ask.AccountId is { } accountId)
            return await FromAccountAsync(accountId, sight, ct);

        if (!string.IsNullOrWhiteSpace(ask.DiscordUserId))
            return await FromDiscordAsync(ask.DiscordUserId, sight, ct);

        if (!string.IsNullOrWhiteSpace(ask.VRChatUserId))
            return await FromVRChatAsync(ask.VRChatUserId, sight, ct);

        return new PersonView(null, null, null, sight.Account);
    }

    private async Task<PersonView> FromVRChatAsync(string vrchatUserId, PersonSight sight, CancellationToken ct)
    {
        var account = sight.Account ? await AccountByVRChatAsync(vrchatUserId, ct) : null;

        // The proved link first, the id typed onto a Modbot account second. They are different
        // claims, and the stronger one wins where both exist.
        string? discordId = null;
        var discordFoundBy = FoundBy.Link;

        if (sight.DiscordSide)
            discordId = await db.LinkedDiscordUserIdAsync(vrchatUserId, ct);

        if (discordId is null && account?.DiscordUserId is { Length: > 0 } typed)
        {
            discordId = typed;
            discordFoundBy = FoundBy.Account;
        }

        return new PersonView(
            new PersonSide(vrchatUserId, await VRChatNameAsync(vrchatUserId, sight, ct), FoundBy.Asked),
            discordId is null
                ? null
                : new PersonSide(discordId, await DiscordNameAsync(discordId, sight, ct), discordFoundBy),
            account is null ? null : View(account, FoundBy.Account),
            sight.Account);
    }

    private async Task<PersonView> FromDiscordAsync(string discordUserId, PersonSight sight, CancellationToken ct)
    {
        var account = sight.Account ? await AccountByDiscordAsync(discordUserId, ct) : null;

        string? vrchatId = null;
        var vrchatFoundBy = FoundBy.Link;

        if (sight.DiscordSide)
            vrchatId = await db.LinkedVRChatUserIdAsync(discordUserId, ct);

        if (vrchatId is null && account?.VRChatUserId is { Length: > 0 } onAccount)
        {
            vrchatId = onAccount;
            vrchatFoundBy = FoundBy.Account;
        }

        // A Discord account nobody typed onto a Modbot account can still reach one through the
        // VRChat account it proved a link with.
        if (account is null && sight.Account && vrchatId is not null)
            account = await AccountByVRChatAsync(vrchatId, ct);

        return new PersonView(
            vrchatId is null
                ? null
                : new PersonSide(vrchatId, await VRChatNameAsync(vrchatId, sight, ct), vrchatFoundBy),
            new PersonSide(discordUserId, await DiscordNameAsync(discordUserId, sight, ct), FoundBy.Asked),
            account is null ? null : View(account, FoundBy.Account),
            sight.Account);
    }

    /// <summary>
    /// A Modbot account, and the person behind it.
    /// </summary>
    /// <remarks>
    /// Nothing at all is answered without the permission to know who holds an account: resolving
    /// an account id to a VRChat id for a caller who may not be told the two are the same person
    /// would leak the mapping backwards, which is the one thing this must not do.
    /// </remarks>
    private async Task<PersonView> FromAccountAsync(Guid accountId, PersonSight sight, CancellationToken ct)
    {
        if (!sight.Account)
            return new PersonView(null, null, null, false);

        var account = await AccountsWithRoles().FirstOrDefaultAsync(u => u.Id == accountId, ct);

        if (account is null)
            return new PersonView(null, null, null, true);

        var vrchatId = account.VRChatUserId is { Length: > 0 } id ? id : null;

        var discordId = account.DiscordUserId is { Length: > 0 } typed ? typed : null;
        var discordFoundBy = FoundBy.Account;

        if (discordId is null && sight.DiscordSide && vrchatId is not null)
        {
            discordId = await db.LinkedDiscordUserIdAsync(vrchatId, ct);
            discordFoundBy = FoundBy.Link;
        }

        return new PersonView(
            vrchatId is null
                ? null
                : new PersonSide(vrchatId, await VRChatNameAsync(vrchatId, sight, ct), FoundBy.Account),
            discordId is null
                ? null
                : new PersonSide(discordId, await DiscordNameAsync(discordId, sight, ct), discordFoundBy),
            View(account, FoundBy.Asked),
            true);
    }

    private IQueryable<ModbotUser> AccountsWithRoles()
        => db.Users.AsNoTracking().Include(u => u.Roles).ThenInclude(r => r.Role);

    private Task<ModbotUser?> AccountByVRChatAsync(string vrchatUserId, CancellationToken ct)
        => AccountsWithRoles().FirstOrDefaultAsync(u => u.VRChatUserId == vrchatUserId, ct);

    private Task<ModbotUser?> AccountByDiscordAsync(string discordUserId, CancellationToken ct)
        => AccountsWithRoles().FirstOrDefaultAsync(u => u.DiscordUserId == discordUserId, ct);

    private static PersonAccount View(ModbotUser account, string foundBy)
        => new(
            account.Id,
            account.Username,
            foundBy,
            account.Roles.Select(r => r.Role.Name).Order(StringComparer.Ordinal).ToList(),
            account.IsDisabled,
            account.CreatedAt,
            account.LastLoginAt);

    /// <summary>The VRChat display name as stored now, or null when only the id has ever been seen.</summary>
    private async Task<string?> VRChatNameAsync(string vrchatUserId, PersonSight sight, CancellationToken ct)
        => !sight.VRChatName
            ? null
            : await db.VRChatUsers.AsNoTracking()
                .Where(u => u.UserId == vrchatUserId)
                .Select(u => u.DisplayName)
                .FirstOrDefaultAsync(ct);

    /// <summary>The name the Discord server shows, from the stored member list. The newest row wins.</summary>
    private async Task<string?> DiscordNameAsync(string discordUserId, PersonSight sight, CancellationToken ct)
        => !sight.DiscordName
            ? null
            : await db.DiscordMembers.AsNoTracking()
                .Where(m => m.UserId == discordUserId)
                .OrderByDescending(m => m.UpdatedAt)
                .Select(m => (string?)m.DisplayName)
                .FirstOrDefaultAsync(ct);
}
