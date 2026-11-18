namespace Modbot.Core.Discord;

/// <summary>
/// Posts one line of plain text in a Discord channel, as the deployment's bot.
/// </summary>
/// <remarks>
/// In Core so the API can ask without depending on the bot, as it does through
/// <see cref="IDiscordBirthdayActions"/>. Made for the flagged alerts (a flagged join today, the
/// avatar watch and the crash alert after it), which say a sentence and need nothing more. Nobody
/// is pinged, whatever the text says.
/// </remarks>
public interface IDiscordChannelPoster
{
    /// <returns>Whether Discord took the message. False when the bot is offline or Discord refused.</returns>
    Task<bool> PostAsync(string channelId, string text, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. Nothing is posted.</summary>
public sealed class NoDiscordChannelPoster : IDiscordChannelPoster
{
    public Task<bool> PostAsync(string channelId, string text, CancellationToken ct = default)
        => Task.FromResult(false);
}
