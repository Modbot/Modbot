namespace Modbot.Core.Discord;

/// <summary>Who made a Discord server event, as far as Modbot may say (calendar design §16).</summary>
public static class DiscordEventMakers
{
    /// <summary>Modbot's own bot.</summary>
    public const string Modbot = "modbot";

    /// <summary>Another bot or app. Its name is given.</summary>
    public const string Bot = "bot";

    /// <summary>A person. Never named: the list is about tools, not members.</summary>
    public const string Person = "person";

    /// <summary>Discord did not say. It says nothing for events made before late 2021.</summary>
    public const string Unknown = "unknown";
}

/// <summary>One scheduled event in the Discord server that has not ended.</summary>
/// <param name="Id">Discord's id for the event. Opaque text.</param>
/// <param name="MadeBy">One of <see cref="DiscordEventMakers"/>.</param>
/// <param name="BotName">The bot's or app's name when <paramref name="MadeBy"/> is a bot or Modbot; null for a person.</param>
/// <param name="Started">Discord shows it as happening now.</param>
public sealed record DiscordServerEvent(
    string Id,
    string Name,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    bool Started,
    string MadeBy,
    string? BotName);

/// <summary>The server's events, and when Discord was asked.</summary>
public sealed record DiscordServerEventList(IReadOnlyList<DiscordServerEvent> Events, DateTimeOffset ReadAt);

/// <summary>
/// The Discord server's scheduled events, whoever made them (calendar design §16).
/// </summary>
/// <remarks>
/// <para>
/// One request to Discord, <c>GET /guilds/{id}/scheduled-events</c>, made when someone opens a page
/// that shows the list and remembered for five minutes, the same rate as the online count. Nothing
/// is asked on a timer, and the bot does not ask for the gateway's scheduled-event intent: the list
/// is read when it is looked at, not kept up to date in between.
/// </para>
/// <para>
/// In Core so the API can ask without depending on the bot.
/// </para>
/// </remarks>
public interface IDiscordServerEvents
{
    /// <summary>
    /// The server's events that have not ended, or null when the bot is not connected or Discord did
    /// not answer.
    /// </summary>
    /// <param name="guildId">An opaque snowflake; never checked.</param>
    Task<DiscordServerEventList?> ReadAsync(string guildId, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. There is nothing to read.</summary>
public sealed class NoDiscordServerEvents : IDiscordServerEvents
{
    public Task<DiscordServerEventList?> ReadAsync(string guildId, CancellationToken ct = default) =>
        Task.FromResult<DiscordServerEventList?>(null);
}
