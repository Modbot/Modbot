using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;

namespace Modbot.Cloud.Features.Site;

/// <summary>
/// Records that a Modbot address was opened: in <c>page_instance</c>, and against the visitor's IP
/// address in <c>visitor_instance</c>.
/// </summary>
/// <remarks>
/// <para>
/// One page view saves twice on purpose. my.modbot.co saves as it serves the page, and its app saves
/// again once it has rendered, so a page the browser took from its cache, or reached by navigating
/// inside the app, is still recorded.
/// </para>
/// <para>
/// The two must count as one visit. A save for the same address and URL within
/// <see cref="RepeatWindow"/> of the visit last counted moves last-seen forward and adds no visit.
/// The window runs from the counted visit, not from the latest save, so somebody who reloads every
/// few minutes is counted again once it has passed.
/// </para>
/// </remarks>
public static class InstanceVisits
{
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);

    /// <summary>How far back the list an address can read reaches.</summary>
    public static readonly TimeSpan HistoryReach = TimeSpan.FromDays(90);

    public const int HistoryLimit = 50;

    /// <param name="ip">The visitor's address. Null records the URL without any list for an address.</param>
    /// <returns>Whether this save counted as a new visit.</returns>
    public static async Task<bool> RecordAsync(
        CloudContext db, string? ip, string url, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var counted = ip is null || await RecordForAddressAsync(db, ip, url, now, ct);
        var added = counted ? 1 : 0;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO page_instance (instance_url, first_seen_at, last_seen_at, visits)
            VALUES ({url}, {now}, {now}, 1)
            ON CONFLICT (instance_url) DO UPDATE
            SET last_seen_at = GREATEST(page_instance.last_seen_at, EXCLUDED.last_seen_at),
                visits = page_instance.visits + {added}
            """,
            ct);

        await transaction.CommitAsync(ct);
        return counted;
    }

    private static async Task<bool> RecordForAddressAsync(
        CloudContext db, string ip, string url, DateTimeOffset now, CancellationToken ct)
    {
        // A concurrent first save for the same pair waits here for the other to commit and then
        // inserts nothing, so the pair is inserted and counted once.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO visitor_instance (ip_address, instance_url, first_seen_at, last_seen_at, last_visit_at, visits)
            VALUES ({ip}, {url}, {now}, {now}, {now}, 1)
            ON CONFLICT (ip_address, instance_url) DO NOTHING
            """,
            ct);

        if (inserted == 1)
            return true;

        // Locked until the transaction ends, so the other save of the same page view reads the visit
        // this one counted rather than the one before it.
        var lastVisit = await db.Database
            .SqlQuery<DateTimeOffset>(
                $"""
                SELECT last_visit_at AS "Value" FROM visitor_instance
                WHERE ip_address = {ip} AND instance_url = {url}
                FOR UPDATE
                """)
            .ToListAsync(ct);

        var counted = lastVisit.Count == 0 || lastVisit[0] <= now - RepeatWindow;
        var added = counted ? 1 : 0;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE visitor_instance
            SET last_seen_at = GREATEST(last_seen_at, {now}),
                visits = visits + {added},
                last_visit_at = CASE WHEN {counted} THEN {now} ELSE last_visit_at END
            WHERE ip_address = {ip} AND instance_url = {url}
            """,
            ct);

        return counted;
    }
}
