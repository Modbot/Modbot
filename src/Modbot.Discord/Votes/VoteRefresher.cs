using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Serilog;

namespace Modbot.Discord.Votes;

/// <summary>
/// Rewrites the message of a vote that shows counts after people vote, a few seconds later and
/// not once for every press.
/// </summary>
/// <remarks>
/// A press only asks. The first ask for a vote starts a wait; the picks made during it are all in
/// the one rewrite that follows, and an ask after that starts the next wait. So a busy vote costs
/// Discord one edit every <see cref="DefaultWait"/> at most, however many people press. The counts
/// are read when the wait ends, never kept in memory, and a vote closed meanwhile is left to its
/// closing rewrite.
/// </remarks>
public sealed class VoteRefresher(IServiceScopeFactory scopes, TimeSpan? wait = null)
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(4);

    private readonly TimeSpan _wait = wait ?? DefaultWait;
    private readonly Dictionary<Guid, Task> _waiting = [];

    /// <summary>
    /// Asks for the vote's message to be rewritten soon. Returns the task that will do it, which is
    /// the one already waiting when there is one.
    /// </summary>
    public Task Request(Guid voteId)
    {
        lock (_waiting)
        {
            if (_waiting.TryGetValue(voteId, out var already))
                return already;

            var task = Task.Run(() => RunAsync(voteId));
            _waiting[voteId] = task;
            return task;
        }
    }

    private async Task RunAsync(Guid voteId)
    {
        try
        {
            await Task.Delay(_wait).ConfigureAwait(false);

            // From here an ask starts the next wait: this rewrite reads the counts below.
            lock (_waiting)
                _waiting.Remove(voteId);

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

            var vote = await db.Votes.AsNoTracking()
                .Include(v => v.Options)
                .FirstOrDefaultAsync(v => v.Id == voteId)
                .ConfigureAwait(false);

            if (vote is null || vote.Status != VoteStatuses.Open || !vote.ShowCounts)
                return;

            var counts = (await db.VotePicks.AsNoTracking()
                    .Where(p => p.VoteId == voteId)
                    .GroupBy(p => p.OptionId)
                    .Select(g => new { OptionId = g.Key, Count = g.Count() })
                    .ToListAsync()
                    .ConfigureAwait(false))
                .ToDictionary(c => c.OptionId, c => c.Count);

            var outcome = await scope.ServiceProvider.GetRequiredService<IDiscordVoteActions>()
                .RefreshAsync(vote, counts)
                .ConfigureAwait(false);

            if (!outcome.Done)
                Log.Warning("A vote's counts could not be shown in Discord: {Error}", outcome.Error);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (_waiting)
                _waiting.Remove(voteId);

            Log.Warning(e, "A vote's counts could not be shown in Discord");
        }
    }
}
