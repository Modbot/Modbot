namespace Modbot.Discord.Bot;

/// <summary>Timings for the bot's connection loop. Tests shorten them; the defaults suit a deployment.</summary>
public sealed class DiscordBotOptions
{
    /// <summary>
    /// How often the settings row is re-read for a changed token, guild or channel. One
    /// single-row read; the same shape as the sync producers' pacing refresh.
    /// </summary>
    public TimeSpan SettingsPollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The wait before the first retry after a failed sign-in. Doubles each time.</summary>
    public TimeSpan FirstRetry { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxRetry { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a dropped session is left to the library's own reconnect before the bot throws
    /// it away and signs in afresh.
    /// </summary>
    public TimeSpan RebuildAfterDisconnected { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The wait between one page of history and the next while reading back or catching up.
    /// </summary>
    /// <remarks>
    /// Discord allows a bot about fifty requests a second overall and a few a second per channel,
    /// and the library queues each request behind those limits by itself. Reading as fast as that
    /// allows would put every post the bot makes -- the moderation log, instance cards -- in the
    /// same queue behind thousands of history pages. Two pages a second reads a channel of ten
    /// thousand messages in under a minute and a server of a million in about three hours, while
    /// leaving nearly all of the limit free.
    /// </remarks>
    public TimeSpan ReadPause { get; init; } = TimeSpan.FromMilliseconds(500);
}
