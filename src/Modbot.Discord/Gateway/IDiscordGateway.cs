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

/// <summary>One slash command, as registered on the guild.</summary>
public sealed record DiscordCommandDefinition(
    string Name,
    string Description,
    IReadOnlyList<DiscordCommandOption> Options);

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

/// <summary>What the bot says back to a command. Always visible only to the person who asked.</summary>
/// <param name="Links">Buttons under the reply that open a web address, or null for none.</param>
/// <param name="Pictures">Files the reply's cards point at by <c>attachment://name</c>.</param>
public sealed record DiscordReply(
    string? Text,
    IReadOnlyList<DiscordEmbedContent> Embeds,
    IReadOnlyList<DiscordLinkButton>? Links = null,
    IReadOnlyList<DiscordPicture>? Pictures = null)
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

    public DiscordCommandCall(
        string discordUserId,
        string discordUsername,
        string commandName,
        IReadOnlyDictionary<string, string> options,
        Func<DiscordReply, CancellationToken, Task> reply)
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
    }

    public string DiscordUserId { get; }

    public string DiscordUsername { get; }

    public string CommandName { get; }

    public IReadOnlyDictionary<string, string> Options { get; }

    public string? Option(string name)
        => Options.TryGetValue(name, out var value) ? value : null;

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
public sealed record DiscordPostOutcome(
    bool Sent,
    string? Error,
    bool Permanent,
    string? MessageId = null,
    bool DirectMessagesClosed = false)
{
    public static DiscordPostOutcome Ok { get; } = new(true, null, false);

    public static DiscordPostOutcome Posted(string messageId) => new(true, null, false, messageId);

    public static DiscordPostOutcome Failed(string error, bool permanent = false) => new(false, error, permanent);
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
public sealed record DiscordGatewayOptions(bool MemberEvents = true, bool MessageContent = true);

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
public sealed record DiscordChannelSnapshot(
    string Id,
    string Name,
    string Type,
    string? CategoryId,
    int Position,
    bool Nsfw,
    DiscordChannelPermissions BotPermissions);

/// <summary>One role as the bot sees it right now.</summary>
/// <param name="Color">0xRRGGBB, zero for none.</param>
/// <param name="BotCanAssign">Manage Roles held, role below the bot's highest, not managed, not @everyone.</param>
public sealed record DiscordRoleSnapshot(
    string Id,
    string Name,
    int Color,
    int Position,
    bool Managed,
    bool Everyone,
    bool BotCanAssign);

/// <summary>Every channel and role in one server, and the bot's server-wide permissions.</summary>
/// <param name="BotCanManageEvents">Manage Events, which the calendar's Discord events need (calendar design §3.2).</param>
public sealed record DiscordServerSnapshot(
    string GuildId,
    string Name,
    bool BotCanViewAuditLog,
    bool BotCanManageRoles,
    IReadOnlyList<DiscordChannelSnapshot> Channels,
    IReadOnlyList<DiscordRoleSnapshot> Roles,
    bool BotCanManageEvents = false);

/// <summary>
/// A server event as the calendar describes it: an external event whose location is a line of text.
/// </summary>
/// <param name="Name">Up to 100 characters.</param>
/// <param name="Description">Up to 1000 characters, or null.</param>
/// <param name="Location">The join link once the instance is open, the world's name before. Up to 100 characters.</param>
/// <param name="CoverImageUrl">An https picture link for the cover, or null for none.</param>
public sealed record DiscordScheduledEventDetails(
    string Name,
    string? Description,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Location,
    string? CoverImageUrl)
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

    /// <summary>The session is signed in and the guild list has arrived. May fire again after a reconnect.</summary>
    event Func<Task>? Ready;

    /// <summary>
    /// A dropped session came back without signing in again. Usable again, and no Ready follows:
    /// Discord.Net raises Ready only for a fresh sign-in, never for a resume.
    /// </summary>
    event Func<Task>? Resumed;

    event Func<DiscordDisconnect, Task>? Disconnected;

    event Func<DiscordCommandCall, Task>? CommandReceived;

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
    /// Rewrites a message the bot posted earlier.
    /// </summary>
    /// <remarks>
    /// A message somebody deleted comes back as a permanent failure, which is the caller's signal
    /// to forget the id rather than to keep trying.
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

    /// <summary>Gives a member a role. Needs Manage Roles and the role below the bot's highest.</summary>
    Task<DiscordRoleOutcome> AddRoleAsync(string guildId, string userId, string roleId, CancellationToken ct);

    /// <summary>Takes a role away from a member.</summary>
    Task<DiscordRoleOutcome> RemoveRoleAsync(string guildId, string userId, string roleId, CancellationToken ct);

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

    Task DisconnectAsync();
}

/// <summary>A fresh session per connection: settings changed, or the last one gave up.</summary>
public interface IDiscordGatewayFactory
{
    IDiscordGateway Create(DiscordGatewayOptions options);
}

