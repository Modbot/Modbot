using Microsoft.EntityFrameworkCore;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.Posts;

/// <param name="Asked">Posts asked about this pass.</param>
/// <param name="Counted">Posts whose numbers were saved.</param>
/// <param name="Missing">Posts Bluesky did not return (deleted there): they keep their last numbers.</param>
public sealed record PostBlueskyCountsPass(int Asked, int Counted, int Missing);

/// <summary>
/// Reads the likes, reposts and replies of the Bluesky posts Modbot sent, from Bluesky's public API
/// (<see cref="BlueskyClient.PostCountsAsync"/>), with no sign-in.
/// </summary>
/// <remarks>
/// <para>
/// A Posted Bluesky destination sent within <see cref="Window"/> is counted again <see cref="Every"/>
/// after it was last counted; an older one keeps its last numbers. One call a pass asks about
/// <see cref="BlueskyClient.MostCountedAtOnce"/> posts at most, those waiting longest first.
/// </para>
/// <para>
/// A post missing from the answer is gone from Bluesky: it keeps its last numbers and is not asked for
/// again before <see cref="Every"/> has passed. A rate limit stops the pass until the time Bluesky says
/// it resets and is never retried (CLAUDE.md, §4.3.1); that stop is apart from the account's own lane
/// (<see cref="Settings.BlueskyStoppedUntil"/>), because it is another server. Any failure is kept for
/// Health until a read goes through.
/// </para>
/// <para>
/// <strong>Rate limit: NOT MEASURED.</strong> Nobody has measured the public API's limits for this call
/// (CLAUDE.md, §4.3.4: not in the spec's table, <c>.agent/research/</c>, <c>RateLimitOptions.cs</c> or
/// the <c>libs/</c> clones). The placeholder is the cautious one used for a similar write lane, one
/// call every <see cref="CallGap"/>; the pass makes one call and runs every
/// <see cref="PostBlueskyService.PollInterval"/>, which is longer.
/// </para>
/// <para>
/// The numbers are written without raising the post's version: counting is not an edit, and must not
/// fail one saved at the same moment.
/// </para>
/// </remarks>
public sealed class PostBlueskyCounts
{
    /// <summary>How long after it was sent a post is still counted.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>The least time between two counts of one post.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    /// <summary>NOT MEASURED: the least time between two calls to the public API. A placeholder.</summary>
    public static readonly TimeSpan CallGap = TimeSpan.FromSeconds(10);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly BlueskyClient _client;
    private readonly ILogger _log;

    public PostBlueskyCounts(ModbotContext db, IModbotClock clock, BlueskyClient client, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(client);

        _db = db;
        _clock = clock;
        _client = client;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<PostBlueskyCountsPass> RunOnceAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.BlueskyCountsStoppedUntil, s.BlueskyCountsError })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (settings is null || (settings.BlueskyCountsStoppedUntil is { } stopped && now < stopped))
            return new PostBlueskyCountsPass(0, 0, 0);

        var since = now - Window;
        var due = now - Every;

        // Never counted first, then the longest ago.
        var rows = await _db.PostDestinations.AsNoTracking()
            .Where(d => d.Network == PostNetworks.Bluesky
                && d.State == PostDestinationStates.Posted
                && d.ExternalId != null
                && d.PostedAt >= since
                && (d.CountedAt == null || d.CountedAt <= due))
            .OrderBy(d => d.CountedAt == null ? 0 : 1)
            .ThenBy(d => d.CountedAt)
            .ThenBy(d => d.PostedAt)
            .Select(d => new { d.Id, Uri = d.ExternalId! })
            .Take(BlueskyClient.MostCountedAtOnce)
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0)
            return new PostBlueskyCountsPass(0, 0, 0);

        var answer = await _client.PostCountsAsync([.. rows.Select(r => r.Uri)], ct).ConfigureAwait(false);

        if (answer.Failure is { } failure)
        {
            await FailedAsync(failure, now, ct).ConfigureAwait(false);
            return new PostBlueskyCountsPass(rows.Count, 0, 0);
        }

        var found = (answer.Value ?? []).GroupBy(c => c.Uri, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var counted = 0;
        var missing = 0;

        foreach (var row in rows)
        {
            if (found.TryGetValue(row.Uri, out var counts))
            {
                counted++;
                await _db.PostDestinations.Where(d => d.Id == row.Id)
                    .ExecuteUpdateAsync(
                        u => u.SetProperty(d => d.LikeCount, counts.Likes)
                            .SetProperty(d => d.RepostCount, counts.Reposts)
                            .SetProperty(d => d.ReplyCount, counts.Replies)
                            .SetProperty(d => d.CountedAt, now),
                        ct).ConfigureAwait(false);
            }
            else
            {
                missing++;
                await _db.PostDestinations.Where(d => d.Id == row.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(d => d.CountedAt, now), ct).ConfigureAwait(false);
            }
        }

        if (settings.BlueskyCountsError is not null)
        {
            await _db.Settings.Where(s => s.Id == 1)
                .ExecuteUpdateAsync(
                    u => u.SetProperty(s => s.BlueskyCountsError, (string?)null)
                        .SetProperty(s => s.BlueskyCountsErrorAt, (DateTimeOffset?)null),
                    ct).ConfigureAwait(false);
        }

        return new PostBlueskyCountsPass(rows.Count, counted, missing);
    }

    /// <summary>Kept for Health; a rate limit also stops the pass until it resets. Nothing is asked again this pass.</summary>
    private async Task FailedAsync(BlueskyFailure failure, DateTimeOffset now, CancellationToken ct)
    {
        var reason = BlueskyErrors.Sentence(failure);

        if (failure.StopsTheLane)
        {
            var until = BlueskyErrors.StopUntil(failure, now);
            _log.Warning("Bluesky is limiting the reading of post numbers; they are not asked for again until the limit resets");

            await _db.Settings
                .Where(s => s.Id == 1 && (s.BlueskyCountsStoppedUntil == null || s.BlueskyCountsStoppedUntil < until))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskyCountsStoppedUntil, until), ct).ConfigureAwait(false);
        }
        else
        {
            _log.Warning("The numbers under the Bluesky posts could not be read: {Reason}", reason);
        }

        await _db.Settings.Where(s => s.Id == 1)
            .ExecuteUpdateAsync(
                u => u.SetProperty(s => s.BlueskyCountsError, reason).SetProperty(s => s.BlueskyCountsErrorAt, now),
                ct).ConfigureAwait(false);
    }
}
