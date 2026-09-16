using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;
using Modbot.Cloud.Engine;

namespace Modbot.Cloud.Features.InstanceLogs;

/// <summary>
/// Drops whole months of <c>instance_log</c> once every line in them is past the window.
/// </summary>
/// <remarks>
/// <para>
/// A dropped partition, not a delete. The events beside these are a few dozen an hour per client and
/// a sliced delete clears them; log lines are hundreds of times more numerous, and deleting six
/// months of them would leave a table twice its own size in dead rows and an autovacuum that never
/// catches up. The monthly partitioning exists for this one job.
/// </para>
/// <para>
/// A month is only dropped when its <em>upper</em> bound is past the cutoff, so nothing is ever
/// removed early: a few extra weeks of the oldest month survive rather than lines being picked out
/// one at a time.
/// </para>
/// </remarks>
public sealed class LogRetention(EngineContext engine, CloudContext cloud, TimeProvider time)
{
    /// <summary>Months dropped, by name.</summary>
    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default)
    {
        var settings = await cloud.GetSettingsAsync(ct).ConfigureAwait(false);

        if (settings.LogKeepDays <= 0)
            return [];

        var cutoff = time.GetUtcNow().AddDays(-settings.LogKeepDays);
        var dropped = new List<string>();

        foreach (var name in await PartitionsAsync(ct).ConfigureAwait(false))
        {
            if (Month(name) is not { } month || month.AddMonths(1) > cutoff)
                continue;

            // EF1002: the name came out of PostgreSQL's own catalogue for this parent table.
#pragma warning disable EF1002
            await engine.Database.ExecuteSqlRawAsync($"DROP TABLE IF EXISTS {name}", ct).ConfigureAwait(false);
#pragma warning restore EF1002

            dropped.Add(name);
        }

        return dropped;
    }

    /// <summary>Every partition of <c>instance_log</c>, read from the catalogue.</summary>
    private Task<List<string>> PartitionsAsync(CancellationToken ct) =>
        engine.Database
            .SqlQuery<string>($"""
                SELECT child.relname AS "Value"
                FROM pg_inherits
                JOIN pg_class parent ON parent.oid = pg_inherits.inhparent
                JOIN pg_class child ON child.oid = pg_inherits.inhrelid
                WHERE parent.relname = {LogPartitionMaintainer.Table}
                """)
            .ToListAsync(ct);

    /// <summary>The month a partition name stands for, or null when the name is not one of ours.</summary>
    private static DateTimeOffset? Month(string name)
    {
        var prefix = LogPartitionMaintainer.Table + "_";

        if (!name.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        return DateTimeOffset.TryParseExact(
            name[prefix.Length..],
            "yyyy_MM",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var month)
            ? month
            : null;
    }
}
