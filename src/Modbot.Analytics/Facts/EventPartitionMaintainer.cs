using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Time;
using Npgsql;

namespace Modbot.Analytics.Facts;

/// <summary>
/// Creates the monthly partitions <c>modbot_event</c> needs before anything can be written to it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a requirement of ingest, not an optimisation.</strong> PostgreSQL rejects an
/// insert whose partition key falls outside every partition, so a month with no partition means
/// every fact for that month is lost -- not degraded, lost -- and the first symptom is the first
/// of the month. That is why partitions are created ahead of time and why this has its own test
/// that walks a fake clock across a month boundary.
/// </para>
/// <para>
/// The migration deliberately creates no partitions: which months exist depends on the clock, and
/// a migration must not read one. Everything here goes through <c>IModbotClock</c> (spec 4.4).
/// </para>
/// </remarks>
public sealed partial class EventPartitionMaintainer
{
    /// <summary>
    /// How far ahead to pre-create. Two months means a deployment whose scheduler has been down
    /// for weeks still ingests, and the cost of an empty partition is a catalogue row.
    /// </summary>
    public const int MonthsAhead = 2;

    /// <summary>
    /// The previous month is covered too, because a sync diff can date a fact to before the
    /// process started and a client reconnecting after an outage can report an old one.
    /// </summary>
    public const int MonthsBehind = 1;

    /// <summary>
    /// Serialises concurrent maintenance runs. <c>CREATE TABLE IF NOT EXISTS</c> still races two
    /// instances into a duplicate-table error, and a Modbot that crash-loops on startup because
    /// two replicas booted together would be a miserable thing to debug.
    /// </summary>
    private const long MaintenanceLockKey = 0x4D4F44_50415254; // "MOD" "PART"

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public EventPartitionMaintainer(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// The partition holding a given instant, named from its <em>UTC</em> month.
    /// </summary>
    /// <remarks>
    /// The retention job (spec 5.5) prunes by dropping whole partitions, and it finds them by
    /// name -- so this naming is a contract between two jobs rather than a detail of one.
    /// </remarks>
    public static string PartitionName(DateTimeOffset instant)
        => "modbot_event_" + instant.UtcDateTime.ToString("yyyy_MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// Ensures the window around now is covered. Returns the partitions it had to create, which
    /// is empty on every run but the first of a month -- worth logging when it is not.
    /// </summary>
    public async Task<IReadOnlyList<string>> EnsureAsync(CancellationToken ct = default)
    {
        var created = new List<string>();
        var first = FirstOfMonth(_clock.UtcNow).AddMonths(-MonthsBehind);

        for (var offset = 0; offset <= MonthsBehind + MonthsAhead; offset++)
        {
            var month = first.AddMonths(offset);
            if (await CreateAsync(month, ct))
                created.Add(PartitionName(month));
        }

        return created;
    }

    /// <summary>
    /// Ensures the partition covering one specific instant exists. For the catch-up and imports,
    /// which reach further back than <see cref="EnsureAsync"/> covers.
    /// </summary>
    public Task<bool> EnsureForAsync(DateTimeOffset instant, CancellationToken ct = default)
        => CreateAsync(FirstOfMonth(instant), ct);

    private async Task<bool> CreateAsync(DateTimeOffset month, CancellationToken ct)
    {
        var name = PartitionName(month);

        // The name is derived from a date and cannot contain anything but digits and
        // underscores, but it is interpolated into DDL -- where an identifier cannot be a
        // parameter -- so it is checked rather than assumed.
        if (!SafePartitionName().IsMatch(name))
            throw new InvalidOperationException($"Refusing to create a partition named '{name}'.");

        if (await ExistsAsync(name, ct))
            return false;

        var from = month.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var to = month.AddMonths(1).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({MaintenanceLockKey})", ct);

            // Re-check under the lock: another instance may have created it while we waited.
            if (await ExistsAsync(name, ct))
            {
                if (transaction is not null)
                    await transaction.CommitAsync(ct);

                return false;
            }

            var sql = $"""
                CREATE TABLE IF NOT EXISTS {name}
                    PARTITION OF modbot_event
                    FOR VALUES FROM ('{from}+00') TO ('{to}+00')
                """;

            // EF1002 is suppressed knowingly. DDL cannot take parameters for an identifier or a
            // partition bound, so this statement has to be assembled as text; `name` is checked
            // against SafePartitionName above and both bounds are formatted from DateTimeOffset
            // with the invariant culture. Nothing here is caller-supplied.
#pragma warning disable EF1002
            await _db.Database.ExecuteSqlRawAsync(sql, ct);
#pragma warning restore EF1002

            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.DuplicateTable)
        {
            // Lost the race to an instance that is not taking our lock -- an older deployment,
            // or a human at psql. The partition exists, which is all anyone wanted.
            return false;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private Task<bool> ExistsAsync(string name, CancellationToken ct)
        => _db.Database
            .SqlQuery<bool>($"SELECT to_regclass({name}) IS NOT NULL AS \"Value\"")
            .SingleAsync(ct);

    /// <summary>Midnight UTC on the first of the instant's UTC month.</summary>
    private static DateTimeOffset FirstOfMonth(DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    [GeneratedRegex(@"^modbot_event_\d{4}_\d{2}$")]
    private static partial Regex SafePartitionName();
}
