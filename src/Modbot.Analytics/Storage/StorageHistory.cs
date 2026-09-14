using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Analytics.Storage;

/// <summary>
/// Records how big Modbot's data is, once a day, and reads those days back for the storage chart.
/// </summary>
/// <remarks>
/// <para>
/// One row per day is all the chart needs: it shows a year, and a year is 365 points. Finer
/// samples would cost a measurement each and draw the same line.
/// </para>
/// <para>
/// Recording a day that already has a row replaces that row. That makes a restart, an overlapping
/// run or a manual call harmless: none of them can leave two rows for one day.
/// </para>
/// </remarks>
public sealed class StorageHistory(ModbotContext db, StorageEstimator estimator, IModbotClock clock)
{
    public DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    public Task<bool> IsTodayRecordedAsync(CancellationToken ct)
    {
        var today = Today;
        return db.StorageDays.AsNoTracking().AnyAsync(d => d.Day == today, ct);
    }

    /// <summary>Measures now and writes the result as today's row, replacing any already there.</summary>
    public async Task RecordTodayAsync(CancellationToken ct)
    {
        var measurement = await estimator.MeasureAsync(ct);
        var today = Today;

        // An upsert in one statement rather than read-then-write, so two runs racing on the same
        // day cannot both decide the row is missing and collide on the key.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO modbot_storage_day (day, bytes, facts)
            VALUES ({today}, {measurement.TotalBytes}, {measurement.FactCount})
            ON CONFLICT (day) DO UPDATE SET bytes = excluded.bytes, facts = excluded.facts
            """,
            ct);
    }

    /// <summary>Every recorded day from <paramref name="from"/> onward, oldest first.</summary>
    public async Task<IReadOnlyList<StorageDay>> SinceAsync(DateOnly from, CancellationToken ct)
        => await db.StorageDays
            .AsNoTracking()
            .Where(d => d.Day >= from)
            .OrderBy(d => d.Day)
            .ToListAsync(ct);
}
