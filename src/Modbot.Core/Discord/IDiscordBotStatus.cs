namespace Modbot.Core.Discord;

/// <summary>Where the bot is in its life: the one word the Health page leads with.</summary>
/// <remarks>
/// <strong>Written as its name, not its number.</strong> Without the converter below
/// System.Text.Json sends <c>1</c> where the Health page expects <c>"NotConfigured"</c>, its
/// lookup misses, and reading a field off the miss throws during render -- which takes out the
/// whole screen rather than one card. <c>GateStatus</c> beside it on the same payload carries the
/// same attribute for the same reason.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter<DiscordBotState>))]
public enum DiscordBotState
{
    /// <summary>No token or no guild id stored. Not a fault (foundation §9).</summary>
    NotConfigured = 1,

    /// <summary>Signing in to the gateway, or waiting out a backoff before trying again.</summary>
    Connecting = 2,

    /// <summary>Signed in, commands registered, answering.</summary>
    Connected = 3,

    /// <summary>Was connected and lost the gateway. The library reconnects on its own.</summary>
    Disconnected = 4,

    /// <summary>Could not sign in. <c>LastError</c> says why; the bot retries with backoff.</summary>
    Failed = 5,
}

/// <summary>
/// What the bot would tell an operator about itself, read from the Health page.
/// </summary>
/// <param name="ConnectedSince">When the current gateway session became ready. Null unless connected.</param>
/// <param name="LastError">The most recent thing that went wrong, as a sentence. Never the token.</param>
/// <param name="CommandsRegistered">How many slash commands the bot registered on the guild this session.</param>
/// <param name="LogChannelConfigured">Whether a moderation log channel is set.</param>
/// <param name="LastPostedAt">When the bot last posted to that channel, in this process.</param>
/// <param name="PostedInThisProcess">Events posted since this process started.</param>
/// <param name="MissingIntents">
/// The privileged intents Discord refused because they are off in the Developer Portal, by the
/// portal's own names ("Message Content Intent", "Server Members Intent"). Empty unless the last
/// sign-in was refused for that reason; the bot then waits for the settings to change rather than
/// signing in again and again.
/// </param>
public sealed record DiscordBotSnapshot(
    DiscordBotState State,
    DateTimeOffset? ConnectedSince,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    int CommandsRegistered,
    bool LogChannelConfigured,
    DateTimeOffset? LastPostedAt,
    int PostedInThisProcess,
    IReadOnlyList<string>? MissingIntents = null);

/// <summary>
/// Read side of the bot's status. Declared here rather than in <c>Modbot.Discord</c> so the API
/// can report it without a reference to the bot -- the host wires both; the API only reads.
/// </summary>
public interface IDiscordBotStatus
{
    DiscordBotSnapshot Snapshot();
}
