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
/// A rich message card, described without the library's types so the formatting can be tested
/// and the library swapped. Sizes are Discord's: title 256, description 4096, field value 1024.
/// </summary>
/// <param name="ImageUrl">A large picture across the bottom of the card. Only an https address is used.</param>
/// <param name="ThumbnailUrl">A small picture in the top corner. Only an https address is used.</param>
public sealed record DiscordEmbedContent(
    string Title,
    string? Description,
    uint Color,
    IReadOnlyList<DiscordEmbedField> Fields,
    DateTimeOffset? Timestamp,
    string? Url,
    string? Footer,
    string? ImageUrl = null,
    string? ThumbnailUrl = null);

/// <summary>A button under a message that opens a web address. Only an https address is used.</summary>
public sealed record DiscordLinkButton(string Label, string Url);

/// <summary>What the bot says back to a command. Always visible only to the person who asked.</summary>
public sealed record DiscordReply(string? Text, IReadOnlyList<DiscordEmbedContent> Embeds)
{
    public static DiscordReply Say(string text) => new(text, []);

    public static DiscordReply Card(DiscordEmbedContent embed) => new(null, [embed]);
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
public sealed record DiscordPostOutcome(bool Sent, string? Error, bool Permanent, string? MessageId = null)
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
public sealed record DiscordDisconnect(string Reason, bool Fatal);

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
public sealed record DiscordServerSnapshot(
    string GuildId,
    string Name,
    bool BotCanViewAuditLog,
    bool BotCanManageRoles,
    IReadOnlyList<DiscordChannelSnapshot> Channels,
    IReadOnlyList<DiscordRoleSnapshot> Roles);

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
    /// every time a room opens.
    /// </param>
    /// <param name="links">Buttons under the message that open a web address, or null for none.</param>
    Task<DiscordPostOutcome> PostAsync(
        string channelId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
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

    Task DisconnectAsync();
}

/// <summary>A fresh session per connection: settings changed, or the last one gave up.</summary>
public interface IDiscordGatewayFactory
{
    IDiscordGateway Create();
}
