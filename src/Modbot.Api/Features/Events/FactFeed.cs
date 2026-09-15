using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Events;

/// <param name="Facts">In id order. Every fact here may be sent; none after a gap still being waited on.</param>
/// <param name="Through">The new cursor: the last id read, or the old cursor when nothing was.</param>
public sealed record FactFeedPage(IReadOnlyList<ModbotEvent> Facts, long Through);

/// <summary>
/// Reads new facts after a cursor for the WebSocket and webhooks, without ever skipping one that
/// commits late (API keys design §4.2).
/// </summary>
/// <remarks>
/// <para>
/// The cursor is a fact id, the shape the Discord moderation log uses. Ids come from one sequence
/// but <strong>a lower id can commit after a higher one</strong>: two transactions take 100 and
/// 101, and 101 commits first. The Discord poster tolerates that; a feed that promises "every
/// event" cannot.
/// </para>
/// <para>
/// So consecutive ids are handed out at once, and reading stops at a missing id -- unless the fact
/// after the gap was observed more than <see cref="GapWait"/> ago. The missing id was taken before
/// that fact was written, so only a transaction held open that long can still fill it; a gap that
/// old is a rollback or a failed insert, and is stepped over. Gaps are rare, so the ordinary case
/// costs nothing, and the rule needs no lock and no second table. Both times are
/// <c>IModbotClock</c>'s (foundation §4.4).
/// </para>
/// </remarks>
public sealed class FactFeed
{
    public static readonly TimeSpan DefaultGapWait = TimeSpan.FromSeconds(10);

    private readonly ModbotContext _db;

    public FactFeed(ModbotContext db, TimeSpan? gapWait = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
        GapWait = gapWait ?? DefaultGapWait;
    }

    public TimeSpan GapWait { get; }

    public async Task<FactFeedPage> ReadAsync(long after, int limit, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var rows = await _db.Events.AsNoTracking()
            .Where(e => e.Id > after)
            .OrderBy(e => e.Id)
            .Take(limit)
            .ToListAsync(ct);

        var ready = new List<ModbotEvent>(rows.Count);
        var expected = after + 1;

        foreach (var row in rows)
        {
            if (row.Id != expected && row.ObservedAt > now - GapWait)
                break;

            ready.Add(row);
            expected = row.Id + 1;
        }

        return new FactFeedPage(ready, ready.Count > 0 ? ready[^1].Id : after);
    }

    /// <summary>The newest fact id, or 0 for an empty log. "From now" starts after it.</summary>
    public async Task<long> NewestIdAsync(CancellationToken ct)
        => await _db.Events.AsNoTracking().MaxAsync(e => (long?)e.Id, ct) ?? 0;

    /// <summary>The oldest fact retention has kept, or null for an empty log.</summary>
    public Task<long?> OldestIdAsync(CancellationToken ct)
        => _db.Events.AsNoTracking().MinAsync(e => (long?)e.Id, ct);
}
