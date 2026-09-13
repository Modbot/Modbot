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
}
