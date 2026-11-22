using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Repairs;

/// <summary>
/// What the false leave repair would remove, counted without changing anything.
/// </summary>
/// <param name="AlreadyRan">True once the repair has run. Running it again is refused.</param>
/// <param name="WindowStart">Leave facts noticed before this are never touched.</param>
/// <param name="LeaveFactsInWindow">Every inferred "left the group" fact noticed on or after the window start.</param>
/// <param name="Qualifying">Of those, the ones that are false: the person is a current member who joined before the fact says they left.</param>
/// <param name="LinkedFacts">Rows saying a qualifying fact belonged to one decision.</param>
/// <param name="EventReports">Rows saying which clients reported a qualifying fact.</param>
/// <param name="Days">Days whose daily totals would be worked out again.</param>
/// <param name="SampleSubjects">A few of the people affected, for a spot check.</param>
public sealed record FalseLeavePreview(
    bool AlreadyRan,
    DateTimeOffset WindowStart,
    int LeaveFactsInWindow,
    int Qualifying,
    int LinkedFacts,
    int EventReports,
    int Days,
    IReadOnlyList<string> SampleSubjects);

/// <summary>What a run of the false leave repair did.</summary>
/// <param name="Ran">False when the repair had already run: it refuses, and nothing was changed.</param>
/// <param name="FactsDeleted">Leave facts removed.</param>
/// <param name="LinkedFactsDeleted">Linked fact rows removed with them.</param>
/// <param name="EventReportsDeleted">Event report rows removed with them.</param>
/// <param name="DaysRecomputed">Days whose daily totals were worked out again.</param>
/// <param name="BackupTables">The tables holding a copy of everything removed.</param>
public sealed record FalseLeaveRepairResult(
    bool Ran,
    int FactsDeleted,
    int LinkedFactsDeleted,
    int EventReportsDeleted,
    int DaysRecomputed,
    IReadOnlyList<string> BackupTables);

/// <summary>
/// One-off repair of the false "left the group" facts the member sync wrote on 2026-11-21.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What happened.</strong> A member list that came back short was read as the whole list, and
/// about 1,270 real members of the group were marked as left, each with a "left the group" fact. The
/// sync is fixed. Once the fixed build has swept, those people are current members again
/// (<c>group_member.left_at</c> cleared), but the false facts stay in their history and spike that
/// day's leave totals. Facts never change (spec 5.2), so this is the one recorded exception: a fact
/// the sync wrote in error is removed, with a copy kept, and only here.
/// </para>
/// <para>
/// <strong>Which facts.</strong> Type <see cref="FactType.MemberLeft"/>, source
/// <see cref="FactSource.SyncDiff"/>, noticed on or after <see cref="WindowStart"/>, whose person is a
/// current member of that group and whose <c>joined_at</c> is before the moment the fact says they
/// left. Someone who really left and came back has a newer join date and keeps their fact, and so
/// does anyone whose leave came from VRChat's audit log.
/// </para>
/// <para>
/// <strong>Once only.</strong> The three backup tables are the record that it ran: they are made by
/// the run itself, and a run that finds them refuses. Nothing else stores that it ran.
/// </para>
/// <para>
/// <strong>Undo.</strong> Put the facts back from the copy, then make the daily totals look at that
/// day again (restored facts keep their old noticed-at time, which the incremental run would skip):
/// <code>
/// INSERT INTO modbot_event SELECT * FROM modbot_event_false_leave_repair;
/// INSERT INTO modbot_linked_fact SELECT * FROM modbot_linked_fact_false_leave_repair;
/// INSERT INTO modbot_event_report SELECT * FROM modbot_event_report_false_leave_repair;
/// UPDATE modbot_daily_totals_state SET observed_through = '2026-11-20 00:00:00+00' WHERE id = 1;
/// </code>
/// Then drop the three backup tables, which also lets the repair run again. Discord leave alerts the
/// false facts set off were already sent and are not undone.
/// </para>
/// </remarks>
public sealed class FalseLeaveRepair
{
    /// <summary>The sync bug's day. Leave facts noticed before it are not this bug's.</summary>
    public static readonly DateTimeOffset WindowStart = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    public const string FactsBackupTable = "modbot_event_false_leave_repair";
    public const string LinkedFactsBackupTable = "modbot_linked_fact_false_leave_repair";
    public const string EventReportsBackupTable = "modbot_event_report_false_leave_repair";

    /// <summary>Keeps two runs from racing: the second waits, then finds the backup tables and refuses.</summary>
    private const long RepairLockKey = 0x46414C_4C4541; // "FAL" "LEA"

    private const int SampleSize = 10;

    private static readonly string WindowStartSql =
        WindowStart.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "+00";

    // Built from constants only (no request data), so it is written into the SQL as text: a
    // CREATE TABLE ... AS statement and the counting queries then share one definition of "false".
    private static readonly string Qualifying = $"""
        FROM modbot_event e
        JOIN group_member m
          ON m.group_id = e.data->>'groupId' AND m.user_id = e.subject_id
        WHERE e.type = '{FactType.MemberLeft}'
          AND e.source = {(short)FactSource.SyncDiff}
          AND e.subject_platform = {(short)FactPlatform.VRChat}
          AND e.observed_at >= TIMESTAMPTZ '{WindowStartSql}'
          AND m.left_at IS NULL
          AND m.joined_at < e.occurred_at
        """;

    private readonly ModbotContext _db;
    private readonly DailyTotalsJob _dailyTotals;
    private readonly ILogger<FalseLeaveRepair> _log;

    public FalseLeaveRepair(ModbotContext db, DailyTotalsJob dailyTotals, ILogger<FalseLeaveRepair> log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dailyTotals);
        ArgumentNullException.ThrowIfNull(log);

        _db = db;
        _dailyTotals = dailyTotals;
        _log = log;
    }

    /// <summary>Counts what <see cref="RunAsync"/> would remove. Changes nothing.</summary>
    public async Task<FalseLeavePreview> PreviewAsync(CancellationToken ct = default)
    {
        var alreadyRan = await TableExistsAsync(FactsBackupTable, ct);

        var inWindow = await CountAsync(
            $"""
            SELECT COUNT(*)::int AS "Value"
            FROM modbot_event e
            WHERE e.type = '{FactType.MemberLeft}'
              AND e.source = {(short)FactSource.SyncDiff}
              AND e.observed_at >= TIMESTAMPTZ '{WindowStartSql}'
            """,
            ct);

        var ids = await QueryAsync<long>($"""SELECT e.id AS "Value" {Qualifying} ORDER BY e.occurred_at, e.id""", ct);

        var linked = await CountAsync(
            $"""
            WITH picked AS (SELECT e.id, e.occurred_at {Qualifying})
            SELECT COUNT(*)::int AS "Value"
            FROM modbot_linked_fact l
            JOIN picked p ON l.fact_id = p.id AND l.occurred_at = p.occurred_at
            """,
            ct);

        var reports = await CountAsync(
            $"""
            WITH picked AS (SELECT e.id, e.occurred_at {Qualifying})
            SELECT COUNT(*)::int AS "Value"
            FROM modbot_event_report r
            JOIN picked p ON r.fact_id = p.id AND r.occurred_at = p.occurred_at
            """,
            ct);

        var samples = await QueryAsync<string>(
            $"""SELECT e.subject_id AS "Value" {Qualifying} ORDER BY e.occurred_at, e.id LIMIT {SampleSize}""",
            ct);

        var days = await _dailyTotals.DaysTouchedByFactsAsync(ids, ct);

        return new FalseLeavePreview(
            alreadyRan, WindowStart, inWindow, ids.Count, linked, reports, days.Count, samples);
    }

    /// <summary>
    /// Saves a copy of the false facts, removes them, and works the daily totals out again for the
    /// days they touched. One transaction, and only once.
    /// </summary>
    public async Task<FalseLeaveRepairResult> RunAsync(CancellationToken ct = default)
    {
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({RepairLockKey})", ct);

            string[] backups = [FactsBackupTable, LinkedFactsBackupTable, EventReportsBackupTable];

            if (await TableExistsAsync(FactsBackupTable, ct))
            {
                _log.LogWarning("False leave repair refused: it has already run");

                return new FalseLeaveRepairResult(false, 0, 0, 0, 0, backups);
            }

            // The copy first, and the facts it holds are what the rest works from: once they are
            // deleted nothing could be asked about them, so every later step joins to the copy.
            await ExecuteAsync($"CREATE TABLE {FactsBackupTable} AS SELECT e.* {Qualifying}", ct);

            await ExecuteAsync(
                $"""
                CREATE TABLE {LinkedFactsBackupTable} AS
                SELECT l.* FROM modbot_linked_fact l
                JOIN {FactsBackupTable} b ON l.fact_id = b.id AND l.occurred_at = b.occurred_at
                """,
                ct);

            await ExecuteAsync(
                $"""
                CREATE TABLE {EventReportsBackupTable} AS
                SELECT r.* FROM modbot_event_report r
                JOIN {FactsBackupTable} b ON r.fact_id = b.id AND r.occurred_at = b.occurred_at
                """,
                ct);

            // Before the delete: afterwards there is nothing left to ask. An imprecise fact belongs
            // to every day its window covers, which the daily totals job decides.
            var ids = await QueryAsync<long>($"""SELECT id AS "Value" FROM {FactsBackupTable}""", ct);
            var days = await _dailyTotals.DaysTouchedByFactsAsync(ids, ct);

            // The same way the user purge does it: the rows that name a fact go with it, and the
            // facts are matched on (id, occurred_at) because the table is partitioned by the second.
            var linked = await ExecuteAsync(
                $"""
                DELETE FROM modbot_linked_fact l USING {FactsBackupTable} b
                WHERE l.fact_id = b.id AND l.occurred_at = b.occurred_at
                """,
                ct);

            var reports = await ExecuteAsync(
                $"""
                DELETE FROM modbot_event_report r USING {FactsBackupTable} b
                WHERE r.fact_id = b.id AND r.occurred_at = b.occurred_at
                """,
                ct);

            var facts = await ExecuteAsync(
                $"""
                DELETE FROM modbot_event e USING {FactsBackupTable} b
                WHERE e.id = b.id AND e.occurred_at = b.occurred_at
                """,
                ct);

            // Deleting does not move observed_at, so the incremental run would never look at these
            // days again.
            if (days.Count > 0)
                await _dailyTotals.RecomputeDaysAsync(days, ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);

            _log.LogInformation(
                "False leave repair ran: {Facts} facts, {Linked} linked fact rows and {Reports} event report rows removed, {Days} days recalculated; copies are in {Backups}",
                facts, linked, reports, days.Count, string.Join(", ", backups));

            return new FalseLeaveRepairResult(true, facts, linked, reports, days.Count, backups);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private async Task<bool> TableExistsAsync(string table, CancellationToken ct)
        => (await QueryAsync<bool>($"""SELECT to_regclass('{table}') IS NOT NULL AS "Value" """, ct))[0];

    private async Task<int> CountAsync(string sql, CancellationToken ct)
        => (await QueryAsync<int>(sql, ct))[0];

    // EF1002 is suppressed on the two helpers below: every statement is assembled from constants in
    // this file (table names, a fixed type, a fixed date) and takes no value from a request.

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct)
    {
#pragma warning disable EF1002
        return await _db.Database.ExecuteSqlRawAsync(sql, ct);
#pragma warning restore EF1002
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, CancellationToken ct)
    {
#pragma warning disable EF1002
        return await _db.Database.SqlQueryRaw<T>(sql).ToListAsync(ct);
#pragma warning restore EF1002
    }
}
