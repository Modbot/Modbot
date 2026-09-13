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
public sealed record DiscordEmbedContent(
    string Title,
    string? Description,
    uint Color,
    IReadOnlyList<DiscordEmbedField> Fields,
    DateTimeOffset? Timestamp,
    string? Url,
    string? Footer);

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
public sealed record DiscordPostOutcome(bool Sent, string? Error, bool Permanent)
{
    public static DiscordPostOutcome Ok { get; } = new(true, null, false);

    public static DiscordPostOutcome Failed(string error, bool permanent = false) => new(false, error, permanent);
}

/// <summary>Why a gateway session ended, as the library reported it.</summary>
/// <param name="Fatal">
/// True when reconnecting with the same settings cannot work -- Discord rejected the token, or
/// refused the intents -- so the bot should stop and wait for the operator instead of retrying.
/// </param>
public sealed record DiscordDisconnect(string Reason, bool Fatal);

/// <summary>
/// One gateway session: connect with a token, register the guild's commands, answer them, post
/// to a channel. The bot's hosted service drives it; the tests drive a fake.
/// </summary>
public interface IDiscordGateway : IAsyncDisposable
{
    DiscordGatewayState State { get; }

    /// <summary>The session is signed in and the guild list has arrived. May fire again after a reconnect.</summary>
    event Func<Task>? Ready;

    event Func<DiscordDisconnect, Task>? Disconnected;

    event Func<DiscordCommandCall, Task>? CommandReceived;

    /// <summary>Signs in and starts the session. Returns once the connection is under way; <see cref="Ready"/> says when it is usable.</summary>
    Task ConnectAsync(string token, CancellationToken ct);

    /// <summary>Replaces the guild's slash commands with these. Returns how many Discord accepted.</summary>
    Task<int> RegisterGuildCommandsAsync(string guildId, IReadOnlyList<DiscordCommandDefinition> commands, CancellationToken ct);

    Task<DiscordPostOutcome> PostAsync(string channelId, IReadOnlyList<DiscordEmbedContent> embeds, CancellationToken ct);

    Task DisconnectAsync();
}

/// <summary>A fresh session per connection: settings changed, or the last one gave up.</summary>
public interface IDiscordGatewayFactory
{
    IDiscordGateway Create();
}
