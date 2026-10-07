using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Users;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Commands;

/// <summary>
/// The bot's slash commands: what they are called, what they take, and which Modbot permission
/// each needs.
/// </summary>
/// <remarks>
/// <para>
/// Every command but <see cref="Link"/>, <see cref="Me"/>, <see cref="Verify"/> and
/// <see cref="Help"/> needs a Modbot account linked to the caller's Discord user id. The permission on top of that is the same one the
/// equivalent web page asks for, so the bot never shows anybody more than the web app would.
/// <see cref="Link"/> is for every member: it answers with the link page's address and nothing else
/// (Discord account linking design §2). <see cref="Me"/> is for every member too, and only ever about
/// the member who ran it; it is registered only while the operator has switched it on (Discord /me
/// design). <see cref="Help"/> lists the commands the caller can use, and nothing about anybody.
/// <see cref="Verify"/> is how a staff member proves their Discord account with a code from their
/// account page (Discord account linking design §14); it is for everybody because staff often do
/// not hold Timeout Members, and it tells nobody anything without a right code.
/// </para>
/// <para>
/// <strong>The staff commands are hidden from members.</strong> Before this, every member saw
/// <c>/lookup</c>, <c>/recent</c> and <c>/modbot</c> in the command list and was refused when they
/// tried one. They are now registered as <see cref="DiscordCommandDefinition.StaffOnly"/>, which
/// Discord shows only to members who may time others out. That only hides them: the Modbot
/// account and permission are checked on every run as before, so an owner who shows them to
/// everybody under Server Settings → Integrations has given nobody anything.
/// </para>
/// <para>
/// <strong>Every command has a switch</strong> (Discord commands design §3.8): the operator's
/// Commands card on Settings → Discord writes the <c>discord_commands</c> setting, and a command
/// that is off is not registered. The names and defaults are in
/// <see cref="DiscordCommandSwitches"/>, which the web app reads too; a test holds that list to
/// <see cref="All"/>, so a command added here without a switch fails the build's tests.
/// </para>
/// <para>
/// A command that replies in public, takes subcommands, or takes a channel, a choice or a yes/no is
/// described with the same <see cref="DiscordCommandDefinition"/>: see its <c>Reply</c>,
/// <c>Subcommands</c> and the <see cref="DiscordOptionKind"/>s.
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
    public const string Verify = StaffDiscordCodes.Command;
    public const string Note = "note";
    public const string Watch = "watch";
    public const string Live = "live";

    /// <summary>The code from the account page that <c>/verify</c> takes.</summary>
    public const string VerifyCodeOption = "code";

    /// <summary>The VRChat side of <c>/lookup</c>: a name or an id, typed, with suggestions.</summary>
    public const string LookupUserOption = "user";

    /// <summary>The Discord side of <c>/lookup</c>: a member, picked from Discord's own list.</summary>
    public const string LookupDiscordOption = "discord";
    public const string RecentCountOption = "count";

    public const int RecentDefault = 10;
    public const int RecentMax = 25;

    /// <summary>
    /// The Discord side of <c>/note</c> and <c>/watch</c>: a member, picked from Discord's own list.
    /// Exactly one of this and <see cref="VRChatOption"/> is filled in; Discord cannot require "one
    /// of", so the handler checks.
    /// </summary>
    public const string MemberOption = "member";

    /// <summary>The VRChat side of <c>/note</c> and <c>/watch</c>: a name or an id, typed, with suggestions.</summary>
    public const string VRChatOption = "vrchat";

    public const string NoteTextOption = "text";
    public const int NoteTextMax = 2000;

    public const string WatchReasonOption = "reason";
    public const int WatchReasonMax = 200;

    /// <summary>How long a watch lasts: one of the <c>ForOneDay</c>.. values below.</summary>
    public const string WatchForOption = "for";
    public const string ForOneDay = "1d";
    public const string ForOneWeek = "7d";
    public const string ForThirtyDays = "30d";
    public const string ForUntilStopped = "until-stopped";

    /// <summary>When somebody should check on a watched person again.</summary>
    public const string WatchFollowUpOption = "follow-up";
    public const string FollowUpNone = "none";
    public const string FollowUpTomorrow = "tomorrow";
    public const string FollowUpInAWeek = "week";

    public static IReadOnlyList<DiscordCommandDefinition> All { get; } =
    [
        new(
            Lookup,
            "Look up a person in Modbot's records",
            [
                new DiscordCommandOption(LookupUserOption, "VRChat user id or display name", DiscordOptionKind.Text, Required: false, Suggests: true),
                new DiscordCommandOption(LookupDiscordOption, "Discord member", DiscordOptionKind.Member, Required: false),
            ],
            ShownTo: DiscordShownTo.Moderators),
        new(
            Recent,
            "The latest moderation events",
            [new DiscordCommandOption(RecentCountOption, $"How many, 1 to {RecentMax} (default {RecentDefault})", DiscordOptionKind.WholeNumber, Required: false, Min: 1, Max: RecentMax)],
            ShownTo: DiscordShownTo.Moderators),
        new(
            Modbot,
            "Whether the bot is working, and where the Modbot web app is",
            [],
            ShownTo: DiscordShownTo.Moderators),
        new(
            Note,
            "Write a note about someone",
            [
                // Discord wants the required option first, so the note comes before the person.
                new DiscordCommandOption(NoteTextOption, "The note", DiscordOptionKind.Text, Required: true, Min: 1, Max: NoteTextMax),
                new DiscordCommandOption(MemberOption, "Discord member", DiscordOptionKind.Member, Required: false),
                new DiscordCommandOption(VRChatOption, "VRChat name or id", DiscordOptionKind.Text, Required: false, Suggests: true),
            ],
            ShownTo: DiscordShownTo.Moderators),
        new(
            Watch,
            "Start watching someone",
            [
                new DiscordCommandOption(WatchReasonOption, "Why", DiscordOptionKind.Text, Required: true, Min: 1, Max: WatchReasonMax),
                new DiscordCommandOption(
                    WatchForOption,
                    "How long",
                    DiscordOptionKind.Choice,
                    Required: true,
                    Choices:
                    [
                        new("1 day", ForOneDay),
                        new("1 week", ForOneWeek),
                        new("30 days", ForThirtyDays),
                        new("Until stopped", ForUntilStopped),
                    ]),
                new DiscordCommandOption(MemberOption, "Discord member", DiscordOptionKind.Member, Required: false),
                new DiscordCommandOption(VRChatOption, "VRChat name or id", DiscordOptionKind.Text, Required: false, Suggests: true),
                new DiscordCommandOption(
                    WatchFollowUpOption,
                    "Check again",
                    DiscordOptionKind.Choice,
                    Required: false,
                    Choices:
                    [
                        new("None", FollowUpNone),
                        new("Tomorrow", FollowUpTomorrow),
                        new("In a week", FollowUpInAWeek),
                    ]),
            ],
            ShownTo: DiscordShownTo.Moderators),
        new(
            Live,
            "Who is in the group's instances now",
            [],
            ShownTo: DiscordShownTo.Moderators),
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
        new(
            Verify,
            "Connect your Discord account to your Modbot account",
            [new DiscordCommandOption(VerifyCodeOption, "The code from your account page in Modbot", DiscordOptionKind.Text, Required: true)]),

        // The right-click menus staff act from (acting from Discord design §2). Registered beside
        // the slash commands because Discord replaces the whole set in one call.
        .. Interactions.StaffMenus.All,
    ];

    /// <summary>
    /// The commands to register on the server: the ones switched on in the <c>discord_commands</c>
    /// setting (<see cref="DiscordCommandSwitches"/>), a missing name taking its default. Not
    /// registered is cleaner than registered and refusing: a member never sees a command that would
    /// only tell them it is off.
    /// </summary>
    /// <param name="switches">The setting as stored. Null or empty means every default.</param>
    public static IReadOnlyList<DiscordCommandDefinition> For(string? switches)
        => [.. All.Where(c => DiscordCommandSwitches.IsOn(switches, c.Name))];

    /// <summary>
    /// The names registered for these switches. Two lists with the same names are the same
    /// registration: the bot registers again only when this changes (Discord commands design §3.8).
    /// </summary>
    public static IReadOnlySet<string> NamesFor(string? switches)
        => For(switches).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Commands any member may run, with no Modbot account.</summary>
    public static bool IsForEveryone(string command) => command is Link or Me or Help or Verify;

    /// <summary>
    /// The permission a command needs beyond a linked account. <see cref="ModbotPermissions.None"/>
    /// means linked is enough; null means the command is not one of ours.
    /// </summary>
    public static ModbotPermissions? Requires(string command) => command switch
    {
        Lookup => ModbotPermissions.ViewProfile,
        Recent => ModbotPermissions.ViewAuditLog,
        Modbot => ModbotPermissions.None,
        Note or Watch => ModbotPermissions.WriteNotes,
        Live => ModbotPermissions.ViewLiveInstances,
        _ => null,
    };

    /// <summary>
    /// Commands that write something. The web app refuses every request from an account with no
    /// VRChat link, so these do too (acting from Discord design §3).
    /// </summary>
    public static bool Writes(string command) => command is Note or Watch;

    /// <summary>The permission's label as the web app's role editor shows it.</summary>
    public static string Label(ModbotPermissions permission) => permission switch
    {
        ModbotPermissions.ViewProfile => "See profiles",
        ModbotPermissions.ViewAuditLog => "See the audit log",
        ModbotPermissions.Kick => "Kick",
        ModbotPermissions.Ban => "Ban",
        ModbotPermissions.WriteNotes => "Write notes",
        ModbotPermissions.ViewLiveInstances => "See live instances",
        ModbotPermissions.AnswerJoinRequests => "Answer join requests",
        _ => permission.ToString(),
    };

    /// <summary>Same rule as the web app: Administrator may do everything (spec 7.3).</summary>
    public static bool Allows(ModbotPermissions held, ModbotPermissions required)
        => held.HasFlag(ModbotPermissions.Administrator) || (held & required) == required;
}
