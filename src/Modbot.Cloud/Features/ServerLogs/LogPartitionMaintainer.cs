using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Engine;
using Npgsql;

namespace Modbot.Cloud.Features.ServerLogs;

/// <summary>
/// Makes the monthly partitions <c>server_log</c> writes into, ahead of Cloud's own clock.
/// </summary>
/// <remarks>
/// <para>
/// The partition key is <c>received_at</c>, which is Cloud's clock, so months can be made ahead of
/// time and a batch never arrives for a month that does not exist. Two months ahead and one behind,
/// the same window the server's fact log uses.
/// </para>
/// <para>
/// The names are the contract with <see cref="LogRetention"/>, which finds the months to drop by
/// reading them.
/// </para>
/// </remarks>
public sealed partial class LogPartitionMaintainer(EngineContext engine, TimeProvider time)
{
    public const string Table = "server_log";

    public const int MonthsAhead = 2;
    public const int MonthsBehind = 1;

    /// <summary>Serialises creation across processes.</summary>
    private const long LockKey = 0x434C4F_4C4F4753; // "CLO" "LOGS"

    public static string PartitionName(DateTimeOffset instant) =>
        Table + "_" + instant.UtcDateTime.ToString("yyyy_MM", CultureInfo.InvariantCulture);

    /// <summary>Makes every month from one behind to two ahead of now. Returns the names it made sure of.</summary>
    public async Task<IReadOnlyList<string>> EnsureAsync(CancellationToken ct = default)
    {
        var month = FirstOfMonth(time.GetUtcNow()).AddMonths(-MonthsBehind);
        var made = new List<string>();

        for (var i = 0; i <= MonthsBehind + MonthsAhead; i++)
        {
            made.Add(await CreateAsync(month, ct).ConfigureAwait(false));
            month = month.AddMonths(1);
        }

        return made;
    }

    private async Task<string> CreateAsync(DateTimeOffset month, CancellationToken ct)
    {
        var name = PartitionName(month);

        // Interpolated into DDL, where an identifier cannot be a parameter, so checked.
        if (!SafeName().IsMatch(name))
            throw new InvalidOperationException($"Refusing to create a partition named '{name}'.");

        if (await ExistsAsync(name, ct).ConfigureAwait(false))
            return name;

        var from = month.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var to = month.AddMonths(1).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var ownsTransaction = engine.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await engine.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;

        try
        {
            await engine.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockKey})", ct)
                .ConfigureAwait(false);

            if (!await ExistsAsync(name, ct).ConfigureAwait(false))
            {
                // EF1002: the name is checked above and the bounds are formatted from a date.
#pragma warning disable EF1002
                await engine.Database.ExecuteSqlRawAsync(
                    $"""
                    CREATE TABLE IF NOT EXISTS {name}
                        PARTITION OF {Table}
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

        return name;
    }

    private Task<bool> ExistsAsync(string name, CancellationToken ct) =>
        engine.Database
            .SqlQuery<bool>($"SELECT to_regclass({name}) IS NOT NULL AS \"Value\"")
            .SingleAsync(ct);

    private static DateTimeOffset FirstOfMonth(DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    [GeneratedRegex(@"^server_log_\d{4}_\d{2}$")]
    private static partial Regex SafeName();
}
