using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Discord.Tests.Fakes;

/// <summary>
/// Stands in for the bot's side of a vote: records what was posted, refreshed and closed, and
/// answers as told.
/// </summary>
public sealed class FakeVoteActions : IDiscordVoteActions
{
    private readonly object _gate = new();

    /// <summary>What every post answers.</summary>
    public VoteSiteOutcome PostOutcome { get; set; } = VoteSiteOutcome.Ok("777");

    /// <summary>What every close answers.</summary>
    public VoteSiteOutcome ResultOutcome { get; set; } = VoteSiteOutcome.Ok();

    public List<Vote> Posted { get; } = [];

    /// <summary>The counts each refresh was asked to show.</summary>
    public List<(Guid VoteId, Dictionary<Guid, int> Counts)> Refreshed { get; } = [];

    public List<(Guid VoteId, Dictionary<Guid, int> Counts)> Results { get; } = [];

    public List<(string ChannelId, string MessageId)> Deleted { get; } = [];

    public Task<VoteSiteOutcome> PostAsync(Vote vote, CancellationToken ct = default)
    {
        lock (_gate)
            Posted.Add(vote);

        return Task.FromResult(PostOutcome);
    }

    public Task<VoteSiteOutcome> RefreshAsync(Vote vote, IReadOnlyDictionary<Guid, int> counts, CancellationToken ct = default)
    {
        lock (_gate)
            Refreshed.Add((vote.Id, new Dictionary<Guid, int>(counts)));

        return Task.FromResult(VoteSiteOutcome.Ok());
    }

    public Task<VoteSiteOutcome> ShowResultAsync(Vote vote, IReadOnlyDictionary<Guid, int> counts, CancellationToken ct = default)
    {
        lock (_gate)
            Results.Add((vote.Id, new Dictionary<Guid, int>(counts)));

        return Task.FromResult(ResultOutcome);
    }

    public Task<VoteSiteOutcome> DeleteAsync(string channelId, string messageId, CancellationToken ct = default)
    {
        lock (_gate)
            Deleted.Add((channelId, messageId));

        return Task.FromResult(VoteSiteOutcome.Ok());
    }
}
