using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Npgsql;

namespace Modbot.Analytics.Messages;

/// <summary>
/// Creates the monthly partitions <c>discord_message</c> and <c>discord_message_edit</c> need.
/// </summary>
/// <remarks>
/// <para>
/// The fact log's partitions are made ahead of the clock (<c>EventPartitionMaintainer</c>). Messages
/// cannot work that way: reading back a channel reaches to its first message, which may be years
/// old, so a month's partition is made the first time a message from that month is stored. Both
/// tables get the month together, because retention drops them together.
/// </para>
/// <para>
/// The names are the contract with <c>RetentionPruner</c>, which finds the months to drop by name,
/// exactly as it does for the fact log.
/// </para>
/// </remarks>
public sealed partial class MessagePartitionMaintainer
{
    public const string MessageTable = "discord_message";
    public const string EditTable = "discord_message_edit";

    /// <summary>Serialises creation across processes, like the fact log's maintenance lock.</summary>
    private const long LockKey = 0x4D4F44_4D534750; // "MOD" "MSGP"

    /// <summary>
    /// Months already known to exist, per database, across every instance in this process. A read-back stores
    /// thousands of pages from the same few months; asking the catalogue each time is wasted work.
    /// Cleared when retention drops a month, so a month dropped and needed again is made again.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> Known = new(StringComparer.Ordinal);

    private readonly ModbotContext _db;

    public MessagePartitionMaintainer(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public static string PartitionName(string table, DateTimeOffset instant)
        => table + "_" + instant.UtcDateTime.ToString("yyyy_MM", CultureInfo.InvariantCulture);

    /// <summary>Ensures both tables have a partition for the month of every instant given.</summary>
    public async Task EnsureForAsync(IEnumerable<DateTimeOffset> instants, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instants);

        foreach (var month in instants.Select(FirstOfMonth).Distinct())
        {
            var key = _db.Database.GetDbConnection().Database + "/" + PartitionName(MessageTable, month);

            if (Known.ContainsKey(key))
                continue;

            await CreateAsync(MessageTable, month, ct).ConfigureAwait(false);
            await CreateAsync(EditTable, month, ct).ConfigureAwait(false);

            Known[key] = true;
        }
    }

    /// <summary>Forgets what is known to exist, so the next store checks again. For retention and tests.</summary>
    public static void Forget() => Known.Clear();

    private async Task CreateAsync(string table, DateTimeOffset month, CancellationToken ct)
    {
        var name = PartitionName(table, month);

        // Interpolated into DDL, where an identifier cannot be a parameter, so checked.
        if (!SafeName().IsMatch(name))
            throw new InvalidOperationException($"Refusing to create a partition named '{name}'.");

        if (await ExistsAsync(name, ct).ConfigureAwait(false))
            return;

        var from = month.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var to = month.AddMonths(1).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;

        try
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockKey})", ct)
                .ConfigureAwait(false);

            if (!await ExistsAsync(name, ct).ConfigureAwait(false))
            {
                // EF1002: the name is checked above and the bounds are formatted from a date.
#pragma warning disable EF1002
                await _db.Database.ExecuteSqlRawAsync(
                    $"""
                    CREATE TABLE IF NOT EXISTS {name}
                        PARTITION OF {table}
                        FOR VALUES FROM ('{from}+00') TO ('{to}+00')
                    """,
                    ct).ConfigureAwait(false);
#pragma warning restore EF1002
            }

            if (transaction is not null)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.DuplicateTable)
        {
            // Somebody else made it; that is all anyone wanted.
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Task<bool> ExistsAsync(string name, CancellationToken ct)
        => _db.Database
            .SqlQuery<bool>($"SELECT to_regclass({name}) IS NOT NULL AS \"Value\"")
            .SingleAsync(ct);

    private static DateTimeOffset FirstOfMonth(DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    [GeneratedRegex(@"^discord_message(_edit)?_\d{4}_\d{2}$")]
    private static partial Regex SafeName();
}
