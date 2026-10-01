namespace Modbot.Discord.Gateway;

/// <summary>Where a gateway session is. <c>Ready</c> is the only state that answers or posts.</summary>
public enum DiscordGatewayState
{
    Disconnected = 0,
    Connecting = 1,
    Ready = 2,
}

public enum DiscordOptionKind
{
    Text = 1,
    WholeNumber = 2,
}

/// <summary>One argument of a slash command, as Discord needs it described up front.</summary>
public sealed record DiscordCommandOption(
    string Name,
    string Description,
    DiscordOptionKind Kind,
    bool Required,
    long? Min = null,
    long? Max = null);

/// <summary>Where a command is run from.</summary>
public enum DiscordCommandKind
{
    /// <summary>Typed as <c>/name</c>.</summary>
    Slash = 1,

    /// <summary>Right-click a member, then Apps.</summary>
    User = 2,

    /// <summary>Right-click a message, then Apps.</summary>
    Message = 3,
}

/// <summary>One command, as registered on the guild.</summary>
/// <param name="Name">
/// A slash command's name is lower case with no spaces. A right-click menu's name is what the menu
/// shows, so it is ordinary words.
/// </param>
/// <param name="Description">A slash command's one line. Discord takes none for a right-click menu.</param>
/// <param name="StaffOnly">
/// Hidden from members who lack Discord's Timeout Members permission, until a server admin changes
/// that in the server's Integrations settings (<c>default_member_permissions</c>). Only hides it:
/// Modbot's own permission check is what decides.
/// </param>
public sealed record DiscordCommandDefinition(
    string Name,
    string Description,
    IReadOnlyList<DiscordCommandOption> Options,
    DiscordCommandKind Kind = DiscordCommandKind.Slash,
    bool StaffOnly = false);

public sealed record DiscordEmbedField(string Name, string Value, bool Inline = false);

/// <summary>
/// A picture sent with a message, which the message's own embeds point at.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a card's pictures are uploaded rather than linked.</strong> VRChat's hosts refuse a
/// request that carries no session, and Discord fetches an embed's pictures from its own servers,
/// signed in as nobody. A VRChat address in an embed is therefore always a broken picture, and
/// Modbot's own <c>/api/files/vrchat</c> route is no better: it needs a signed-in caller, and most
/// deployments have no address the internet can reach at all. Sending the bytes is the only way
/// that works everywhere, and once they are sent Discord owns them: the card keeps its picture
/// when Modbot is offline and when VRChat moves the file.
/// </para>
/// <para>
/// <strong>Paid for once.</strong> An instance card is rewritten every minute for hours. An edit
/// that sends no pictures keeps the ones the message already has, so the upload happens on the
/// first post and never again.
/// </para>
/// </remarks>
/// <param name="Name">
/// The file name, which an embed refers to as <c>attachment://name</c>. It must end in the
/// extension for the bytes -- Discord decides what a file is from its name, and a picture called
/// <c>.dat</c> is shown as a download rather than in the card.
/// </param>
public sealed record DiscordPicture(string Name, byte[] Bytes)
{
    /// <summary>How many files Discord accepts on one message.</summary>
    public const int PerMessage = 10;

    /// <summary>The scheme an embed uses to point at a file sent with the same message.</summary>
    public const string Scheme = "attachment://";

    /// <summary>The address an embed refers to this picture by.</summary>
    public string Reference => Scheme + Name;
}

/// <summary>
/// A rich message card, described without the library's types so the formatting can be tested
/// and the library swapped. Sizes are Discord's: title 256, description 4096, field value 1024.
/// </summary>
/// <remarks>
/// Every picture address may be either an https address or <c>attachment://name</c>, naming a
/// <see cref="DiscordPicture"/> sent with the same message. <see cref="Url"/> is the card's click
/// target and stays https only: it is a page, not a picture.
/// </remarks>
/// <param name="ImageUrl">A large picture across the bottom of the card.</param>
/// <param name="ThumbnailUrl">A small picture in the top corner.</param>
/// <param name="AuthorName">
/// A line above the title, for the person or thing the card is about. Discord allows 256
/// characters and shows it smaller than the title, which is why a card about one person puts
/// their name here and keeps the title for what happened to them.
/// </param>
/// <param name="AuthorUrl">Where the author line goes when clicked. Only an https address is used.</param>
/// <param name="AuthorIconUrl">The small round picture beside the author line.</param>
public sealed record DiscordEmbedContent(
    string Title,
    string? Description,
    uint Color,
    IReadOnlyList<DiscordEmbedField> Fields,
    DateTimeOffset? Timestamp,
    string? Url,
    string? Footer,
    string? ImageUrl = null,
    string? ThumbnailUrl = null,
    string? FooterIconUrl = null,
    string? AuthorName = null,
    string? AuthorUrl = null,
    string? AuthorIconUrl = null);

/// <summary>A button under a message that opens a web address. Only an https address is used.</summary>
public sealed record DiscordLinkButton(string Label, string Url);

/// <summary>
/// A button under a reply that asks the bot to do something, rather than opening an address. A
/// press arrives as a <see cref="DiscordButtonPress"/> carrying <see cref="Id"/>.
/// </summary>
/// <param name="Id">
/// What the press asks for. Always starts with <see cref="Prefix"/>, so a press on a button some
/// other bot or an older Modbot put there is told apart and left alone. Discord allows 100
/// characters.
/// </param>
/// <param name="Style">How the button looks. Red is for the press that bans or kicks.</param>
public sealed record DiscordActionButton(string Label, string Id, DiscordButtonStyle Style = DiscordButtonStyle.Plain)
{
    public const string Prefix = "modbot:";

    /// <summary>
    /// What a button in a direct message ends with: this mark and the server's id. A press in a
    /// direct message carries no server, so the mark is how the one Modbot it belongs to knows it
    /// among several sharing one bot (the join gate's Get in, join gate design §4).
    /// </summary>
    public const string ServerMark = "@";

    /// <summary>The id with the server mark taken off, for comparing with a button's plain id.</summary>
    public static string Plain(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var at = id.IndexOf(ServerMark, StringComparison.Ordinal);
        return at < 0 ? id : id[..at];
    }

    /// <summary>An id that a press in a direct message can be traced back to this server by.</summary>
    public static string Marked(string id, string guildId) => id + ServerMark + guildId;
}

public enum DiscordButtonStyle
{
    /// <summary>Grey.</summary>
    Plain = 0,

    /// <summary>Discord's own blue, for the one thing a message asks you to do.</summary>
    Main = 1,

    /// <summary>Red, for a press that changes something for somebody else: a ban, a kick.</summary>
    Danger = 2,
}

/// <summary>What kind of box a form field is.</summary>
public enum DiscordFormFieldKind
{
    /// <summary>One line of text.</summary>
    ShortText = 1,

    /// <summary>Several lines of text.</summary>
    LongText = 2,

    /// <summary>A list to pick from.</summary>
    Choice = 3,
}

/// <summary>One thing a <see cref="DiscordFormFieldKind.Choice"/> field offers.</summary>
/// <param name="Value">What comes back when it is picked. Discord allows 100 characters.</param>
/// <param name="Description">A line under the label, or null.</param>
public sealed record DiscordChoice(string Label, string Value, string? Description = null);

/// <summary>One field of a <see cref="DiscordForm"/>.</summary>
/// <param name="Id">What the field's answer comes back under in <see cref="DiscordFormSubmit.Values"/>.</param>
/// <param name="MaxLength">For text: the most characters. Discord allows 4,000.</param>
/// <param name="Choices">For a list: what it offers. Discord allows 25.</param>
/// <param name="MaxChoices">For a list: how many may be picked.</param>
public sealed record DiscordFormField(
    string Id,
    string Label,
    DiscordFormFieldKind Kind,
    bool Required,
    string? Placeholder = null,
    int? MaxLength = null,
    IReadOnlyList<DiscordChoice>? Choices = null,
    int MaxChoices = 1);

/// <summary>
/// A form Discord shows over the app (Discord calls it a modal). Sending it arrives as a
/// <see cref="DiscordFormSubmit"/> carrying <see cref="Id"/>.
/// </summary>
/// <param name="Title">Discord shows 45 characters.</param>
/// <param name="Id">Always starts with <see cref="DiscordActionButton.Prefix"/>, like a button's.</param>
public sealed record DiscordForm(string Title, string Id, IReadOnlyList<DiscordFormField> Fields);

/// <summary>The member a right-click menu was used on.</summary>
public sealed record DiscordTargetUser(string Id, string Username, bool IsBot);

/// <summary>The message a right-click menu was used on, as Discord handed it over.</summary>
/// <param name="ChannelName">The channel's name, without the #, when Discord sent it.</param>
/// <param name="Text">
/// The message's words. Discord hands these over for the message a menu was used on even without
/// the Message Content intent.
/// </param>
/// <param name="Url">The message's own link, which opens it in Discord.</param>
public sealed record DiscordTargetMessage(
    string Id,
    string ChannelId,
    string? ChannelName,
    string AuthorId,
    string AuthorName,
    bool AuthorIsBot,
    string Text,
    DateTimeOffset SentAt,
    string Url);

/// <summary>What the bot says back to a command. Always visible only to the person who asked.</summary>
/// <param name="Links">Buttons under the reply that open a web address, or null for none.</param>
/// <param name="Pictures">Files the reply's cards point at by <c>attachment://name</c>.</param>
/// <param name="Actions">Buttons under the reply that the bot answers when pressed, after the links.</param>
public sealed record DiscordReply(
    string? Text,
    IReadOnlyList<DiscordEmbedContent> Embeds,
    IReadOnlyList<DiscordLinkButton>? Links = null,
    IReadOnlyList<DiscordPicture>? Pictures = null,
    IReadOnlyList<DiscordActionButton>? Actions = null)
{
    public static DiscordReply Say(string text) => new(text, []);

    public static DiscordReply Card(DiscordEmbedContent embed) => new(null, [embed]);

    public static DiscordReply Card(DiscordEmbedContent embed, IReadOnlyList<DiscordPicture>? pictures)
        => new(null, [embed], null, pictures);
}

/// <summary>
/// A slash command somebody ran, with a way to answer it. Ids are strings end to end: Modbot
/// stores Discord ids as text and never does arithmetic on them.
/// </summary>
public sealed class DiscordCommandCall
{
    private readonly Func<DiscordReply, CancellationToken, Task> _reply;
    private readonly Func<DiscordForm, CancellationToken, Task>? _showForm;

    /// <param name="showForm">
    /// Shows a form as the answer. Null where none can be shown: a slash command has already been
    /// acknowledged before it is raised.
    /// </param>
    public DiscordCommandCall(
        string discordUserId,
        string discordUsername,
        string commandName,
        IReadOnlyDictionary<string, string> options,
        Func<DiscordReply, CancellationToken, Task> reply,
        Func<DiscordForm, CancellationToken, Task>? showForm = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reply);

        DiscordUserId = discordUserId;
        DiscordUsername = discordUsername;
        CommandName = commandName;
        Options = options;
        _reply = reply;
        _showForm = showForm;
    }

    public string DiscordUserId { get; }

    public string DiscordUsername { get; }

    public string CommandName { get; }

    public IReadOnlyDictionary<string, string> Options { get; }

    /// <summary>A slash command, or one of the right-click menus.</summary>
    public DiscordCommandKind Kind { get; init; } = DiscordCommandKind.Slash;

    /// <summary>The member a <see cref="DiscordCommandKind.User"/> menu was used on.</summary>
    public DiscordTargetUser? TargetUser { get; init; }

    /// <summary>The message a <see cref="DiscordCommandKind.Message"/> menu was used on.</summary>
    public DiscordTargetMessage? TargetMessage { get; init; }

    public string? Option(string name)
        => Options.TryGetValue(name, out var value) ? value : null;

    public Task ReplyAsync(DiscordReply reply, CancellationToken ct = default) => _reply(reply, ct);

    /// <summary>
    /// Shows a form instead of a reply. Only as the first answer, and only within Discord's three
    /// seconds; throws when it can no longer be shown.
    /// </summary>
    public Task ShowFormAsync(DiscordForm form, CancellationToken ct = default)
        => _showForm is null
            ? throw new InvalidOperationException("A form cannot be shown for this command.")
            : _showForm(form, ct);
}

/// <summary>
/// Somebody pressed one of the bot's <see cref="DiscordActionButton"/>s, with a way to answer. The
/// answer is visible only to them, like a command's.
/// </summary>
public sealed class DiscordButtonPress
{
    private readonly Func<DiscordReply, CancellationToken, Task> _reply;
    private readonly Func<DiscordForm, CancellationToken, Task>? _showForm;
    private readonly Func<DiscordReply, CancellationToken, Task>? _update;

    /// <param name="showForm">Shows a form as the answer, or null where none can be shown.</param>
    /// <param name="update">
    /// Rewrites the message the button sits on, or null where that cannot be done.
    /// </param>
    public DiscordButtonPress(
        string discordUserId,
        string discordUsername,
        string buttonId,
        Func<DiscordReply, CancellationToken, Task> reply,
        Func<DiscordForm, CancellationToken, Task>? showForm = null,
        Func<DiscordReply, CancellationToken, Task>? update = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(buttonId);
        ArgumentNullException.ThrowIfNull(reply);

        DiscordUserId = discordUserId;
        DiscordUsername = discordUsername;
        ButtonId = buttonId;
        _reply = reply;
        _showForm = showForm;
        _update = update;
    }

    public string DiscordUserId { get; }

    public string DiscordUsername { get; }

    /// <summary>The <see cref="DiscordActionButton.Id"/> of the button pressed.</summary>
    public string ButtonId { get; }

    /// <summary>
    /// The channel of the message the button sits on, when that message is a card in a channel;
    /// null under a reply only the presser can see.
    /// </summary>
    public string? CardChannelId { get; init; }

    /// <summary>The card's message id, beside <see cref="CardChannelId"/>.</summary>
    public string? CardMessageId { get; init; }

    public Task ReplyAsync(DiscordReply reply, CancellationToken ct = default) => _reply(reply, ct);

    /// <summary>Shows a form instead of a reply. Only as the first answer; throws when it can no longer be shown.</summary>
    public Task ShowFormAsync(DiscordForm form, CancellationToken ct = default)
        => _showForm is null
            ? throw new InvalidOperationException("A form cannot be shown for this button.")
            : _showForm(form, ct);

    /// <summary>
    /// Rewrites the message the button sits on with <paramref name="reply"/>, buttons and all; meant
    /// for a private message, such as a confirmation. Falls back to a new reply where it cannot.
    /// </summary>
    public Task UpdateAsync(DiscordReply reply, CancellationToken ct = default)
        => (_update ?? _reply)(reply, ct);
}

/// <summary>
/// Somebody sent one of the bot's <see cref="DiscordForm"/>s, with a way to answer. The answer is
/// visible only to them.
/// </summary>
public sealed class DiscordFormSubmit
{
    private readonly Func<DiscordReply, CancellationToken, Task> _reply;

    public DiscordFormSubmit(
        string discordUserId,
        string discordUsername,
        string formId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> values,
        Func<DiscordReply, CancellationToken, Task> reply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(reply);

        DiscordUserId = discordUserId;
        DiscordUsername = discordUsername;
        FormId = formId;
        Values = values;
        _reply = reply;
    }

    public string DiscordUserId { get; }

    public string DiscordUsername { get; }

    /// <summary>The <see cref="DiscordForm.Id"/> of the form sent.</summary>
    public string FormId { get; }

    /// <summary>Each field's answer by its <see cref="DiscordFormField.Id"/>: one text, or the values picked.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Values { get; }

    /// <summary>A text field's answer, trimmed; empty when it was left blank or is not there.</summary>
    public string Text(string fieldId)
        => Values.TryGetValue(fieldId, out var value) && value.Count > 0 ? (value[0] ?? string.Empty).Trim() : string.Empty;

    /// <summary>A list field's picks; empty when none were, or when the field is not there at all.</summary>
    public IReadOnlyList<string> Picked(string fieldId)
        => Values.TryGetValue(fieldId, out var value) ? [.. value.Where(v => !string.IsNullOrWhiteSpace(v))] : [];

    public Task ReplyAsync(DiscordReply reply, CancellationToken ct = default) => _reply(reply, ct);
}

/// <summary>
/// Whether a channel post went through, and if not, whether trying again would help.
/// </summary>
/// <param name="Permanent">
/// True when the channel is gone or the bot may not post there: the same message will fail the
/// same way until an operator changes something, so the poster should stop and say so rather
/// than retry every few seconds.
/// </param>
/// <param name="MessageId">
/// Discord's id for the message that was sent, when it is known. Kept so a message can be found
/// again and rewritten -- an instance announcement is one message that keeps being brought up to
/// date, not a new message every minute. Null on a failure, and null for a send whose id nobody
/// asked for.
/// </param>
/// <param name="DirectMessagesClosed">
/// A direct message was refused because the person does not accept messages from the server's
/// members or has blocked the bot (Discord error 50007). Whoever sent it may try another way.
/// </param>
/// <param name="NotFound">
/// Discord answered that the message or channel is not there (404). Different from a refusal
/// (403), where the thing may well be there and the bot is only not allowed to touch it: a
/// deleted message is done with, a refused one is not.
/// </param>
/// <param name="Unclear">
/// Discord gave no clear answer to a send -- no answer at all, a timeout, a 5xx -- so the message
/// may be in the channel or may not (posts design §3.5). Only <see cref="IDiscordGateway.SendPostAsync"/>
/// says this; the caller looks in the channel rather than sending again.
/// </param>
public sealed record DiscordPostOutcome(
    bool Sent,
    string? Error,
    bool Permanent,
    string? MessageId = null,
    bool DirectMessagesClosed = false,
    bool NotFound = false,
    bool Unclear = false)
{
    public static DiscordPostOutcome Ok { get; } = new(true, null, false);

    public static DiscordPostOutcome Posted(string messageId) => new(true, null, false, messageId);

    public static DiscordPostOutcome Failed(string error, bool permanent = false, bool notFound = false) =>
        new(false, error, permanent, NotFound: notFound);

    /// <summary>No clear answer: the message may or may not be in the channel.</summary>
    public static DiscordPostOutcome NoClearAnswer(string error) => new(false, error, false, Unclear: true);
}

/// <summary>Why a gateway session ended, as the library reported it.</summary>
/// <param name="Fatal">
/// True when reconnecting with the same settings cannot work -- Discord rejected the token, or
/// refused the intents -- so the bot should stop and wait for the operator instead of retrying.
/// </param>
/// <param name="IntentsRefused">
/// Discord refused an intent the session asked for (close code 4014): a privileged intent that is
/// not turned on in the Developer Portal. The bot can connect again without it.
/// </param>
/// <param name="MissingIntents">
/// When Discord refused the intents, the privileged ones switched off in the Developer Portal, by
/// the portal's names. Empty for any other close, or when they could not be looked up.
/// </param>
public sealed record DiscordDisconnect(
    string Reason,
    bool Fatal,
    bool IntentsRefused = false,
    IReadOnlyList<string>? MissingIntents = null);

/// <summary>What one session asks Discord for, fixed when it is made.</summary>
/// <remarks>
/// Both privileged intents are asked for unless Discord has refused them: members are recorded as
/// facts and messages are stored in full (M5 spec §5), whatever else is switched on. A session
/// made after a refusal leaves the refused ones out, so everything else keeps working.
/// </remarks>
/// <param name="MemberEvents">Ask for the privileged Server Members intent: joins, leaves, role changes.</param>
/// <param name="MessageContent">Ask for the privileged Message Content intent: the text of messages.</param>
/// <param name="GuildId">
/// The one server this session answers in: the server id from settings. A command run anywhere
/// else, or in a direct message, is left alone without a word. Null answers nothing.
/// </param>
public sealed record DiscordGatewayOptions(bool MemberEvents = true, bool MessageContent = true, string? GuildId = null);

/// <summary>Somebody joined a server the bot is in.</summary>
/// <param name=Member>The member as they joined -- name, roles, join time -- for recording the join.</param>
public sealed record DiscordMemberJoin(string GuildId, string UserId, string Username, bool IsBot, DiscordMemberSnapshot? Member = null);

/// <summary>
/// Somebody put a reaction on a message, or took one off (giveaways design §4.2).
/// </summary>
/// <param name="Emoji">
/// The emoji as text, exactly as it is written in a message: the character itself for a standard
/// one, <c>&lt;:name:id&gt;</c> for one of the server's own. Compared as text and never parsed, so
/// an emoji Modbot has never seen costs nothing.
/// </param>
/// <param name="Username">
/// The name Discord had to hand, when it had one. Null when the reaction arrived for a person the
/// session does not know, which happens without the members intent.
/// </param>
public sealed record DiscordReactionSnapshot(
    string GuildId,
    string ChannelId,
    string MessageId,
    string UserId,
    string Emoji,
    string? Username = null,
    bool IsBot = false);

/// <summary>Whether giving or taking away a role went through.</summary>
/// <param name="NotInServer">Discord does not know that member in the server (Unknown Member).</param>
/// <param name="RoleGone">The role no longer exists (Unknown Role).</param>
public sealed record DiscordRoleOutcome(bool Done, bool NotInServer, bool RoleGone, string? Error)
{
    public static DiscordRoleOutcome Ok { get; } = new(true, false, false, null);

    public static DiscordRoleOutcome MemberNotInServer { get; } = new(false, true, false, "That member is not in the server.");

    public static DiscordRoleOutcome NoSuchRole { get; } = new(false, false, true, "That role does not exist any more.");

    public static DiscordRoleOutcome Failed(string error) => new(false, false, false, error);
}

/// <summary>What a checked removal did (<see cref="IDiscordGateway.RemoveCheckedAsync"/>).</summary>
/// <param name="Kept">The check said no, so nobody was removed. <paramref name="Member"/> is who was read.</param>
/// <param name="Member">The member as Discord had them, when they were read.</param>
public sealed record DiscordCheckedRemoval(DiscordModerationOutcome Outcome, bool Kept = false, DiscordMemberSnapshot? Member = null);

/// <summary>
/// Whether banning, unbanning or removing somebody went through.
/// </summary>
/// <remarks>
/// Its own type rather than <see cref="DiscordPostOutcome"/> because the two ways these fail are
/// different decisions for the caller. <see cref="NotAllowed"/> is a setup problem an operator has
/// to fix and no amount of trying again will help; <see cref="NothingToDo"/> is not a failure at
/// all -- Discord says the person is already banned, or was never banned -- and a sync that treated
/// it as one would report a problem every pass forever.
/// </remarks>
/// <param name="NotAllowed">The bot lacks the permission in that server, or the person outranks it.</param>
/// <param name="NotInServer">Discord does not know that person in the server.</param>
/// <param name="NothingToDo">Discord says it is already so: already banned, or not banned at all.</param>
public sealed record DiscordModerationOutcome(
    bool Done, string? Error, bool NotAllowed = false, bool NotInServer = false, bool NothingToDo = false)
{
    public static DiscordModerationOutcome Ok { get; } = new(true, null);

    /// <summary>Discord had nothing to change. Counts as done, so nothing retries it.</summary>
    public static DiscordModerationOutcome Already { get; } = new(true, null, NothingToDo: true);

    public static DiscordModerationOutcome MemberNotInServer { get; } = new(false, "That person is not in the server.", NotInServer: true);

    public static DiscordModerationOutcome Refused(string error) => new(false, error, NotAllowed: true);

    public static DiscordModerationOutcome Failed(string error) => new(false, error);
}

/// <summary>What the bot may do in one channel, after the category's and the channel's overwrites.</summary>
public sealed record DiscordChannelPermissions(
    bool ViewChannel,
    bool ReadMessageHistory,
    bool SendMessages,
    bool EmbedLinks,
    bool AttachFiles,
    bool ManageMessages)
{
    public static DiscordChannelPermissions None { get; } = new(false, false, false, false, false, false);
}

/// <summary>One channel as the bot sees it right now.</summary>
/// <param name="Type">One of <c>DiscordChannelTypes</c>.</param>
/// <param name="CategoryId">The category it sits under, or null.</param>
/// <param name="EveryoneCanView">Whether @everyone may see it; false for a staff-only channel. Null when not known.</param>
public sealed record DiscordChannelSnapshot(
    string Id,
    string Name,
    string Type,
    string? CategoryId,
    int Position,
    bool Nsfw,
    DiscordChannelPermissions BotPermissions,
    bool? EveryoneCanView = null);

/// <summary>One role as the bot sees it right now.</summary>
/// <param name="Color">0xRRGGBB, zero for none.</param>
/// <param name="BotCanAssign">Manage Roles held, role below the bot's highest, not managed, not @everyone.</param>
/// <param name="Permissions">The role's server-wide permission bits, as Discord sends them. Null when not known.</param>
/// <param name="Mentionable">"Allow anyone to @mention this role" is on.</param>
public sealed record DiscordRoleSnapshot(
    string Id,
    string Name,
    int Color,
    int Position,
    bool Managed,
    bool Everyone,
    bool BotCanAssign,
    long? Permissions = null,
    bool Mentionable = false);

/// <summary>Every channel and role in one server, and the bot's server-wide permissions.</summary>
/// <param name="BotCanManageEvents">Manage Events, which the calendar's Discord events need (calendar design §3.2).</param>
/// <param name="BotCanBanMembers">Ban Members, which copying a VRChat ban into Discord needs (Discord sync design §6).</param>
/// <param name="BotCanRemoveMembers">Kick Members, which removing somebody from the server needs.</param>
/// <param name="IconUrl">The server's icon, or null for none.</param>
/// <param name="BannerUrl">The server's banner, or null for none.</param>
/// <param name="BoostCount">How many boosts the server has, or null when not known.</param>
/// <param name="BoostLevel">The boost level Discord gives the server, 0 to 3, or null when not known.</param>
/// <param name="BotCanMentionEveryone">
/// Mention @everyone, @here and All Roles, which lets the bot ping a role that is not open to
/// mentions (calendar design §3.3.1). The bot itself never pings @everyone or @here.
/// </param>
public sealed record DiscordServerSnapshot(
    string GuildId,
    string Name,
    bool BotCanViewAuditLog,
    bool BotCanManageRoles,
    IReadOnlyList<DiscordChannelSnapshot> Channels,
    IReadOnlyList<DiscordRoleSnapshot> Roles,
    bool BotCanManageEvents = false,
    bool BotCanBanMembers = false,
    bool BotCanRemoveMembers = false,
    string? IconUrl = null,
    string? BannerUrl = null,
    int? BoostCount = null,
    int? BoostLevel = null,
    bool BotCanMentionEveryone = false);

/// <summary>
/// A server event as the calendar describes it: an external event whose location is a line of text.
/// </summary>
/// <param name="Name">Up to 100 characters.</param>
/// <param name="Description">Up to 1000 characters, or null.</param>
/// <param name="Location">The join link once the instance is open, the world's name before. Up to 100 characters.</param>
/// <param name="CoverImageUrl">An https picture link for the cover, or null for none.</param>
/// <param name="CoverBytes">
/// The cover itself, in place of <paramref name="CoverImageUrl"/>: the picture cropped for Discord in
/// the event form (calendar design §15.4). A PNG, JPEG, GIF or WebP.
/// </param>
public sealed record DiscordScheduledEventDetails(
    string Name,
    string? Description,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Location,
    string? CoverImageUrl,
    byte[]? CoverBytes = null)
{
    /// <summary>The error an update comes back with when the event was deleted or ended in Discord.</summary>
    public const string Gone = "That server event is gone, or has already ended.";
}

/// <summary>
/// One gateway session: connect with a token, register the guild's commands, answer them, post
/// to a channel. The bot's hosted service drives it; the tests drive a fake.
/// </summary>
public interface IDiscordGateway : IAsyncDisposable
{
    DiscordGatewayState State { get; }

    /// <summary>
    /// The bot's own Discord account id, once it has signed in; null before that.
    /// </summary>
    /// <remarks>
    /// Read so that a ban Modbot performed can be told apart from one a person performed: Discord's
    /// audit log names the bot as the actor on its own bans, and only a person's ban is worth
    /// copying anywhere (Discord sync design §4).
    /// </remarks>
    string? BotUserId { get; }

    /// <summary>
    /// The Discord account id of the owner of the server, as the session last saw it; null when the
    /// bot is not in that server or the id is not a number.
    /// </summary>
    /// <remarks>Read so a person acting through the API cannot ban or remove the owner, whom Discord would refuse anyway.</remarks>
    string? GuildOwnerId(string guildId);

    /// <summary>The session is signed in and the guild list has arrived. May fire again after a reconnect.</summary>
    event Func<Task>? Ready;

    /// <summary>
    /// A dropped session came back without signing in again. Usable again, and no Ready follows:
    /// Discord.Net raises Ready only for a fresh sign-in, never for a resume.
    /// </summary>
    event Func<Task>? Resumed;

    event Func<DiscordDisconnect, Task>? Disconnected;

    /// <summary>
    /// Somebody ran one of the bot's slash commands or right-click menus in the session's own server
    /// (<see cref="DiscordGatewayOptions.GuildId"/>). Commands from anywhere else are never raised.
    /// A slash command arrives already acknowledged; a right-click menu does not, so it can be
    /// answered with a form (acting from Discord design §11).
    /// </summary>
    event Func<DiscordCommandCall, Task>? CommandReceived;

    /// <summary>
    /// Somebody pressed a button the bot put under one of its replies. Only presses in the
    /// session's own server (<see cref="DiscordGatewayOptions.GuildId"/>) whose id starts with
    /// <see cref="DiscordActionButton.Prefix"/> are raised.
    /// </summary>
    event Func<DiscordButtonPress, Task>? ButtonPressed;

    /// <summary>
    /// Somebody sent a form the bot showed. Only forms sent in the session's own server whose id
    /// starts with <see cref="DiscordActionButton.Prefix"/> are raised, as for a button.
    /// </summary>
    event Func<DiscordFormSubmit, Task>? FormSubmitted;

    /// <summary>
    /// A channel was created or changed -- renamed, moved, or its permission overwrites edited.
    /// Carries the server's id and the channel as it is now.
    /// </summary>
    event Func<string, DiscordChannelSnapshot, Task>? ChannelChanged;

    /// <summary>A channel was deleted. Carries the server's id and the channel's id.</summary>
    event Func<string, string, Task>? ChannelRemoved;

    /// <summary>
    /// Something changed that can move the bot's permissions or the role list across the whole
    /// server: a role created, changed or deleted, the server itself changed, or the bot's own
    /// roles changed. Carries the server's id; <see cref="ReadServer"/> gives the new picture.
    /// </summary>
    event Func<string, Task>? ServerChanged;

    /// <summary>
    /// Somebody joined a server the bot is in. Only raised by a session made with
    /// <see cref="DiscordGatewayOptions.MemberEvents"/>.
    /// </summary>
    event Func<DiscordMemberJoin, Task>? MemberJoined;

    /// <summary>
    /// Every channel and role in the server as the session holds them, or null when the bot is
    /// not in that server. Read from the session's memory, so it costs no request to Discord.
    /// </summary>
    DiscordServerSnapshot? ReadServer(string guildId);

    /// <summary>Signs in and starts the session. Returns once the connection is under way; <see cref="Ready"/> says when it is usable.</summary>
    Task ConnectAsync(string token, CancellationToken ct);

    /// <summary>Replaces the guild's slash commands with these. Returns how many Discord accepted.</summary>
    Task<int> RegisterGuildCommandsAsync(string guildId, IReadOnlyList<DiscordCommandDefinition> commands, CancellationToken ct);

    Task<DiscordPostOutcome> PostAsync(string channelId, IReadOnlyList<DiscordEmbedContent> embeds, CancellationToken ct);

    /// <summary>
    /// Posts a message and says what its id is, so it can be rewritten later.
    /// </summary>
    /// <param name="text">
    /// A line above the embed, or null for none. This is the operator's own words, so it is sent
    /// with mentions disabled: a message written months ago must not be able to ping a channel
    /// every time an instance opens.
    /// </param>
    /// <param name="links">Buttons under the message that open a web address, or null for none.</param>
    Task<DiscordPostOutcome> PostAsync(
        string channelId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        CancellationToken ct);

    /// <summary>
    /// Posts a message with pictures the embeds point at by <c>attachment://name</c>.
    /// </summary>
    /// <param name="pictures">
    /// The files to send, at most <see cref="DiscordPicture.PerMessage"/>. Null or empty sends
    /// none, which is the same as the overload without them.
    /// </param>
    Task<DiscordPostOutcome> PostAsync(
        string channelId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordPicture>? pictures,
        CancellationToken ct);

    /// <summary>
    /// Posts a message whose text is a mention of one role, and pings that role and nobody else
    /// (calendar design §3.3.1). The calendar's channel post, the first time it goes up for a date.
    /// </summary>
    /// <remarks>
    /// Discord's allowed mentions name the one role, so nothing else in the message can ping. The
    /// server's @everyone role (whose id is the server's) is never pinged: asked for it, the message
    /// goes out with mentions off. Whether the role is pinged is still Discord's to decide: a role
    /// not open to mentions pings only when the bot holds Mention @everyone, @here and All Roles.
    /// </remarks>
    Task<DiscordPostOutcome> PostMentioningRoleAsync(
        string channelId,
        string roleId,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordPicture>? pictures,
        CancellationToken ct);

    /// <summary>
    /// Posts a message with pictures and with buttons the bot answers when pressed, after the links.
    /// </summary>
    /// <param name="actions">
    /// Buttons whose presses arrive as <see cref="ButtonPressed"/>. Null or empty sends none, which
    /// is the same as the overload without them.
    /// </param>
    Task<DiscordPostOutcome> PostAsync(
        string channelId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordPicture>? pictures,
        IReadOnlyList<DiscordActionButton>? actions,
        CancellationToken ct);

    /// <summary>
    /// Adds a line to a card the bot posted saying who dealt with it, and takes away the card's
    /// buttons whose ids start with <paramref name="removeButtonsStarting"/> (acting from Discord
    /// design §6). The card, its picture and its other buttons stay as they are.
    /// </summary>
    /// <param name="line">
    /// The line, for example "Banned by alice". Added to the message's own text, above the card, so
    /// the card itself is sent back untouched.
    /// </param>
    Task<DiscordPostOutcome> MarkHandledAsync(
        string channelId, string messageId, string line, string removeButtonsStarting, CancellationToken ct);

    /// <summary>
    /// Rewrites a message the bot posted earlier.
    /// </summary>
    /// <remarks>
    /// A message somebody deleted comes back as a permanent failure, which is the caller's signal
    /// to forget the id rather than to keep trying. An edit never pings anybody, whatever its text.
    /// </remarks>
    /// <param name="links">
    /// The buttons the message should have after the edit. Null or empty removes any it had.
    /// </param>
    Task<DiscordPostOutcome> EditAsync(
        string channelId,
        string messageId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        CancellationToken ct);

    /// <summary>
    /// Rewrites a message the bot posted earlier, and says what its pictures should now be.
    /// </summary>
    /// <param name="pictures">
    /// Null keeps the files the message already has, which is how a card rewritten every minute
    /// pays for its picture once: the embed keeps pointing at <c>attachment://name</c> and nothing
    /// is uploaded again. A list -- empty included -- replaces them, so a card that has lost its
    /// picture loses the file too.
    /// </param>
    Task<DiscordPostOutcome> EditAsync(
        string channelId,
        string messageId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordPicture>? pictures,
        CancellationToken ct);

    // ── Posts (posts design §3.5) ────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a post: one plain message of text, with pictures as files, that may ping one role.
    /// <strong>Never sent twice by the library:</strong> the request goes with Discord.Net's
    /// <c>RetryMode.AlwaysFail</c>, so a timeout or a 502 comes back as
    /// <see cref="DiscordPostOutcome.Unclear"/> instead of being sent again behind Modbot's back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A clear refusal (a 4xx other than 429) comes back <see cref="DiscordPostOutcome.Permanent"/>
    /// with Discord's words. A rate limit, or a channel that could not be looked up, comes back not
    /// sent and not permanent: nothing was made, and it may be sent on a later pass.
    /// </para>
    /// <para>
    /// Mentions are off except <paramref name="roleId"/>, which is pinged and nothing else is, and
    /// never @everyone: asked for the server's own id, nobody is pinged.
    /// </para>
    /// </remarks>
    Task<DiscordPostOutcome> SendPostAsync(
        string channelId, string text, string? roleId, IReadOnlyList<DiscordPicture>? pictures, CancellationToken ct);

    /// <summary>
    /// Rewrites the text of a post the bot sent, keeping its files. Pings nobody. A message that is
    /// gone comes back permanent with <see cref="DiscordPostOutcome.NotFound"/>.
    /// </summary>
    Task<DiscordPostOutcome> EditPostAsync(string channelId, string messageId, string text, CancellationToken ct);

    /// <summary>
    /// Publishes a message in an Announcement channel to the servers that follow it (crosspost).
    /// Needs Send Messages for the bot's own message. Discord refuses it in any other channel.
    /// </summary>
    Task<DiscordPostOutcome> PublishAsync(string channelId, string messageId, CancellationToken ct);

    /// <summary>
    /// Reads up to <paramref name="limit"/> messages after <paramref name="afterMessageId"/> (the newest
    /// when null), for the look after an unclear send. One request; needs Read Message History.
    /// </summary>
    Task<DiscordMessagePage> ReadRecentAsync(string channelId, string? afterMessageId, int limit, CancellationToken ct);

    /// <summary>
    /// Deletes somebody's message. For AI moderation rules set to act (M8 §2).
    /// </summary>
    /// <remarks>
    /// Needs Manage Messages in the channel, and no gateway intent: a message is deleted by id.
    /// </remarks>
    /// <param name="reason">Written to the server's audit log.</param>
    Task<DiscordPostOutcome> DeleteMessageAsync(string channelId, string messageId, string reason, CancellationToken ct);

    /// <summary>
    /// Times a member out. For AI moderation rules set to act (M8 §2).
    /// </summary>
    /// <remarks>
    /// Needs Moderate Members, and no gateway intent: the member is looked up by id over REST.
    /// </remarks>
    Task<DiscordPostOutcome> TimeOutAsync(string guildId, string userId, TimeSpan duration, string reason, CancellationToken ct);

    /// <summary>
    /// Sends one person a direct message. A refusal because they do not accept DMs comes back with
    /// <see cref="DiscordPostOutcome.DirectMessagesClosed"/> set.
    /// </summary>
    Task<DiscordPostOutcome> SendDirectMessageAsync(
        string userId, string text, IReadOnlyList<DiscordLinkButton>? links, CancellationToken ct);

    /// <summary>
    /// Posts a line in a channel that mentions one person, and pings nobody else whatever the text
    /// says.
    /// </summary>
    Task<DiscordPostOutcome> MentionAsync(
        string channelId, string userId, string text, IReadOnlyList<DiscordLinkButton>? links, CancellationToken ct);

    /// <summary>
    /// A direct message with buttons the bot answers when pressed as well as link buttons (join
    /// gate design §4). Refusals as <see cref="SendDirectMessageAsync(string, string, IReadOnlyList{DiscordLinkButton}?, CancellationToken)"/>.
    /// </summary>
    Task<DiscordPostOutcome> SendDirectMessageAsync(
        string userId,
        string text,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordActionButton>? actions,
        CancellationToken ct);

    /// <summary>A mention of one person, with buttons the bot answers when pressed. Pings nobody else.</summary>
    Task<DiscordPostOutcome> MentionAsync(
        string channelId,
        string userId,
        string text,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordActionButton>? actions,
        CancellationToken ct);

    /// <summary>
    /// Posts a message with buttons the bot answers when pressed: the join gate's message, and an
    /// alert with Hold new joiners on it. Mentions are off, whatever the text says.
    /// </summary>
    Task<DiscordPostOutcome> PostWithActionsAsync(
        string channelId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordActionButton>? actions,
        CancellationToken ct);

    /// <summary>
    /// Rewrites a message the bot posted with <see cref="PostWithActionsAsync"/>. A message that is
    /// gone comes back as a permanent failure with <see cref="DiscordPostOutcome.NotFound"/>.
    /// </summary>
    Task<DiscordPostOutcome> EditWithActionsAsync(
        string channelId,
        string messageId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordActionButton>? actions,
        CancellationToken ct);

    /// <summary>
    /// Pauses the server's invites until <paramref name="until"/>, at most 24 hours ahead, through
    /// Discord's incident actions (join gate design §8). Needs Manage Server. A DM pause already set
    /// is kept.
    /// </summary>
    Task<DiscordPostOutcome> PauseInvitesAsync(string guildId, DateTimeOffset until, CancellationToken ct);

    // ── Server events (calendar design §3.2) ─────────────────────────────────────────────
    //
    // All three need Manage Events. The outcome's MessageId carries Discord's event id.

    /// <summary>Creates an external server event.</summary>
    Task<DiscordPostOutcome> CreateEventAsync(string guildId, DiscordScheduledEventDetails details, CancellationToken ct);

    /// <summary>
    /// Changes a server event the bot created, and starts it when <paramref name="start"/> is set
    /// and it has not started. A deleted event comes back as a permanent failure.
    /// </summary>
    Task<DiscordPostOutcome> UpdateEventAsync(
        string guildId, string eventId, DiscordScheduledEventDetails details, bool start, CancellationToken ct);

    /// <summary>
    /// Ends a server event: completed when it had started, cancelled when it had not. An event that
    /// is already gone counts as ended.
    /// </summary>
    Task<DiscordPostOutcome> EndEventAsync(string guildId, string eventId, CancellationToken ct);

    /// <summary>
    /// Every server event that has not ended, whoever made it (calendar design §16). One request.
    /// A person who made one is never named. Null when the bot is not in the server or Discord did
    /// not answer.
    /// </summary>
    Task<IReadOnlyList<Core.Discord.DiscordServerEvent>?> ReadServerEventsAsync(string guildId, CancellationToken ct);

    // ── Bans and removals (M5 spec §4; Discord sync design §4) ───────────────────────────
    //
    // All three take ids and go over REST, so the member does not have to be in the session's
    // cache and no gateway intent is needed. The reason is written to the server's audit log,
    // which is also how the bot's own actions are recognised when they come back as events.

    /// <summary>
    /// Bans somebody from the server. Needs Ban Members, and the bot's highest role above theirs.
    /// </summary>
    /// <param name="deleteMessageDays">
    /// How many days of their messages Discord should delete as well, 0 to 7. Zero keeps them,
    /// which is what a copied ban does: Modbot stores those messages and a moderator still needs
    /// to be able to read what the person said.
    /// </param>
    Task<DiscordModerationOutcome> BanAsync(
        string guildId, string userId, string reason, int deleteMessageDays, CancellationToken ct);

    /// <summary>Lifts a ban. Somebody who is not banned counts as nothing to do.</summary>
    Task<DiscordModerationOutcome> UnbanAsync(string guildId, string userId, string reason, CancellationToken ct);

    /// <summary>
    /// Everybody the server has banned, with the reason given. Null when the bot may not read the
    /// ban list.
    /// </summary>
    /// <remarks>
    /// Read when somebody asks what the first run of ban sync would do (M5 §7), and for the Bans
    /// page's Discord list on sign-in and once a day; bans arrive as events between. Needs Ban
    /// Members, and pages a thousand at a time.
    /// </remarks>
    Task<IReadOnlyList<DiscordBanSnapshot>?> ReadBansAsync(string guildId, CancellationToken ct);

    /// <summary>
    /// Removes somebody from the server without banning them. Needs Kick Members. Somebody who is
    /// not in the server counts as nothing to do: they are already out.
    /// </summary>
    Task<DiscordModerationOutcome> RemoveAsync(string guildId, string userId, string reason, CancellationToken ct);

    /// <summary>
    /// Removes somebody only when <paramref name="mayRemove"/>, shown the member as Discord has them
    /// at this moment, says so (join gate design §6). The member is read live over REST, never from
    /// the stored list, so a role given a second ago or a member who just left is seen.
    /// </summary>
    /// <returns>
    /// Removed; not in the server (<see cref="DiscordModerationOutcome.NothingToDo"/>); kept, with
    /// the member who was read; or a failure, with <see cref="DiscordModerationOutcome.NotAllowed"/>
    /// when Discord refused. A member who could not be read is never removed.
    /// </returns>
    Task<DiscordCheckedRemoval> RemoveCheckedAsync(
        string guildId, string userId, string reason, Func<DiscordMemberSnapshot, bool> mayRemove, CancellationToken ct);

    /// <summary>Gives a member a role. Needs Manage Roles and the role below the bot's highest.</summary>
    Task<DiscordRoleOutcome> AddRoleAsync(string guildId, string userId, string roleId, CancellationToken ct);

    /// <summary>Takes a role away from a member.</summary>
    Task<DiscordRoleOutcome> RemoveRoleAsync(string guildId, string userId, string roleId, CancellationToken ct);

    /// <summary>
    /// Gives or takes away a role, saying why in the server's audit log.
    /// </summary>
    /// <remarks>
    /// Role sync writes its own reason so that the entry Discord records -- and which Modbot then
    /// reads back as a fact -- says the change came from a sync rather than from a person.
    /// </remarks>
    Task<DiscordRoleOutcome> ChangeRoleAsync(
        string guildId, string userId, string roleId, bool add, string reason, CancellationToken ct);

    // ── Messages (M5 spec §5.1) ──────────────────────────────────────────────────────────
    //
    // Only messages in a server, never direct messages, and only messages people post: Discord's
    // own system lines ("X pinned a message") are left out.

    /// <summary>A message was posted.</summary>
    event Func<DiscordMessageSnapshot, Task>? MessageReceived;

    /// <summary>
    /// A message changed. <see cref="DiscordMessageSnapshot.EditedAt"/> is set when its text was
    /// edited, and null when Discord only filled in something else, such as a link preview.
    /// </summary>
    event Func<DiscordMessageSnapshot, Task>? MessageEdited;

    /// <summary>
    /// One or more messages were deleted. Carries the server's id, the channel or thread, and the
    /// message ids -- all a delete event says.
    /// </summary>
    event Func<string, string, IReadOnlyList<string>, Task>? MessagesDeleted;

    // ── Reactions (giveaways design §4.2) ────────────────────────────────────────────────
    //
    // Needs the Guild Message Reactions intent, which is not privileged. Both events carry the
    // message rather than its text: a reaction is matched against the message id Modbot posted,
    // and nothing here has to read what the message says.

    /// <summary>Somebody put a reaction on a message in the server.</summary>
    event Func<DiscordReactionSnapshot, Task>? ReactionAdded;

    /// <summary>Somebody took a reaction off a message in the server.</summary>
    event Func<DiscordReactionSnapshot, Task>? ReactionRemoved;

    /// <summary>
    /// Puts a reaction on a message the bot posted, so people have something to click.
    /// </summary>
    /// <remarks>Needs Add Reactions in the channel, and Read Message History to find the message.</remarks>
    Task<DiscordPostOutcome> AddReactionAsync(string channelId, string messageId, string emoji, CancellationToken ct);

    /// <summary>
    /// Reads one page of a channel's or thread's history: the hundred messages before
    /// <paramref name="beforeId"/>, after <paramref name="afterId"/>, or the newest hundred when both
    /// are null. One request to Discord; the library waits out Discord's rate limits on its own.
    /// </summary>
    Task<DiscordMessagePage> ReadMessagesAsync(string channelId, string? beforeId, string? afterId, CancellationToken ct);

    /// <summary>
    /// Threads in the server: the open ones the session holds in <paramref name="channelIds"/>, and
    /// the archived public ones in <paramref name="archivedIn"/>, which costs requests.
    /// </summary>
    Task<IReadOnlyList<DiscordThreadSnapshot>> ReadThreadsAsync(
        string guildId, IReadOnlyList<string> channelIds, IReadOnlyList<string> archivedIn, CancellationToken ct);

    // ── Members, voice and moderation (M5 spec §5) ───────────────────────────────────────
    //
    // Each event carries the server's id first; a join is MemberJoined above. Member events need
    // the Server Members intent.

    /// <summary>Somebody left, was kicked or was banned. Carries the user's id.</summary>
    event Func<string, string, Task>? MemberLeft;

    /// <summary>A member's nickname, roles or timeout changed. Carries the member as they are now.</summary>
    event Func<string, DiscordMemberSnapshot, Task>? MemberUpdated;

    event Func<string, string, Task>? MemberBanned;

    event Func<string, string, Task>? MemberUnbanned;

    /// <summary>
    /// Somebody joined, left or moved between voice channels. Carries the user's id, the channel they
    /// were in and the channel they are in now; either is null for none.
    /// </summary>
    event Func<string, string, string?, string?, Task>? VoiceChanged;

    /// <summary>
    /// A new audit log entry was written. Carries only the server's id: the entry is read with
    /// <see cref="ReadAuditLogAsync"/>, the same way a catch-up reads it, so there is one path.
    /// </summary>
    event Func<string, Task>? AuditLogChanged;

    /// <summary>
    /// The audit log entries after <paramref name="afterId"/>, oldest first, up to a thousand; or,
    /// with no id, as far back as Discord keeps -- forty-five days. One request per hundred.
    /// </summary>
    Task<DiscordAuditPage> ReadAuditLogAsync(string guildId, string? afterId, CancellationToken ct);

    /// <summary>
    /// Every member of the server, asked for over the gateway. Null when the bot is not in the
    /// server or the list could not be had.
    /// </summary>
    Task<IReadOnlyList<DiscordMemberSnapshot>?> ReadMembersAsync(string guildId, CancellationToken ct);

    /// <summary>Who is in a voice channel right now, from the session's memory. Empty when the server is not known.</summary>
    IReadOnlyList<DiscordVoiceState> ReadVoice(string guildId);

    /// <summary>How many members the server has, as the session last heard. Null when not known.</summary>
    int? ReadMemberCount(string guildId);

    /// <summary>
    /// How many of the server's members Discord counts as online: one REST request for the server
    /// with its counts. Null when the bot is not in the server or Discord did not answer.
    /// </summary>
    Task<int?> ReadOnlineCountAsync(string guildId, CancellationToken ct);

    Task DisconnectAsync();
}

/// <summary>A fresh session per connection: settings changed, or the last one gave up.</summary>
public interface IDiscordGatewayFactory
{
    IDiscordGateway Create(DiscordGatewayOptions options);
}

