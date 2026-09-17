using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Email;

/// <summary>Where the daily email limit stands, for the settings page and the health page.</summary>
/// <param name="Limit">The saved limit per 24 hours.</param>
/// <param name="SentInLast24Hours">Sends in the rolling window, ones in progress included.</param>
/// <param name="Queued">Messages waiting, for instance or for their next try.</param>
/// <param name="Failed">Messages given up on in the last few days.</param>
/// <param name="NextSendAt">When the next queued message should go out. Null when none is waiting or none can.</param>
public sealed record EmailQueueSummary(
    int Limit,
    int SentInLast24Hours,
    int Queued,
    int Failed,
    DateTimeOffset? NextSendAt);

public static class EmailQueueStatus
{
    public static async Task<EmailQueueSummary> ReadAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var limit = await EmailSender.LimitAsync(db, ct);
        var sent = await EmailSender.SentInWindowAsync(db, now, ct);

        var counts = await db.EmailQueue.AsNoTracking()
            .Where(e => e.State == EmailStates.Queued || e.State == EmailStates.Failed)
            .GroupBy(e => e.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var queued = counts.FirstOrDefault(c => c.State == EmailStates.Queued)?.Count ?? 0;
        var failed = counts.FirstOrDefault(c => c.State == EmailStates.Failed)?.Count ?? 0;

        return new EmailQueueSummary(limit, sent.Count, queued, failed, await NextSendAtAsync(db, sent, limit, now, ct));
    }

    private static async Task<DateTimeOffset?> NextSendAtAsync(
        ModbotContext db, List<DateTimeOffset> sent, int limit, DateTimeOffset now, CancellationToken ct)
    {
        var head = await EmailSender.Waiting(db, now).AsNoTracking()
            .Select(e => e.Kind)
            .FirstOrDefaultAsync(ct);

        if (head is not null)
        {
            var (queue, at) = EmailLimit.WhenCanSend(sent, [], EmailLimit.KindOf(head), limit, now);
            return queue ? at : now;
        }

        // Everything waiting is waiting for its next try after a refusal.
        return await db.EmailQueue.AsNoTracking()
            .Where(e => e.State == EmailStates.Queued && e.NextAttemptAt != null)
            .MinAsync(e => e.NextAttemptAt, ct);
    }
}
