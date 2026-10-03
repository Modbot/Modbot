using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Commands;

/// <summary>
/// The bot's slash commands: what they are called, what they take, and which Modbot permission
/// each needs.
/// </summary>
/// <remarks>
/// <para>
/// Every command but <see cref="Link"/>, <see cref="Me"/> and <see cref="Help"/> needs a Modbot
/// account linked to the caller's Discord user id. The permission on top of that is the same one the
/// equivalent web page asks for, so the bot never shows anybody more than the web app would.
/// <see cref="Link"/> is for every member: it answers with the link page's address and nothing else
/// (Discord account linking design §2). <see cref="Me"/> is for every member too, and only ever about
/// the member who ran it; it is registered only while the operator has switched it on (Discord /me
/// design). <see cref="Help"/> lists the commands the caller can use, and nothing about anybody.
/// </para>
/// <para>
/// <strong>The staff commands are hidden from members.</strong> Before this, every member saw
/// <c>/lookup</c>, <c>/recent</c> and <c>/modbot</c> in the command list and was refused when they
/// tried one. They are now registered as <see cref="DiscordCommandDefinition.StaffOnly"/>, which
/// Discord shows only to members who may time others out. That only hides them: the Modbot
/// account and permission are checked on every run as before, so an owner who shows them to
/// everybody under Server Settings → Integrations has given nobody anything.
/// </para>
/// </remarks>
public static class DiscordCommands
{
    public const string Lookup = "lookup";
    public const string Recent = "recent";
    public const string Modbot = "modbot";
    public const string Link = "link";
    public const string Me = "me";
    public const string Help = "help";

    /// <summary>The VRChat side of <c>/lookup</c>: a name or an id, typed, with suggestions.</summary>
    public const string LookupUserOption = "user";

    /// <summary>The Discord side of <c>/lookup</c>: a member, picked from Discord's own list.</summary>
    public const string LookupDiscordOption = "discord";
    public const string RecentCountOption = "count";

    public const int RecentDefault = 10;
    public const int RecentMax = 25;

    public static IReadOnlyList<DiscordCommandDefinition> All { get; } =
    [
        new(
            Lookup,
            "Look up a person in Modbot's records",
            [
                new DiscordCommandOption(LookupUserOption, "VRChat user id or display name", DiscordOptionKind.Text, Required: false, Suggests: true),
                new DiscordCommandOption(LookupDiscordOption, "Discord member", DiscordOptionKind.Member, Required: false),
            ],
            StaffOnly: true),
        new(
            Recent,
            "The latest moderation events",
            [new DiscordCommandOption(RecentCountOption, $"How many, 1 to {RecentMax} (default {RecentDefault})", DiscordOptionKind.WholeNumber, Required: false, Min: 1, Max: RecentMax)],
            StaffOnly: true),
        new(
            Modbot,
            "Whether the bot is working, and where the Modbot web app is",
            [],
            StaffOnly: true),
        new(
            Link,
            "Link your VRChat account",
            []),
        new(
            Me,
            "What Modbot holds about you",
            []),
        new(
            Help,
            "What the bot's commands do",
            []),

        // The right-click menus staff act from (acting from Discord design §2). Registered beside
        // the slash commands because Discord replaces the whole set in one call.
        .. Interactions.StaffMenus.All,
    ];

    /// <summary>
    /// The commands to register on the server: all of them, less <see cref="Me"/> while the
    /// operator has it switched off. Not registered is cleaner than registered and refusing: a
    /// member never sees a command that would only tell them it is off.
    /// </summary>
    public static IReadOnlyList<DiscordCommandDefinition> For(bool meCommand)
        => meCommand ? All : [.. All.Where(c => c.Name != Me)];

    /// <summary>Commands any member may run, with no Modbot account.</summary>
    public static bool IsForEveryone(string command) => command is Link or Me or Help;

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
        ModbotPermissions.Kick => "Kick",
        ModbotPermissions.Ban => "Ban",
        ModbotPermissions.WriteNotes => "Write notes",
        ModbotPermissions.AnswerJoinRequests => "Answer join requests",
        _ => permission.ToString(),
    };

    /// <summary>Same rule as the web app: Administrator may do everything (spec 7.3).</summary>
    public static bool Allows(ModbotPermissions held, ModbotPermissions required)
        => held.HasFlag(ModbotPermissions.Administrator) || (held & required) == required;
}
