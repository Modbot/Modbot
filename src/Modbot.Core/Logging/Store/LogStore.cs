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

    public static bool IsValidRetention(int days) => days is >= 0 and <= MaxRetentionDays;

    /// <summary>
    /// Deletes log lines past the retention window. Returns how many went.
    /// </summary>
    /// <remarks>
    /// A delete rather than a dropped partition, which is how the fact log does it (spec 5.5). The
    /// fact log runs to hundreds of millions of rows and a mass delete there would never finish;
    /// this table is thousands of rows a day with outbound API traffic left out, so six months of it
    /// is small enough that a sliced delete is the simpler thing that works.
    /// </remarks>
    public static async Task<long> PruneAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var days = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => (int?)s.LogRetentionDays)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? DefaultRetentionDays;

        if (days <= 0)
            return 0;

        var cutoff = now.AddDays(-days);
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
}
