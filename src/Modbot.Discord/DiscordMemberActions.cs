using Modbot.Core.Discord;
using Modbot.Discord.Bot;
using Modbot.Discord.Gateway;

namespace Modbot.Discord;

/// <summary>
/// Bans, unbans, removes and times out one member of the Discord server for a person who asked
/// through the API, through the bot's live session (API conventions design §8).
/// </summary>
/// <remarks>
/// The same gateway calls ban sync and AutoMod make, by id over REST, with the reason written to the
/// server's audit log. No queue and no retry: an action that cannot happen now -- the bot offline,
/// a missing Discord permission -- is answered as not having happened, and the person decides
/// whether to ask again.
/// </remarks>
public sealed class DiscordMemberActions : IDiscordMemberActions
{
    private readonly DiscordBotService _bot;

    public DiscordMemberActions(DiscordBotService bot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        _bot = bot;
    }

    public Task<DiscordOffLimits> OffLimitsAsync(string guildId, CancellationToken ct = default)
        => Task.FromResult(
            _bot.ReadyGateway is { } gateway
                ? new DiscordOffLimits(gateway.BotUserId, gateway.GuildOwnerId(guildId))
                : DiscordOffLimits.None);

    public async Task<DiscordMemberOutcome> BanAsync(
        string guildId, string userId, string reason, int deleteMessageDays, CancellationToken ct = default)
        => _bot.ReadyGateway is { } gateway
            ? Outcome(await gateway.BanAsync(guildId, userId, reason, deleteMessageDays, ct).ConfigureAwait(false))
            : DiscordMemberOutcome.Offline;

    public async Task<DiscordMemberOutcome> UnbanAsync(string guildId, string userId, string reason, CancellationToken ct = default)
        => _bot.ReadyGateway is { } gateway
            ? Outcome(await gateway.UnbanAsync(guildId, userId, reason, ct).ConfigureAwait(false))
            : DiscordMemberOutcome.Offline;

    public async Task<DiscordMemberOutcome> KickAsync(string guildId, string userId, string reason, CancellationToken ct = default)
        => _bot.ReadyGateway is { } gateway
            ? Outcome(await gateway.RemoveAsync(guildId, userId, reason, ct).ConfigureAwait(false))
            : DiscordMemberOutcome.Offline;

    public async Task<DiscordMemberOutcome> TimeOutAsync(
        string guildId, string userId, TimeSpan duration, string reason, CancellationToken ct = default)
    {
        if (_bot.ReadyGateway is not { } gateway)
            return DiscordMemberOutcome.Offline;

        var outcome = await gateway.TimeOutAsync(guildId, userId, duration, reason, ct).ConfigureAwait(false);
        return outcome.Sent ? DiscordMemberOutcome.Ok : DiscordMemberOutcome.Failed(outcome.Error ?? "Discord refused.");
    }

    private static DiscordMemberOutcome Outcome(DiscordModerationOutcome outcome)
        => outcome.NothingToDo
            ? DiscordMemberOutcome.Already
            : outcome.Done
                ? DiscordMemberOutcome.Ok
                : DiscordMemberOutcome.Failed(outcome.Error ?? "Discord refused.");
}
