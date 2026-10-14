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
    // A count refresh and the closing rewrite of one message must not cross: a refresh that landed
    // after the result would put the buttons back. Messages already closed here are never
    // refreshed, and the two edits take turns.
    private readonly SemaphoreSlim _edits = new(1, 1);
    // Only the latest few are kept: a refresh already past its check of the database can only be
    // for a vote closed moments ago, and the refresher itself skips any vote the database says is
    // closed, so a long list would only grow.
    private const int MostClosedKept = 100;
    private readonly HashSet<string> _closed = [];
    private readonly Queue<string> _closedOrder = new();

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

        lock (_closed)
        {
            if (_closed.Add(vote.MessageId))
            {
                _closedOrder.Enqueue(vote.MessageId);

                if (_closedOrder.Count > MostClosedKept)
                    _closed.Remove(_closedOrder.Dequeue());
            }
        }

        await _edits.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // No buttons: the same edit takes both the buttons and the lists away.
            var outcome = await gateway
                .EditWithActionsAsync(vote.ChannelId, vote.MessageId, null, [VoteMessage.Closed(vote, counts)], null, null, ct)
                .ConfigureAwait(false);

            return Result(outcome);
        }
        finally
        {
            _edits.Release();
        }
    }

    public async Task<VoteSiteOutcome> RefreshAsync(Vote vote, IReadOnlyDictionary<Guid, int> counts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vote);
        ArgumentNullException.ThrowIfNull(counts);

        if (readyGateway() is not { } gateway)
            return VoteSiteOutcome.Offline;

        await _edits.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            lock (_closed)
            {
                if (_closed.Contains(vote.MessageId))
                    return VoteSiteOutcome.Ok();
            }

            var open = VoteMessage.Open(vote, counts);

            var outcome = VoteMessage.Lists(vote) is { Count: > 0 } lists
                ? await gateway.EditWithListsAsync(vote.ChannelId, vote.MessageId, null, [open], lists, ct).ConfigureAwait(false)
                : await gateway.EditWithActionsAsync(vote.ChannelId, vote.MessageId, null, [open], null, VoteMessage.Buttons(vote), ct).ConfigureAwait(false);

            return Result(outcome);
        }
        finally
        {
            _edits.Release();
        }
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
