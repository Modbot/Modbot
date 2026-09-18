using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;

namespace Modbot.Core.Logging.Store;

/// <summary>
/// The log table's own numbers, and the job that keeps it from growing forever.
/// </summary>
public static class LogStore
{
    /// <summary>Six months. <c>0</c> keeps lines forever.</summary>
    public const int DefaultRetentionDays = 180;

    /// <summary>The longest window an operator may set, so a typo cannot mean "centuries".</summary>
    public const int MaxRetentionDays = 3650;

    /// <summary>
    /// Rows deleted in one statement. Small enough that no single delete holds a lock long enough
    /// to be noticed by a request; repeated until the day's worth is gone.
    /// </summary>
    public const int DeleteSlice = 10_000;

    /// <summary>
    /// The most lines the table holds, whatever the keep-for setting says. Two million is about a
    /// gigabyte at half a kilobyte a line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The keep-for setting is a promise about time, and it was written when the table only ever
    /// held Information and above: a few thousand lines a day, so six months of it was small. Since
    /// the table follows <c>LOG_LEVEL</c> (2026-09-18) an operator can ask it to hold every request
    /// and every SQL statement as well, which is two or three orders of magnitude more, and six
    /// months of <em>that</em> is not a log any more, it is the largest thing in the database.
    /// </para>
    /// <para>
    /// So there is a second, harder limit that nobody can turn off, including the operator who set
    /// keep-for to 0 for "forever". Losing the oldest lines is survivable; a Modbot that has filled
    /// its own database is not, and it would take the group's history down with it.
    /// </para>
    /// </remarks>
    public const long MaxLines = 2_000_000;

    public static bool IsValidRetention(int days) => days is >= 0 and <= MaxRetentionDays;

    /// <summary>
    /// Deletes log lines past the keep-for setting, and then any past <see cref="MaxLines"/>.
    /// </summary>
    /// <remarks>
    /// A delete rather than a dropped partition, which is how the fact log does it (spec 5.5). The
    /// fact log runs to hundreds of millions of rows and a mass delete there would never finish;
    /// this table is thousands of rows a day with outbound API traffic left out, so six months of it
    /// is small enough that a sliced delete is the simpler thing that works.
    /// </remarks>
    public static async Task<LogPrune> PruneAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var days = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => (int?)s.LogRetentionDays)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? DefaultRetentionDays;

        // 0 is "keep forever", which the ceiling below still has the last word on.
        var pastTheWindow = days > 0
            ? await DeleteAsync(db, now.AddDays(-days), ct).ConfigureAwait(false)
            : 0;

        return new LogPrune(pastTheWindow, await TrimToCeilingAsync(db, ct).ConfigureAwait(false));
    }

    /// <summary>Deletes everything written before <paramref name="cutoff"/>, a slice at a time.</summary>
    private static async Task<long> DeleteAsync(ModbotContext db, DateTimeOffset cutoff, CancellationToken ct)
    {
        long removed = 0;

        while (!ct.IsCancellationRequested)
        {
            var deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                DELETE FROM modbot_log
                WHERE ctid IN (SELECT ctid FROM modbot_log WHERE at < {cutoff} LIMIT {DeleteSlice})
                """,
                ct).ConfigureAwait(false);

            removed += deleted;

            if (deleted < DeleteSlice)
                break;
        }

        return removed;
    }

    /// <summary>Deletes the oldest lines until only <see cref="MaxLines"/> are left.</summary>
    /// <remarks>
    /// Found by id rather than by counting the table: the id is written in order, so the id of the
    /// two-millionth-newest line is one index read and everything below it is older. A count would
    /// read every row, and this runs on a table that is being written to the whole time.
    /// </remarks>
    private static async Task<long> TrimToCeilingAsync(ModbotContext db, CancellationToken ct)
    {
        var oldestKept = await db.Logs.AsNoTracking()
            .OrderByDescending(e => e.Id)
            .Select(e => e.Id)
            .Skip((int)MaxLines - 1)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (oldestKept <= 0)
            return 0;

        long removed = 0;

        while (!ct.IsCancellationRequested)
        {
            var deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                DELETE FROM modbot_log
                WHERE ctid IN (SELECT ctid FROM modbot_log WHERE id < {oldestKept} LIMIT {DeleteSlice})
                """,
                ct).ConfigureAwait(false);

            removed += deleted;

            if (deleted < DeleteSlice)
                break;
        }

        return removed;
    }
}

/// <summary>What one run of the daily job deleted, and for which of the two reasons.</summary>
/// <param name="PastTheWindow">Lines older than the keep-for setting.</param>
/// <param name="OverTheCeiling">
/// Lines past <see cref="LogStore.MaxLines"/>. Above zero means the log is being written faster
/// than the keep-for setting prunes it, which is worth saying out loud.
/// </param>
public readonly record struct LogPrune(long PastTheWindow, long OverTheCeiling)
{
    public long Total => PastTheWindow + OverTheCeiling;
}
