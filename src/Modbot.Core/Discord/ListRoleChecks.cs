using Modbot.Core.Data.Entities;

namespace Modbot.Core.Discord;

/// <summary>
/// Which Discord roles a saved list may give, and when a pass stops and waits (roles from lists
/// design §4, §5).
/// </summary>
/// <remarks>
/// Asked when a pairing is saved and again on every pass, by the same code, so a role that gains a
/// staff permission after it was paired stops being given the next minute.
/// </remarks>
public static class ListRoleChecks
{
    /// <summary>The brake: a pass taking the role from more than this many people at once stops.</summary>
    public const int BrakeTaking = 25;

    /// <summary>The brake never stops a pass taking the role from fewer than this many people.</summary>
    public const int BrakeFloor = 3;

    /// <summary>
    /// The server-wide permissions that make a role a staff role, with the name Discord's settings
    /// give each. Any one of them refuses the role (design §4).
    /// </summary>
    public static IReadOnlyList<(long Bit, string Name)> StaffPowers { get; } =
    [
        (1L << 3, "Administrator"),
        (1L << 5, "Manage Server"),
        (1L << 28, "Manage Roles"),
        (1L << 4, "Manage Channels"),
        (1L << 13, "Manage Messages"),
        (1L << 2, "Ban Members"),
        (1L << 1, "Kick Members"),
        (1L << 40, "Timeout Members"),
        (1L << 17, "Mention @everyone, @here and All Roles"),
        (1L << 29, "Manage Webhooks"),
        (1L << 27, "Manage Nicknames"),
        (1L << 34, "Manage Threads"),
        (1L << 33, "Manage Events"),
        (1L << 30, "Manage Expressions"),
        (1L << 7, "View Audit Log"),
        (1L << 22, "Mute Members"),
        (1L << 23, "Deafen Members"),
        (1L << 24, "Move Members"),
    ];

    /// <summary>The staff powers a role's permissions carry, by name. Empty for a community role.</summary>
    public static IReadOnlyList<string> StaffPowersIn(long permissions)
        => [.. StaffPowers.Where(p => (permissions & p.Bit) != 0).Select(p => p.Name)];

    /// <summary>
    /// Whether a pass that would take the role from <paramref name="taking"/> people, of the
    /// <paramref name="holders"/> who hold it now, stops and waits for somebody to press Apply.
    /// </summary>
    public static bool Brakes(int taking, int holders)
        => taking >= BrakeFloor && (taking > BrakeTaking || taking * 2 > holders);

    /// <summary>
    /// Why this role cannot be given by a list, as a sentence, or null when it can.
    /// </summary>
    /// <param name="role">The role as the server index has it; null when Modbot does not know it.</param>
    /// <param name="decidedElsewhere">
    /// Why something else already decides who holds the role (role sync, account linking, another
    /// list), or null when nothing does.
    /// </param>
    public static string? WhyNot(DiscordRole? role, string? decidedElsewhere)
    {
        if (role is null || role.RemovedAt is not null)
            return "That role is not in the Discord server.";

        if (role.Everyone || role.RoleId == role.GuildId)
            return "A list cannot give @everyone.";

        if (role.Managed)
            return $"{role.Name} belongs to a bot or an integration, so nobody can give it.";

        if (decidedElsewhere is not null)
            return decidedElsewhere;

        if (role.Permissions is not { } permissions)
            return $"Modbot has not read what {role.Name} can do yet. Try again in a minute.";

        var powers = StaffPowersIn(permissions);
        if (powers.Count > 0)
            return $"{role.Name} has staff permissions: {Joined(powers)}. A list only gives roles without them.";

        if (!role.BotCanAssign)
            return $"The bot cannot give {role.Name}. Give it Manage Roles and keep its own role above that one.";

        return null;
    }

    private static string Joined(IReadOnlyList<string> powers)
    {
        var named = powers.Take(3).ToList();
        var more = powers.Count - named.Count;

        var text = named.Count == 1
            ? named[0]
            : string.Join(", ", named.Take(named.Count - 1)) + " and " + named[^1];

        return more > 0 ? $"{string.Join(", ", named)} and {more} more" : text;
    }
}
