namespace Modbot.Core.Discord;

/// <summary>
/// How many of a server's members Discord counts as online, as its own server profile shows it.
/// </summary>
/// <remarks>
/// <para>
/// The bot does not ask for Discord's presence intent, so it never hears who is online. Discord
/// answers the count itself when asked for the server with its counts, <c>GET /guilds/{id}</c>
/// with <c>with_counts=true</c>. That is one request, made when someone opens a page that shows
/// the count and remembered for five minutes, so a server costs at most twelve an hour and nothing
/// while nobody looks. The owner chose that rate over asking on a timer (2026-09-27).
/// </para>
/// <para>
/// In Core so the API can ask without depending on the bot.
/// </para>
/// </remarks>
public interface IDiscordOnlineCount
{
    /// <summary>
    /// Discord's count of the server's members who are online, or null when the bot is not
    /// connected or Discord did not give it.
    /// </summary>
    /// <param name="guildId">An opaque snowflake; never checked.</param>
    Task<int?> ReadAsync(string guildId, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. There is no count to give.</summary>
public sealed class NoDiscordOnlineCount : IDiscordOnlineCount
{
    public Task<int?> ReadAsync(string guildId, CancellationToken ct = default) => Task.FromResult<int?>(null);
}
