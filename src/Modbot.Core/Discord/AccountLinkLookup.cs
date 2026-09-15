using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Discord;

/// <summary>
/// Reading <c>discord_account_link</c>: which VRChat account a Discord account is linked to, and the
/// other way round (Discord account linking design §13).
/// </summary>
/// <remarks>
/// <para>
/// The link table is the one place that connects a Discord user id to a VRChat user id for a member.
/// Anything that wants to treat the two as one person -- a filter that names a person, a profile
/// that shows both sides -- reads it through here rather than writing its own query, so "active"
/// means the same thing everywhere: <see cref="DiscordAccountLink.UnlinkedAt"/> is null.
/// </para>
/// <para>
/// <strong>No link is the normal answer.</strong> Most people never link. A null here means two
/// separate people with separate histories, never "missing data".
/// </para>
/// </remarks>
public static class AccountLinkLookup
{
    /// <summary>Every link that has not been ended.</summary>
    public static IQueryable<DiscordAccountLink> ActiveAccountLinks(this ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.DiscordAccountLinks.AsNoTracking().Where(l => l.UnlinkedAt == null);
    }

    /// <summary>The VRChat user id this Discord account is linked to, or null.</summary>
    public static Task<string?> LinkedVRChatUserIdAsync(this ModbotContext db, string discordUserId, CancellationToken ct = default)
        => db.ActiveAccountLinks()
            .Where(l => l.DiscordUserId == discordUserId)
            .Select(l => l.VRChatUserId)
            .FirstOrDefaultAsync(ct)!;

    /// <summary>The Discord user id this VRChat account is linked to, or null.</summary>
    public static Task<string?> LinkedDiscordUserIdAsync(this ModbotContext db, string vrchatUserId, CancellationToken ct = default)
        => db.ActiveAccountLinks()
            .Where(l => l.VRChatUserId == vrchatUserId)
            .Select(l => l.DiscordUserId)
            .FirstOrDefaultAsync(ct)!;
}
