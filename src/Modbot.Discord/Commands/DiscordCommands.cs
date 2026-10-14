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
/// Every command but <see cref="Link"/>, <see cref="Me"/>, <see cref="VerifyAccount"/>, <see cref="Help"/>,
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
/// <see cref="Report"/> and the <see cref="ReportMenu"/> message menu are for every member too and
/// answer in private; they are off until the operator turns them on, need no Modbot account, and
/// leave no <c>modbot.discord.command</c> fact, because its subject would be the reporter (Discord
/// commands design §3.4).
/// <see cref="Profile"/> is for every member too and answers in private; it is off until the operator
/// turns it on, needs no Modbot account, and shows a member only the profile fields their owner lets
/// that viewer read (member profiles design §4).
/// <see cref="Find"/> is for every member too and answers in private; it is off until the operator
/// turns it on, needs no Modbot account, and lists only members who turned on Findable, matching
/// them only on answers the searcher may read (find design).
/// <see cref="Term"/> and <see cref="Rule"/> are for every member too and answer in private; they are
/// off until the operator turns them on, need no Modbot account, and answer from the words staff
/// type on the Terms and rules page.
/// <see cref="Help"/> lists the commands the caller can use, and nothing about anybody.
/// <see cref="Event"/> and <see cref="Post"/> are for event hosts: Discord shows them to members who
/// may manage events (<see cref="DiscordShownTo.EventHosts"/>), and Modbot asks Manage calendar for
/// <c>/event</c>, Manage posts for <c>/post new</c> and See posts for <c>/post list</c>
/// (<see cref="RequiresFor"/>).
/// <see cref="VerifyAccount"/> is how a staff member proves their Discord account with a code from their
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
    public const string VerifyAccount = StaffDiscordCodes.Command;
    public const string Note = "note";
    public const string Watch = "watch";
    public const string Live = "live";
    public const string Ban = "ban";
    public const string Kick = "kick";
    public const string Gate = "gate";

    /// <summary>
    /// <c>/ageverified</c>: the 18+ check, typed in a ticket. Staff only; it sets the flag, gives and
    /// takes the roles the server chose, and may delete the ticket. Off until the operator turns it on.
    /// </summary>
    public const string AgeVerified = "ageverified";

    /// <summary>How the 18+ check was done: one of <see cref="AgeCheckMethods"/>.</summary>
    public const string AgeMethodOption = "method";
    public const string Events = "events";
    public const string RemindMe = "remindme";

    /// <summary>
    /// <c>/notify</c>: sign up to be told before events of the Modbot categories (event alerts design
    /// §2). For every member, private, and not on the Commands card: it is registered while the
    /// Alerts setting is on, and not otherwise.
    /// </summary>
    public const string Notify = "notify";
    public const string Event = "event";
    public const string Post = "post";
    public const string Report = "report";
    public const string Profile = "profile";
    public const string Find = "find";

    /// <summary>The profile field <c>/find</c> looks in, picked from the suggestions.</summary>
    public const string FindFieldOption = "field";

    /// <summary>What the answer to <see cref="FindFieldOption"/> must say: an option of a pick-list, or words a text answer holds.</summary>
    public const string FindValueOption = "value";

    /// <summary>The second field <c>/find</c> may also look in.</summary>
    public const string FindAlsoFieldOption = "also-field";

    /// <summary>What the answer to <see cref="FindAlsoFieldOption"/> must say.</summary>
    public const string FindAlsoValueOption = "also-value";

    /// <summary>
    /// <c>/birthday</c>: a member's month and day, never a year (birthdays). For every member, private,
    /// and needs no Modbot account; off until the operator turns it on.
    /// </summary>
    public const string Birthday = "birthday";

    public const string BirthdaySet = "set";
    public const string BirthdayRemove = "remove";
    public const string BirthdayShow = "show";

    /// <summary>The month <c>/birthday set</c> takes, 1 to 12.</summary>
    public const string BirthdayMonthOption = "month";

    /// <summary>The day of the month <c>/birthday set</c> takes, 1 to 31.</summary>
    public const string BirthdayDayOption = "day";

    /// <summary>
    /// <c>/term</c>: what a word the community uses means, from the Terms and rules page. For every
    /// member, private, and needs no Modbot account; off until the operator turns it on.
    /// </summary>
    public const string Term = "term";

    /// <summary>The word <c>/term</c> explains, picked from the suggestions (a term's name or one of its other names).</summary>
    public const string TermNameOption = "name";

    /// <summary>
    /// <c>/rule</c>: one of the community's rules, from the Terms and rules page. For every member,
    /// private, and needs no Modbot account; off until the operator turns it on.
    /// </summary>
    public const string Rule = "rule";

    /// <summary>The rule <c>/rule</c> shows, picked from the suggestions (its number) or typed as a number or a title.</summary>
    public const string RuleWhichOption = "rule";

    /// <summary>
    /// <c>/vote</c>: asks members to vote on a question, in a channel the caller picks. For event
    /// hosts, who need Manage posts; off until the operator turns it on.
    /// </summary>
    public const string Vote = "vote";

    /// <summary>Where <c>/vote</c> posts the question.</summary>
    public const string VoteChannelOption = "channel";

    /// <summary>The role a person must hold to vote, when it is not everybody.</summary>
    public const string VoteRoleOption = "only_for";

    /// <summary>Yes: the message shows the count for each option while the vote is open. Off when left out.</summary>
    public const string VoteShowCountsOption = "show_counts";

    /// <summary>Yes: the Modbot page shows who picked each option. Left out, the vote is anonymous.</summary>
    public const string VoteShowWhoOption = "show_who_voted";

    /// <summary>The message menu "Report to mods": for every member, off until the operator turns it on.</summary>
    public const string ReportMenu = "Report to mods";

    /// <summary>What <c>/report</c> says went wrong: 1 to <see cref="ReportWhatMax"/> characters.</summary>
    public const string ReportWhatOption = "what";

    public const int ReportWhatMax = 1000;

    /// <summary>The code from the account page that <c>/verifyaccount</c> takes.</summary>
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

    /// <summary>The steps under <c>/event</c>.</summary>
    public const string EventOpen = "open";
    public const string EventCancelDate = "cancel-date";

    /// <summary>
    /// The event <c>/event</c> is about: an upcoming event picked from the suggestions, carried by its
    /// id.
    /// </summary>
    public const string EventOption = "event";

    /// <summary>The date of the event <c>/event cancel-date</c> cancels, picked from the suggestions.</summary>
    public const string EventDateOption = "date";

    /// <summary>Also say, in the event's channel, that the date is cancelled.</summary>
    public const string EventSayOption = "say-so";

    /// <summary>The steps under <c>/post</c>.</summary>
    public const string PostNew = "new";
    public const string PostList = "list";

    /// <summary>The Discord channel <c>/post new</c> posts in, picked from Discord's own list.</summary>
    public const string PostChannelOption = "channel";

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
            AgeVerified,
            "Mark a member 18+ verified",
            [
                new DiscordCommandOption(MemberOption, "Who", DiscordOptionKind.Member, Required: true),
                new DiscordCommandOption(
                    AgeMethodOption,
                    "How",
                    DiscordOptionKind.Choice,
                    Required: true,
                    Choices:
                    [
                        new("Badge", AgeCheckMethods.Badge),
                        new("Photo ID", AgeCheckMethods.Photo),
                    ]),
                new DiscordCommandOption(VRChatOption, "VRChat profile link", DiscordOptionKind.Text, Required: false),
            ],
            ShownTo: DiscordShownTo.Moderators),
        new(
            Event,
            "Calendar events",
            [],
            ShownTo: DiscordShownTo.EventHosts,
            Subcommands:
            [
                new DiscordSubcommand(
                    EventOpen,
                    "Open the instance now",
                    [new DiscordCommandOption(EventOption, "Which event", DiscordOptionKind.Text, Required: true, Suggests: true)]),
                new DiscordSubcommand(
                    EventCancelDate,
                    "Cancel one date",
                    [
                        new DiscordCommandOption(EventOption, "Which event", DiscordOptionKind.Text, Required: true, Suggests: true),
                        new DiscordCommandOption(EventDateOption, "Which date", DiscordOptionKind.Text, Required: true, Suggests: true),
                        new DiscordCommandOption(EventSayOption, "Say so in the event's channel", DiscordOptionKind.YesNo, Required: false),
                    ]),
            ]),
        new(
            Post,
            "Posts",
            [],
            ShownTo: DiscordShownTo.EventHosts,
            Subcommands:
            [
                new DiscordSubcommand(
                    PostNew,
                    "Write a post now",
                    [new DiscordCommandOption(PostChannelOption, "Where to post", DiscordOptionKind.Channel, Required: true)]),
                new DiscordSubcommand(PostList, "Posts waiting to go out", []),
            ]),
        new(
            Vote,
            "Ask members to vote",
            [
                new DiscordCommandOption(VoteChannelOption, "Where to post the vote", DiscordOptionKind.Channel, Required: true),
                new DiscordCommandOption(VoteRoleOption, "Only people with this role can vote", DiscordOptionKind.Role, Required: false),
                new DiscordCommandOption(VoteShowCountsOption, "Show the count for each option while the vote is open", DiscordOptionKind.YesNo, Required: false),
                new DiscordCommandOption(VoteShowWhoOption, "Staff can see who picked what on the Modbot page", DiscordOptionKind.YesNo, Required: false),
            ],
            ShownTo: DiscordShownTo.EventHosts),
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
            Notify,
            "Get told before events you care about",
            []),
        new(
            Report,
            "Tell the mods about someone, privately",
            [
                new DiscordCommandOption(MemberOption, "Who", DiscordOptionKind.Member, Required: true),
                new DiscordCommandOption(ReportWhatOption, "What happened", DiscordOptionKind.Text, Required: true, Min: 1, Max: ReportWhatMax),
            ]),
        new(
            Profile,
            "Your profile, or a member's",
            [new DiscordCommandOption(MemberOption, "Whose profile", DiscordOptionKind.Member, Required: false)]),
        new(
            Find,
            "Find members by their profile",
            [
                new DiscordCommandOption(FindFieldOption, "Which profile field", DiscordOptionKind.Text, Required: true, Suggests: true),
                new DiscordCommandOption(FindValueOption, "What it says", DiscordOptionKind.Text, Required: true, Suggests: true),
                new DiscordCommandOption(FindAlsoFieldOption, "A second profile field", DiscordOptionKind.Text, Required: false, Suggests: true),
                new DiscordCommandOption(FindAlsoValueOption, "What it says", DiscordOptionKind.Text, Required: false, Suggests: true),
            ]),
        new(
            Birthday,
            "Your birthday",
            [],
            Subcommands:
            [
                new DiscordSubcommand(
                    BirthdaySet,
                    "Set your birthday",
                    [
                        new DiscordCommandOption(
                            BirthdayMonthOption,
                            "Month",
                            DiscordOptionKind.Choice,
                            Required: true,
                            Choices:
                            [
                                new("January", "1"),
                                new("February", "2"),
                                new("March", "3"),
                                new("April", "4"),
                                new("May", "5"),
                                new("June", "6"),
                                new("July", "7"),
                                new("August", "8"),
                                new("September", "9"),
                                new("October", "10"),
                                new("November", "11"),
                                new("December", "12"),
                            ]),
                        new DiscordCommandOption(BirthdayDayOption, "Day", DiscordOptionKind.WholeNumber, Required: true, Min: 1, Max: 31),
                    ]),
                new DiscordSubcommand(BirthdayShow, "Show your birthday", []),
                new DiscordSubcommand(BirthdayRemove, "Forget your birthday", []),
            ]),
        new(
            Term,
            "What a word in this community means",
            [new DiscordCommandOption(TermNameOption, "The word", DiscordOptionKind.Text, Required: true, Suggests: true, Min: 1, Max: 100)]),
        new(
            Rule,
            "One of the community's rules",
            [new DiscordCommandOption(RuleWhichOption, "Which rule", DiscordOptionKind.Text, Required: true, Suggests: true, Min: 1, Max: 100)]),
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
            VerifyAccount,
            "Connect your Discord account to your Modbot account",
            [new DiscordCommandOption(VerifyCodeOption, "The code from your account page in Modbot", DiscordOptionKind.Text, Required: true)]),

        // The right-click menus staff act from (acting from Discord design §2). Registered beside
        // the slash commands because Discord replaces the whole set in one call.
        .. Interactions.StaffMenus.All,

        // The one message menu everyone sees (Discord commands design §3.4, decision 3).
        new(ReportMenu, string.Empty, [], DiscordCommandKind.Message),
    ];

    /// <summary>
    /// The commands to register on the server: the ones switched on in the <c>discord_commands</c>
    /// setting (<see cref="DiscordCommandSwitches"/>), a missing name taking its default. Not
    /// registered is cleaner than registered and refusing: a member never sees a command that would
    /// only tell them it is off.
    /// </summary>
    /// <param name="switches">The setting as stored. Null or empty means every default.</param>
    /// <param name="alertsOn">
    /// Whether event alerts are on. <c>/notify</c> follows this and not the switches: it is there
    /// while they are on and gone while they are off.
    /// </param>
    public static IReadOnlyList<DiscordCommandDefinition> For(string? switches, bool alertsOn = false)
        => [.. All.Where(c => c.Name == Notify ? alertsOn : DiscordCommandSwitches.IsOn(switches, c.Name))];

    /// <summary>
    /// The names registered for these switches. Two lists with the same names are the same
    /// registration: the bot registers again only when this changes (Discord commands design §3.8).
    /// </summary>
    public static IReadOnlySet<string> NamesFor(string? switches, bool alertsOn = false)
        => For(switches, alertsOn).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Commands any member may run, with no Modbot account.</summary>
    public static bool IsForEveryone(string command) => command is Link or Me or Help or VerifyAccount or Events or RemindMe or Notify or Report or Profile or Find or Birthday or Term or Rule;

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
        AgeVerified => ModbotPermissions.EditAgeVerification,
        Event => ModbotPermissions.ManageCalendar,
        Vote => ModbotPermissions.ManagePosts,

        // The lowest a step of /post needs: reading the list. RequiresFor names the step's own.
        Post => ModbotPermissions.ViewPosts,
        _ => null,
    };

    /// <summary>
    /// The permission one step of a command needs. The same as <see cref="Requires"/> for a command
    /// that asks one thing of every step; <c>/post new</c> writes a post, which the web app asks
    /// Manage posts for, while <c>/post list</c> only reads them (See posts).
    /// </summary>
    public static ModbotPermissions? RequiresFor(string command, string? subcommand)
        => command == Post && subcommand == PostNew
            ? ModbotPermissions.ManagePosts
            : Requires(command);

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
                || (RequiresOnDiscord(command) is { } onDiscord && Allows(held, onDiscord))
                || (command == Post && Allows(held, ModbotPermissions.ManagePosts)));

    /// <summary>
    /// Commands that write something. The web app refuses every request from an account with no
    /// VRChat link, so these do too (acting from Discord design §3). <c>/gate waiting</c> only
    /// reads; the other steps of <c>/gate</c> write. <c>/post list</c> only reads; <c>/post new</c>
    /// writes. Both steps of <c>/event</c> write. Asked without a step, <c>/gate</c> and
    /// <c>/post</c> count as writing, which is the safe way for a list of what somebody may use.
    /// </summary>
    public static bool Writes(string command, string? subcommand = null)
        => command is Note or Watch or Ban or Kick or Event or AgeVerified or Vote
            || (command == Gate && subcommand != GateWaiting)
            || (command == Post && subcommand != PostList);

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
        ModbotPermissions.EditAgeVerification => "Edit 18+ verified",
        ModbotPermissions.ViewMembers => "See members",
        ModbotPermissions.ManageCalendar => "Manage calendar",
        ModbotPermissions.ViewCalendar => "See calendar",
        ModbotPermissions.ManagePosts => "Manage posts",
        ModbotPermissions.ViewPosts => "See posts",
        ModbotPermissions.AnswerJoinRequests => "Answer join requests",
        _ => permission.ToString(),
    };

    /// <summary>Same rule as the web app: Administrator may do everything (spec 7.3).</summary>
    public static bool Allows(ModbotPermissions held, ModbotPermissions required)
        => held.HasFlag(ModbotPermissions.Administrator) || (held & required) == required;
}
