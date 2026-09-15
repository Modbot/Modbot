using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Modbot.Cloud.Engine;

/// <summary>
/// Creates the monthly partitions <c>log_line</c> and <c>log_event</c> need before anything can be
/// written to them, and names them for retention to find.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A requirement of ingest.</strong> PostgreSQL refuses an insert that falls outside every
/// partition, so a month without one means every batch that month fails. Partitions are made one
/// month behind to two months ahead, at start and daily. The same approach as the server's
/// <c>EventPartitionMaintainer</c>.
/// </para>
/// <para>
/// The partition key is Cloud's received time, which comes from <see cref="TimeProvider"/>, so the
/// month a line lands in is always one of these (cloud log backup spec 4.3).
/// </para>
/// </remarks>
public sealed partial class PartitionMaintainer(EngineContext db, TimeProvider time)
{
    public const int MonthsAhead = 2;
    public const int MonthsBehind = 1;

    /// <summary>The partitioned tables, in the order they are maintained.</summary>
    public static readonly IReadOnlyList<string> Tables = ["log_line", "log_event"];

    /// <summary>Serialises maintenance across Cloud processes started together.</summary>
    private const long MaintenanceLockKey = 0x434C4F_50415254; // "CLO" "PART"

    /// <summary>The partition of <paramref name="table"/> holding an instant, named from its UTC month.</summary>
    public static string PartitionName(string table, DateTimeOffset instant)
        => $"{table}_{instant.UtcDateTime.ToString("yyyy_MM", CultureInfo.InvariantCulture)}";

    /// <summary>Whether a table name is one this class made, for retention to trust.</summary>
    public static bool IsPartitionName(string table, string name) =>
        Tables.Contains(table) && SafePartitionName().IsMatch(name) && name.StartsWith(table + "_", StringComparison.Ordinal);

    /// <summary>The first instant after a partition's month, or null when the name is not one of ours.</summary>
    public static DateTimeOffset? UpperBound(string table, string name)
    {
        if (!IsPartitionName(table, name))
            return null;

        var suffix = name[(table.Length + 1)..];
        return DateTime.TryParseExact(suffix, "yyyy_MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var month)
            ? new DateTimeOffset(month, TimeSpan.Zero).AddMonths(1)
            : null;
    }

    /// <summary>Ensures the months around now exist for both tables. Returns the partitions created.</summary>
    public async Task<IReadOnlyList<string>> EnsureAsync(CancellationToken ct = default)
    {
        var created = new List<string>();
        var first = FirstOfMonth(time.GetUtcNow()).AddMonths(-MonthsBehind);

        foreach (var table in Tables)
        {
            for (var offset = 0; offset <= MonthsBehind + MonthsAhead; offset++)
            {
                var month = first.AddMonths(offset);
                if (await CreateAsync(table, month, ct))
                    created.Add(PartitionName(table, month));
            }
        }

        return created;
    }

    private async Task<bool> CreateAsync(string table, DateTimeOffset month, CancellationToken ct)
    {
        var name = PartitionName(table, month);

        // Built from a fixed table name and a date, but interpolated into DDL where an identifier
        // cannot be a parameter, so it is checked rather than assumed.
        if (!IsPartitionName(table, name))
            throw new InvalidOperationException($"Refusing to create a partition named '{name}'.");

        if (await ExistsAsync(name, ct))
            return false;

        var from = month.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var to = month.AddMonths(1).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({MaintenanceLockKey})", ct);

            if (await ExistsAsync(name, ct))
            {
                await transaction.CommitAsync(ct);
                return false;
            }

            // EF1002 is suppressed knowingly: DDL cannot take parameters for an identifier or a
            // partition bound. Both come from a fixed list and a DateTimeOffset.
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(
                $"CREATE TABLE IF NOT EXISTS {name} PARTITION OF {table} FOR VALUES FROM ('{from}+00') TO ('{to}+00')",
                ct);
#pragma warning restore EF1002

            await transaction.CommitAsync(ct);
            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.DuplicateTable)
        {
            return false;
        }
    }

    private Task<bool> ExistsAsync(string name, CancellationToken ct)
        => db.Database.SqlQuery<bool>($"SELECT to_regclass({name}) IS NOT NULL AS \"Value\"").SingleAsync(ct);

    private static DateTimeOffset FirstOfMonth(DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    [GeneratedRegex(@"^log_(line|event)_\d{4}_\d{2}$")]
    private static partial Regex SafePartitionName();
}
