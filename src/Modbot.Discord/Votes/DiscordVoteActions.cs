using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Votes;

/// <summary>
/// <see cref="IDiscordVoteActions"/> through the bot's live session: posting a vote and rewriting
/// its message with the result.
/// </summary>
/// <param name="readyGateway">The bot's session while it is ready, or null.</param>
public sealed class DiscordVoteActions(Func<IDiscordGateway?> readyGateway) : IDiscordVoteActions
{
    public async Task<VoteSiteOutcome> PostAsync(Vote vote, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vote);

        if (readyGateway() is not { } gateway)
            return VoteSiteOutcome.Offline;

        var open = VoteMessage.Open(vote);

        var outcome = VoteMessage.Lists(vote) is { Count: > 0 } lists
            ? await gateway.PostWithListsAsync(vote.ChannelId, null, [open], lists, ct).ConfigureAwait(false)
            : await gateway.PostWithActionsAsync(vote.ChannelId, null, [open], null, VoteMessage.Buttons(vote), ct).ConfigureAwait(false);

        return Result(outcome);
    }

    public async Task<VoteSiteOutcome> ShowResultAsync(Vote vote, IReadOnlyDictionary<Guid, int> counts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vote);
        ArgumentNullException.ThrowIfNull(counts);

        if (readyGateway() is not { } gateway)
            return VoteSiteOutcome.Offline;

        // No buttons: the same edit takes both the buttons and the lists away.
        var outcome = await gateway
            .EditWithActionsAsync(vote.ChannelId, vote.MessageId, null, [VoteMessage.Closed(vote, counts)], null, null, ct)
            .ConfigureAwait(false);

        return Result(outcome);
    }

    public async Task<VoteSiteOutcome> DeleteAsync(string channelId, string messageId, CancellationToken ct = default)
    {
        if (readyGateway() is not { } gateway)
            return VoteSiteOutcome.Offline;

        var outcome = await gateway
            .DeleteMessageAsync(channelId, messageId, "Modbot: vote could not be saved", ct)
            .ConfigureAwait(false);

        // A message already gone is what a delete wanted.
        return outcome.Sent || outcome.NotFound
            ? VoteSiteOutcome.Ok()
            : VoteSiteOutcome.Failed(outcome.Error ?? "Discord refused.");
    }

    private static VoteSiteOutcome Result(DiscordPostOutcome outcome)
        => outcome.Sent
            ? VoteSiteOutcome.Ok(outcome.MessageId)
            : VoteSiteOutcome.Failed(outcome.Error ?? "Discord refused.");
}
