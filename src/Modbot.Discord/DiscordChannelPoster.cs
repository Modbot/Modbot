using Modbot.Core.Discord;
using Modbot.Discord.Gateway;

namespace Modbot.Discord;

/// <summary>
/// <see cref="IDiscordChannelPoster"/> through the bot's live session.
/// </summary>
/// <param name="readyGateway">The bot's session while it is ready, or null.</param>
public sealed class DiscordChannelPoster(Func<IDiscordGateway?> readyGateway) : IDiscordChannelPoster
{
    public async Task<bool> PostAsync(string channelId, string text, CancellationToken ct = default)
    {
        if (readyGateway() is not { } gateway)
            return false;

        // No role is named, so nobody is pinged.
        var outcome = await gateway.SendPostAsync(channelId, text, roleId: null, pictures: null, ct).ConfigureAwait(false);

        return outcome.Sent;
    }
}
