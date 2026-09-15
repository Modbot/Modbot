using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Commands;

/// <summary>
/// The bot's slash commands: what they are called, what they take, and which Modbot permission
/// each needs.
/// </summary>
/// <remarks>
/// Every command but <see cref="Link"/> needs a Modbot account linked to the caller's Discord user
/// id. The permission on top of that is the same one the equivalent web page asks for, so the bot
/// never shows anybody more than the web app would. <see cref="Link"/> is for every member: it
/// answers with the link page's address and nothing else (Discord account linking design §2).
/// </remarks>
public static class DiscordCommands
{
    public const string Lookup = "lookup";
    public const string Recent = "recent";
    public const string Modbot = "modbot";
    public const string Link = "link";

    public const string LookupUserOption = "user";
    public const string RecentCountOption = "count";

    public const int RecentDefault = 10;
    public const int RecentMax = 25;

    public static IReadOnlyList<DiscordCommandDefinition> All { get; } =
    [
        new(
            Lookup,
            "Look up a VRChat user in Modbot's records",
            [new DiscordCommandOption(LookupUserOption, "VRChat user id or display name", DiscordOptionKind.Text, Required: true)]),
        new(
            Recent,
            "The latest moderation events in the group",
            [new DiscordCommandOption(RecentCountOption, $"How many, 1 to {RecentMax} (default {RecentDefault})", DiscordOptionKind.WholeNumber, Required: false, Min: 1, Max: RecentMax)]),
        new(
            Modbot,
            "Whether the bot is working, and where the Modbot web app is",
            []),
        new(
            Link,
            "Link your VRChat account",
            []),
    ];

    /// <summary>Commands any member may run, with no Modbot account.</summary>
    public static bool IsForEveryone(string command) => command == Link;

    /// <summary>
    /// The permission a command needs beyond a linked account. <see cref="ModbotPermissions.None"/>
    /// means linked is enough; null means the command is not one of ours.
    /// </summary>
    public static ModbotPermissions? Requires(string command) => command switch
    {
        Lookup => ModbotPermissions.ViewProfile,
        Recent => ModbotPermissions.ViewAuditLog,
        Modbot => ModbotPermissions.None,
        _ => null,
    };

    /// <summary>The permission's label as the web app's role editor shows it.</summary>
    public static string Label(ModbotPermissions permission) => permission switch
    {
        ModbotPermissions.ViewProfile => "See profiles",
        ModbotPermissions.ViewAuditLog => "See the audit log",
        _ => permission.ToString(),
    };

    /// <summary>Same rule as the web app: Administrator may do everything (spec 7.3).</summary>
    public static bool Allows(ModbotPermissions held, ModbotPermissions required)
        => held.HasFlag(ModbotPermissions.Administrator) || (held & required) == required;
}
