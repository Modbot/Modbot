using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Analytics.Team;

/// <summary>
/// "Is moderation keeping up?" as waits: how long join requests, flags and reviews sat before
/// somebody decided them (<see cref="QueueWait"/>).
/// </summary>
/// <remarks>
/// <para>
/// Counted over the decisions made in the window, because a decision is when a wait becomes known;
/// what is still waiting has no length yet. Each queue gives its middle wait and the middle of each
/// day's, never an average: one request left over a weekend would otherwise outweigh a hundred
/// answered within the hour.
/// </para>
/// <para>
/// <strong>Join requests</strong> come from VRChat's audit log: the request, then the approval (the
/// person joining, let in by somebody else) or the rejection or block. A decision is matched to the
/// latest request from the same person in the thirty days before it, and only when no other decision
/// came between the two. That keeps out joins by invite, which no request came before, and a join by
/// invite after a rejection, whose request was already answered. A request somebody withdrew, or answered in a way the audit
/// log does not record, has no decision and is not counted.
/// </para>
/// <para>
/// <strong>Flags</strong> are AutoMod's, from when the rule matched to when a moderator dismissed it
/// or closed its review as right. <strong>Reviews</strong> are the pattern reviews, opened to closed.
/// </para>
/// </remarks>
public sealed class TeamQueues(AnalyticsSql sql)
{
    /// <summary>How far before a decision its request may be and still be the one it answered.</summary>
    private static readonly TimeSpan RequestReach = TimeSpan.FromDays(30);

    public async Task<IReadOnlyList<QueueWait>> RunAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var start = AnalyticsSql.DayStart(from);
        var end = AnalyticsSql.DayEnd(to);

        return
        [
            Wait(Queues.JoinRequests, await RequestsAsync(start, end, ct)),
            Wait(Queues.Flags, await FlagsAsync(start, end, ct)),
            Wait(Queues.Reviews, await ReviewsAsync(start, end, ct)),
        ];
    }

    /// <summary>A queue's waits, from when each was asked and decided.</summary>
    public static QueueWait Wait(string queue, IReadOnlyList<(DateTimeOffset Asked, DateTimeOffset Decided)> waits)
    {
        ArgumentNullException.ThrowIfNull(waits);

        static decimal Minutes((DateTimeOffset Asked, DateTimeOffset Decided) w)
            => (decimal)Math.Max(0, (w.Decided - w.Asked).TotalMinutes);

        var perDay = waits
            .GroupBy(w => AnalyticsSql.DayOf(w.Decided))
            .OrderBy(g => g.Key)
            .Select(g => new DayValue(g.Key, Round(Middles.Of(g.Select(Minutes))!.Value)))
            .ToList();

        return new QueueWait(
            queue,
            waits.Count,
            Middles.Of(waits.Select(Minutes)) is { } middle ? Round(middle) : null,
            perDay);
    }

    private static decimal Round(decimal minutes) => Math.Round(minutes, 1);

    private async Task<IReadOnlyList<(DateTimeOffset, DateTimeOffset)>> RequestsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct)
    {
        // A decision answers the latest request before it only when nothing decided that request
        // first. Without that, somebody rejected and invited in a week later read as a request that
        // waited a week.
        const string Sql = """
            WITH decisions AS (
                SELECT e.id, e.subject_id, e.occurred_at
                FROM modbot_event e
                WHERE e.subject_id IS NOT NULL
                  AND e.occurred_at >= @from - @reach AND e.occurred_at < @to
                  AND (e.type = ANY(@turnedAway)
                       OR (e.type = @joined AND e.actor_id IS NOT NULL AND e.actor_id <> e.subject_id))
            )
            SELECT r.asked_at, d.occurred_at
            FROM decisions d
            CROSS JOIN LATERAL (
                SELECT MAX(q.occurred_at) AS asked_at
                FROM modbot_event q
                WHERE q.type = @asked
                  AND q.subject_id = d.subject_id
                  AND q.occurred_at <= d.occurred_at
                  AND q.occurred_at > d.occurred_at - @reach
            ) r
            WHERE d.occurred_at >= @from AND d.occurred_at < @to
              AND r.asked_at IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM decisions p
                  WHERE p.subject_id = d.subject_id
                    AND p.occurred_at >= r.asked_at
                    AND (p.occurred_at < d.occurred_at OR (p.occurred_at = d.occurred_at AND p.id < d.id)))
            """;

        return await sql.ReadAsync(
            Sql,
            r => (AnalyticsSql.InstantOf(r, 0), AnalyticsSql.InstantOf(r, 1)),
            ct,
            ("asked", FactType.JoinRequestCreated),
            ("turnedAway", new[] { FactType.JoinRequestRejected, FactType.JoinRequestBlocked }),
            ("joined", FactType.MemberJoined),
            ("reach", RequestReach),
            ("from", start),
            ("to", end));
    }

    private async Task<IReadOnlyList<(DateTimeOffset, DateTimeOffset)>> FlagsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct)
    {
        var rows = await sql.Db.ModerationFlags.AsNoTracking()
            .Where(f => (f.DismissedAt ?? f.ConfirmedAt) >= start && (f.DismissedAt ?? f.ConfirmedAt) < end)
            .Select(f => new { f.FlaggedAt, Decided = (f.DismissedAt ?? f.ConfirmedAt)!.Value })
            .ToListAsync(ct);

        return rows.Select(r => (r.FlaggedAt, r.Decided)).ToList();
    }

    private async Task<IReadOnlyList<(DateTimeOffset, DateTimeOffset)>> ReviewsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct)
    {
        var rows = await sql.Db.Reviews.AsNoTracking()
            .Where(r => r.ClosedAt >= start && r.ClosedAt < end)
            .Select(r => new { r.OpenedAt, Closed = r.ClosedAt!.Value })
            .ToListAsync(ct);

        return rows.Select(r => (r.OpenedAt, r.Closed)).ToList();
    }
}
