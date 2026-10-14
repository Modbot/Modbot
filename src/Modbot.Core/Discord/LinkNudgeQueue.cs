using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Discord;

/// <summary>
/// Who the one-time link reminder is still to be sent to, and how fast it goes (account links). The
/// bot sends it; the Account links tab counts it. Both read the queue here, so the number staff see is
/// the number the bot works through.
/// </summary>
public static class LinkNudgeQueue
{
    /// <summary>The wait between two messages. Slow on purpose: a burst of direct messages from a bot is what Discord punishes.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The members still to be reminded: in the server since before <paramref name="startedAt"/>, no
    /// bot, no active link, and never reminded.
    /// </summary>
    public static IQueryable<DiscordMember> Waiting(ModbotContext db, string guildId, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(db);

        var links = db.ActiveAccountLinks();

        return db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId
                        && !m.IsBot
                        && m.LeftAt == null
                        && (m.JoinedAt ?? m.FirstSeenAt) <= startedAt
                        && !links.Any(l => l.DiscordUserId == m.UserId)
                        && !db.DiscordLinkNudges.Any(n => n.DiscordUserId == m.UserId));
    }
}
