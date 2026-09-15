using Modbot.Core.Discord;
using Modbot.Discord.Bot;
using Modbot.Discord.Gateway;

namespace Modbot.Discord;

/// <summary>
/// Deletes messages and times people out for AI moderation rules set to act, through the bot's
/// live session.
/// </summary>
/// <remarks>
/// No queue and no retry. An action that cannot happen now -- the bot offline, a missing
/// permission -- is recorded as not having happened, and the flag stays for a moderator. Deleting
/// a message ten minutes late, after people have read it, is a different act from deleting it.
/// </remarks>
public sealed class DiscordModerationActions : IDiscordModerationActions
{
    private const string Offline = "The Discord bot is not connected.";

    private readonly DiscordBotService _bot;

    public DiscordModerationActions(DiscordBotService bot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        _bot = bot;
    }

    public async Task<DiscordActionOutcome> DeleteMessageAsync(
        string channelId, string messageId, string reason, CancellationToken ct = default)
    {
        if (_bot.ReadyGateway is not { } gateway)
            return DiscordActionOutcome.Failed(Offline);

        return Outcome(await gateway.DeleteMessageAsync(channelId, messageId, reason, ct).ConfigureAwait(false));
    }

    public async Task<DiscordActionOutcome> TimeOutAsync(
        string guildId, string userId, TimeSpan duration, string reason, CancellationToken ct = default)
    {
        if (_bot.ReadyGateway is not { } gateway)
            return DiscordActionOutcome.Failed(Offline);

        return Outcome(await gateway.TimeOutAsync(guildId, userId, duration, reason, ct).ConfigureAwait(false));
    }

    private static DiscordActionOutcome Outcome(DiscordPostOutcome outcome)
        => outcome.Sent ? DiscordActionOutcome.Ok : DiscordActionOutcome.Failed(outcome.Error ?? "Discord refused.");
}
