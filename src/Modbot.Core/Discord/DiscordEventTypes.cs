using Modbot.Core.Data.Entities;

namespace Modbot.Core.Discord;

/// <summary>One heading in the event picker and the types under it, in display order.</summary>
public sealed record DiscordEventGroup(string Name, IReadOnlyList<string> Types);

/// <summary>
/// Which fact types a Discord channel may be sent, and the groups the settings page shows them in
/// (Discord event routes design §4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every type in <see cref="FactType"/> except a short list</strong>, so a type added
/// later is offered without anybody remembering to add it here. Its group comes from the start
/// of its name.
/// </para>
/// <para>
/// <strong>The never-sent list is by name prefix</strong> and is checked again when a post goes
/// out, not only when a route is saved. A channel is a place many people can usually read, and the
/// fact log also holds sign-ins -- a failed one carries the caller's address -- reset links and
/// contact changes. The accounts design says a reset link never goes anywhere but to the person it
/// is for, and this is what keeps that true whatever a route row says.
/// </para>
/// <para>
/// In Core so the settings API can check what it saves without referencing the Discord library.
/// </para>
/// </remarks>
public static class DiscordEventTypes
{
    public const string Moderation = "Moderation";
    public const string Members = "Members";
    public const string Instances = "Instances";
    public const string Group = "Group";
    public const string Discord = "Discord";
    public const string AccessAndSettings = "Access and settings";
    public const string Other = "Other";

    private static readonly string[] GroupOrder =
        [Moderation, Members, Instances, Group, Discord, AccessAndSettings, Other];

    /// <summary>
    /// Types, or starts of type names followed by a dot or a dash, that are never sent. Plumbing is
    /// here as well as secrets: <see cref="FactType.DiscordLogPosted"/> would post about posting.
    /// </summary>
    private static readonly string[] NeverSent =
    [
        "modbot.user.login",
        "modbot.user.password",
        "modbot.user.sign-out-everywhere",
        "modbot.user.contact",
        FactType.DiscordLogPosted,
        FactType.MigrationApplied,
        FactType.PartitionCreated,
        FactType.RetentionPruned,
    ];

    /// <summary>The moderation actions themselves, which lead the picker.</summary>
    private static readonly HashSet<string> ModerationActions = new(StringComparer.Ordinal)
    {
        FactType.MemberBanned,
        FactType.MemberUnbanned,
        FactType.MemberKicked,
        FactType.GroupInstanceKick,
        FactType.GroupInstanceWarn,
        FactType.JoinRequestRejected,
        FactType.JoinRequestBlocked,
    };

    /// <summary>Every type a route may name, in <see cref="FactType"/>'s declaration order.</summary>
    public static IReadOnlyList<string> Sendable { get; } = FactType.All.Where(CanSend).ToArray();

    /// <summary>The picker's groups, each in declaration order, empty groups left out.</summary>
    public static IReadOnlyList<DiscordEventGroup> Groups { get; } = GroupOrder
        .Select(name => new DiscordEventGroup(name, Sendable.Where(t => GroupOf(t) == name).ToArray()))
        .Where(g => g.Types.Count > 0)
        .ToArray();

    /// <summary>Whether a fact of this type may go to Discord at all.</summary>
    public static bool CanSend(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;

        foreach (var blocked in NeverSent)
        {
            if (type.Length == blocked.Length && string.Equals(type, blocked, StringComparison.Ordinal))
                return false;

            if (type.Length > blocked.Length
                && type.StartsWith(blocked, StringComparison.Ordinal)
                && type[blocked.Length] is '.' or '-')
                return false;
        }

        return true;
    }

    /// <summary>The heading a type is listed under. First rule that fits, as design §4 lists them.</summary>
    public static string GroupOf(string type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (ModerationActions.Contains(type)
            || Starts(type, "modbot.report", "modbot.evidence", "modbot.review", "modbot.user-profile", "modbot.ai-moderation"))
            return Moderation;

        if (Starts(type,
                "vrchat.group.member", "vrchat.group.role", "vrchat.group.request", "vrchat.group.invite",
                "vrchat.group.members", "vrchat.group.bans", "vrchat.user"))
            return Members;

        if (Starts(type, "vrchat.group.instance", "vrchat.instance", "vrchat.avatar"))
            return Instances;

        if (Starts(type, "vrchat.group"))
            return Group;

        if (Starts(type, "discord"))
            return Discord;

        if (Starts(type, "modbot.user", "modbot.role", "modbot.apikey", "modbot.settings", "modbot.ban-reasons"))
            return AccessAndSettings;

        return Other;
    }

    /// <summary>
    /// The types to keep from a request: sendable ones only, each once, in <see cref="Sendable"/>'s
    /// order so a saved route reads back the same whatever order the boxes were ticked in.
    /// </summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string>? types)
    {
        if (types is null)
            return [];

        var chosen = types
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToHashSet(StringComparer.Ordinal);

        return Sendable.Where(chosen.Contains).ToArray();
    }

    /// <summary>Whether the type is one of the prefixes, or starts with one followed by a dot.</summary>
    private static bool Starts(string type, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (string.Equals(type, prefix, StringComparison.Ordinal))
                return true;

            if (type.Length > prefix.Length
                && type.StartsWith(prefix, StringComparison.Ordinal)
                && type[prefix.Length] == '.')
                return true;
        }

        return false;
    }
}
