using Modbot.Core.Data.Entities;

namespace Modbot.Core.Discord;

/// <summary>
/// The moderation actions themselves: bans, unbans, kicks, warns, rejected join requests and
/// role changes.
/// </summary>
/// <remarks>
/// <para>
/// This was the closed list the single moderation log channel could carry. Channels are now
/// chosen per route from <see cref="DiscordEventTypes"/>, and this list is left doing two smaller
/// jobs: it is the moderation history <c>/lookup</c> and <c>/recent</c> show, and it is what the
/// migration gave the route it made from an existing log channel whose choice was never changed.
/// </para>
/// </remarks>
public static class ModerationLogEvents
{
    /// <summary>The moderation actions, in the order the old settings page listed them.</summary>
    public static IReadOnlyList<string> Allowed { get; } =
    [
        FactType.MemberBanned,
        FactType.MemberUnbanned,
        FactType.MemberKicked,
        FactType.GroupInstanceKick,
        FactType.GroupInstanceWarn,
        FactType.JoinRequestRejected,
        FactType.JoinRequestBlocked,
        FactType.RoleGranted,
        FactType.RoleRevoked,
    ];
}
