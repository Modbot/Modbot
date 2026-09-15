using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;
using Modbot.Cloud.Engine;

namespace Modbot.Cloud.Features.Retention;

/// <summary>What one retention run did.</summary>
public sealed record RetentionResult(long EventsRemoved, int ClocksRemoved);

/// <summary>
/// Deletes events older than the admin's window, a slice at a time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A delete, in slices.</strong> The server drops whole partitions instead (foundation 5.5)
/// because its presence facts run to hundreds of millions of rows. Cloud's events are a few dozen an
/// hour per client and are keyed on the client's event id, which a partitioned table cannot hold as
/// a unique key (cloud event backup spec 4.3). A day's expired events are a small delete, run in
/// slices of <see cref="Slice"/> so no single statement holds a long lock.
/// </para>
/// <para>
/// <c>install_clock</c> rows untouched for longer than the window go too. Totals stay: they hold
/// counts and random ids, nothing more. An install removed from the main database is not chased here;
/// its events simply age out.
/// </para>
/// </remarks>
public sealed class RetentionPruner(EngineContext engine, CloudContext cloud, TimeProvider time)
{
    public const int Slice = 10_000;

    public async Task<RetentionResult> RunAsync(CancellationToken ct = default)
    {
        var settings = await cloud.GetSettingsAsync(ct);
        if (settings.EventKeepDays <= 0)
            return new RetentionResult(0, 0);

        var cutoff = time.GetUtcNow().AddDays(-settings.EventKeepDays);
        long removed = 0;

        while (true)
        {
            var deleted = await engine.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM client_event
                WHERE ctid IN (SELECT ctid FROM client_event WHERE received_at < {cutoff} LIMIT {Slice})
                """, ct);

            removed += deleted;
            if (deleted < Slice)
                break;
        }

        var clocks = await engine.InstallClocks.Where(c => c.UpdatedAt < cutoff).ExecuteDeleteAsync(ct);

        return new RetentionResult(removed, clocks);
    }
}
