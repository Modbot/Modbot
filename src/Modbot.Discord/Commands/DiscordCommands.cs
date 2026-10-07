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
/// Every command but <see cref="Link"/>, <see cref="Me"/>, <see cref="Verify"/>, <see cref="Help"/>,
/// <see cref="Events"/> and <see cref="RemindMe"/> needs a Modbot account linked to the caller's Discord user id. The permission on top of that is the same one the
/// equivalent web page asks for, so the bot never shows anybody more than the web app would.
/// <see cref="Link"/> is for every member: it answers with the link page's address and nothing else
/// (Discord account linking design §2). <see cref="Me"/> is for every member too, and only ever about
/// the member who ran it; it is registered only while the operator has switched it on (Discord /me
/// design). <see cref="Events"/> is for every member too and answers in public unless the member
/// asks otherwise; it is registered only while the operator has switched it on, and shows nothing
/// that is not already published in the Discord server (Discord commands design §3.6).
/// <see cref="RemindMe"/> is for every member too and answers in private; it is off until the operator
/// turns it on, sends the member one direct message before an event they pick, and offers only the
/// events <see cref="Events"/> would show (Discord commands design §3.5).
/// <see cref="Help"/> lists the commands the caller can use, and nothing about anybody.
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
    public const string Ban = "ban";
    public const string Kick = "kick";
    public const string Gate = "gate";
    public const string Events = "events";
    public const string RemindMe = "remindme";

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

    /// <summary>
    /// Where <c>/kick</c> removes somebody from: one of <see cref="FromDiscord"/>, <see cref="FromVRChat"/>
    /// or <see cref="FromBoth"/>. Left out, it follows the person named: a Discord member is removed
    /// from the server, a VRChat person from the group (Discord commands design §3.3, decision 5).
    /// </summary>
    public const string KickFromOption = "from";
    public const string FromDiscord = "discord";
    public const string FromVRChat = "vrchat";
    public const string FromBoth = "both";

    /// <summary>The steps under <c>/gate</c>.</summary>
    public const string GateWaiting = "waiting";
    public const string GateLetIn = "let-in";
    public const string GateHold = "hold";
    public const string GateLift = "lift";

    /// <summary>How many people <c>/gate waiting</c> lists, each with a button to let them in.</summary>
    public const int GateWaitingMost = 10;

    /// <summary>The yes/no option that makes <c>/events</c> answer in private.</summary>
    public const string EventsPrivateOption = "private";

    /// <summary>How many dates <c>/events</c> shows.</summary>
    public const int EventsMost = 5;

    /// <summary>How far ahead <c>/events</c> looks, in days.</summary>
    public const int EventsDays = 14;

    /// <summary>
    /// After a public <c>/events</c> in a channel, how long a second one in the same channel answers
    /// in private (Discord commands design §3.2).
    /// </summary>
    public static readonly TimeSpan EventsPublicOncePer = TimeSpan.FromSeconds(60);

    /// <summary>The event <c>/remindme</c> is about: picked from the suggestions, whose value is the event's id.</summary>
    public const string RemindEventOption = "event";

    /// <summary>How long before the event <c>/remindme</c> sends its message: one of the <c>RemindBefore…</c> values.</summary>
    public const string RemindBeforeOption = "before";
    public const string RemindBefore15Minutes = "15m";
    public const string RemindBefore1Hour = "1h";
    public const string RemindBefore1Day = "1d";

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
            Ban,
            "Ban someone",
            [
                new DiscordCommandOption(MemberOption, "Discord member", DiscordOptionKind.Member, Required: false),
                new DiscordCommandOption(VRChatOption, "VRChat name or id", DiscordOptionKind.Text, Required: false, Suggests: true),
            ],
            ShownTo: DiscordShownTo.Moderators),
        new(
            Kick,
            "Remove someone",
            [
                new DiscordCommandOption(MemberOption, "Discord member", DiscordOptionKind.Member, Required: false),
                new DiscordCommandOption(VRChatOption, "VRChat name or id", DiscordOptionKind.Text, Required: false, Suggests: true),
                new DiscordCommandOption(
                    KickFromOption,
                    "Remove from",
                    DiscordOptionKind.Choice,
                    Required: false,
                    Choices:
                    [
                        new("Discord server", FromDiscord),
                        new("VRChat group", FromVRChat),
                        new("Both", FromBoth),
                    ]),
            ],
            ShownTo: DiscordShownTo.Moderators),
        new(
            Gate,
            "The join gate",
            [],
            ShownTo: DiscordShownTo.Moderators,
            Subcommands:
            [
                new DiscordSubcommand(GateWaiting, "Who is waiting", []),
                new DiscordSubcommand(
                    GateLetIn,
                    "Let someone in",
                    [new DiscordCommandOption(MemberOption, "Who", DiscordOptionKind.Member, Required: true)]),
                new DiscordSubcommand(GateHold, "Hold new joiners", []),
                new DiscordSubcommand(GateLift, "Lift the hold", []),
            ]),
        new(
            Events,
            "Upcoming events, in your time zone",
            [new DiscordCommandOption(EventsPrivateOption, "Only show me", DiscordOptionKind.YesNo, Required: false)],
            Reply: DiscordReplyKind.Chosen,
            PrivateOption: EventsPrivateOption,
            PublicOncePer: EventsPublicOncePer),
        new(
            RemindMe,
            "Get a message before an event",
            [
                new DiscordCommandOption(RemindEventOption, "Which event", DiscordOptionKind.Text, Required: false, Suggests: true),
                new DiscordCommandOption(
                    RemindBeforeOption,
                    "How long before",
                    DiscordOptionKind.Choice,
                    Required: false,
                    Choices:
                    [
                        new("15 minutes", RemindBefore15Minutes),
                        new("1 hour", RemindBefore1Hour),
                        new("1 day", RemindBefore1Day),
                    ]),
            ]),
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
    public static bool IsForEveryone(string command) => command is Link or Me or Help or Verify or Events or RemindMe;

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
        Ban => ModbotPermissions.Ban,
        Kick => ModbotPermissions.Kick,
        Gate => ModbotPermissions.ManageJoinGate,
        _ => null,
    };

    /// <summary>
    /// The permission a command needs on the other platform, when it acts on both: <c>/ban</c> on
    /// somebody with no VRChat link is a Discord ban ("Ban on Discord"), and <c>/kick</c> can remove
    /// somebody from the Discord server alone ("Remove from Discord"). Null for every other command.
    /// </summary>
    public static ModbotPermissions? RequiresOnDiscord(string command) => command switch
    {
        Ban => ModbotPermissions.DiscordBan,
        Kick => ModbotPermissions.DiscordKick,
        _ => null,
    };

    /// <summary>
    /// Whether these permissions let somebody use the command at all: the one <see cref="Requires"/>
    /// names, or for <c>/ban</c> and <c>/kick</c> either that or the Discord one. Which of the two a
    /// run needs depends on who it is about, and is checked once that is known.
    /// </summary>
    public static bool CanUse(string command, ModbotPermissions held)
        => Requires(command) is { } required
            && (Allows(held, required)
                || (RequiresOnDiscord(command) is { } onDiscord && Allows(held, onDiscord)));

    /// <summary>
    /// Commands that write something. The web app refuses every request from an account with no
    /// VRChat link, so these do too (acting from Discord design §3). <c>/gate waiting</c> only
    /// reads; the other steps of <c>/gate</c> write. Asked without a step, <c>/gate</c> counts as
    /// writing, which is the safe way for a list of what somebody may use.
    /// </summary>
    public static bool Writes(string command, string? subcommand = null)
        => command is Note or Watch or Ban or Kick
            || (command == Gate && subcommand != GateWaiting);

    /// <summary>The permission's label as the web app's role editor shows it.</summary>
    public static string Label(ModbotPermissions permission) => permission switch
    {
        ModbotPermissions.ViewProfile => "See profiles",
        ModbotPermissions.ViewAuditLog => "See the audit log",
        ModbotPermissions.Kick => "Kick",
        ModbotPermissions.Ban => "Ban",
        ModbotPermissions.WriteNotes => "Write notes",
        ModbotPermissions.ViewLiveInstances => "See live instances",
        ModbotPermissions.DiscordBan => "Ban on Discord",
        ModbotPermissions.DiscordKick => "Remove from Discord",
        ModbotPermissions.ManageJoinGate => "Manage the join gate",
        ModbotPermissions.ViewMembers => "See members",
        ModbotPermissions.AnswerJoinRequests => "Answer join requests",
        _ => permission.ToString(),
    };

    /// <summary>Same rule as the web app: Administrator may do everything (spec 7.3).</summary>
    public static bool Allows(ModbotPermissions held, ModbotPermissions required)
        => held.HasFlag(ModbotPermissions.Administrator) || (held & required) == required;
}
