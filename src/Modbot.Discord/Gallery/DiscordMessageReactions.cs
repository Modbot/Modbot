using Modbot.Core.Discord;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Gallery;

/// <summary><see cref="IDiscordMessageReactions"/> through the bot's live session.</summary>
/// <param name="readyGateway">The bot's session while it is ready, or null.</param>
public sealed class DiscordMessageReactions(Func<IDiscordGateway?> readyGateway) : IDiscordMessageReactions
{
    public async Task<bool> AddAsync(string channelId, string messageId, string emoji, CancellationToken ct = default)
    {
        if (readyGateway() is not { } gateway)
            return false;

        var outcome = await gateway.AddReactionAsync(channelId, messageId, emoji, ct).ConfigureAwait(false);
        return outcome.Sent;
    }
}
