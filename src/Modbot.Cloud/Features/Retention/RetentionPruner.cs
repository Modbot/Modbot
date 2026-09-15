using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;
using Modbot.Cloud.Engine;

namespace Modbot.Cloud.Features.Retention;

/// <summary>What one retention run did.</summary>
public sealed record RetentionResult(IReadOnlyList<string> DroppedPartitions, int LogFilesRemoved, int ClocksRemoved);

/// <summary>
/// Drops log lines and parsed events older than the admin's windows, a whole month at a time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A DROP, never a DELETE.</strong> A month of log lines is tens of millions of rows; a
/// mass delete of them is hours of work and a table full of dead rows. Dropping the partition they
/// live in is a catalogue update (foundation 5.5).
/// </para>
/// <para>
/// A partition is dropped only once its whole month is past the window, so a line may outlive its
/// window by up to a month. Only partitions whose name <see cref="PartitionMaintainer"/> would have
/// made are touched; a table attached by hand is left alone.
/// </para>
/// <para>
/// <c>log_file</c> and <c>install_clock</c> rows untouched for longer than the log line window go
/// too. Those are the only per-install rows that would otherwise outlive the lines. Totals stay:
/// they hold counts and random ids, nothing from a log (cloud log backup spec 6).
/// </para>
/// <para>
/// An install removed from the main database is not chased here. Its rows simply age out.
/// </para>
/// </remarks>
public sealed class RetentionPruner(EngineContext engine, CloudContext cloud, TimeProvider time)
{
    public async Task<RetentionResult> RunAsync(CancellationToken ct = default)
    {
        var settings = await cloud.GetSettingsAsync(ct);
        var now = time.GetUtcNow();
        var dropped = new List<string>();

        dropped.AddRange(await DropExpiredAsync("log_line", settings.LogLineKeepDays, now, ct));
        dropped.AddRange(await DropExpiredAsync("log_event", settings.LogEventKeepDays, now, ct));

        var filesRemoved = 0;
        var clocksRemoved = 0;

        if (settings.LogLineKeepDays > 0)
        {
            var cutoff = now.AddDays(-settings.LogLineKeepDays);
            filesRemoved = await engine.LogFiles.Where(f => f.LastReceivedAt < cutoff).ExecuteDeleteAsync(ct);
            clocksRemoved = await engine.InstallClocks.Where(c => c.UpdatedAt < cutoff).ExecuteDeleteAsync(ct);
        }

        return new RetentionResult(dropped, filesRemoved, clocksRemoved);
    }

    private async Task<IReadOnlyList<string>> DropExpiredAsync(string table, int keepDays, DateTimeOffset now, CancellationToken ct)
    {
        if (keepDays <= 0)
            return [];

        var cutoff = now.AddDays(-keepDays);
        var dropped = new List<string>();

        var partitions = await engine.Database
            .SqlQuery<string>($"""
                SELECT child.relname AS "Value"
                FROM pg_inherits
                JOIN pg_class parent ON parent.oid = pg_inherits.inhparent
                JOIN pg_class child ON child.oid = pg_inherits.inhrelid
                WHERE parent.relname = {table}
                """)
            .ToListAsync(ct);

        foreach (var name in partitions.Order(StringComparer.Ordinal))
        {
            if (PartitionMaintainer.UpperBound(table, name) is not { } upper || upper > cutoff)
                continue;

            // The name matched PartitionMaintainer's pattern for this table, which allows only
            // letters, digits and underscores, so it is safe to put in DDL.
#pragma warning disable EF1002
            await engine.Database.ExecuteSqlRawAsync($"DROP TABLE IF EXISTS {name}", ct);
#pragma warning restore EF1002

            dropped.Add(name);
        }

        return dropped;
    }
}
