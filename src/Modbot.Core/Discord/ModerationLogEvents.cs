using Modbot.Core.Data.Entities;

namespace Modbot.Core.Discord;

/// <summary>
/// Which fact types the Discord bot may post to the moderation log channel, and how the chosen
/// set is stored on the settings row.
/// </summary>
/// <remarks>
/// <para>
/// A closed list, on purpose. The channel is a place the whole Discord server can usually read,
/// and the fact log also holds sign-ins, reset links and account changes. The operator picks
/// from this list; anything else written into the column -- by hand, by a bug, by an old build --
/// is dropped when it is read back. That is the guarantee the accounts design leans on when it
/// says a reset link never goes anywhere but to the person it is for.
/// </para>
/// <para>
/// Lives in Core rather than in the bot so the settings API can validate what it saves without
/// referencing the Discord library.
/// </para>
/// </remarks>
public static class ModerationLogEvents
{
    /// <summary>Everything the channel can carry. Order is the order the settings page shows.</summary>
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

    /// <summary>
    /// What a freshly configured channel gets: all of it. Foundation §9 lists bans, unbans,
    /// kicks, warns, join-request rejections and role changes, which is the whole allowed list.
    /// </summary>
    public static IReadOnlyList<string> Defaults => Allowed;

    /// <summary>
    /// The set to post, from the stored column. Null means the defaults; anything else is read as
    /// a comma-separated list and cut down to <see cref="Allowed"/>, so an empty or junk column
    /// posts nothing rather than everything.
    /// </summary>
    public static IReadOnlySet<string> Parse(string? stored)
    {
        if (stored is null)
            return Defaults.ToHashSet(StringComparer.Ordinal);

        var chosen = stored
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        return Allowed.Where(chosen.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The column value for a chosen set. Unknown types are dropped; the result keeps the order
    /// of <see cref="Allowed"/>. An empty choice becomes an empty string, which
    /// <see cref="Parse"/> reads as "post nothing" -- distinct from null, which means the defaults.
    /// </summary>
    public static string Serialize(IEnumerable<string> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        var chosen = types.ToHashSet(StringComparer.Ordinal);
        return string.Join(',', Allowed.Where(chosen.Contains));
    }
}
