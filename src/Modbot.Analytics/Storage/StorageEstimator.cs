using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Time;
using Npgsql;

namespace Modbot.Analytics.Storage;

/// <summary>
/// Measures what Modbot's data occupies and projects it forward, so that an operator choosing a
/// retention window (spec 5.5) is choosing against real numbers.
/// </summary>
/// <remarks>
/// <para>
/// Modbot has no default retention period: an unconfigured deployment keeps every fact forever.
/// That is only a defensible default if the operator can see what it costs them, which is what
/// this class exists to tell them. "Keep everything: about 240 MB a year, roughly six cents a
/// month" is a decision someone can make. "Choose a retention period" is not.
/// </para>
/// <para>
/// Everything here is measured. The size comes from the database's own accounting rather than
/// from row counts multiplied by an estimated row width, and the growth rate comes from the fact
/// log rather than from an assumption about group size. Both change as a group grows, and a
/// constant baked in today would be wrong for somebody on the day it shipped.
/// </para>
/// </remarks>
public sealed class StorageEstimator(ModbotContext db, IModbotClock clock)
{
    /// <summary>Mean Gregorian month. Estimates are monthly; days are what accrue.</summary>
    private const double DaysPerMonth = 30.436875;

    /// <summary>Storage is priced and sized in binary gigabytes far more often than decimal ones.</summary>
    private const double BytesPerGb = 1024d * 1024d * 1024d;

    /// <summary>
    /// How far back the arrival rate is measured. Long enough to span the weekly rhythm of a
    /// VRChat group -- weekends are not like Tuesdays -- and short enough that a group which has
    /// grown recently is projected from what it is now rather than from what it was.
    /// </summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromDays(30);

    private static readonly int[] HorizonMonths = [6, 12, 24];

    public async Task<StorageForecast> ForecastAsync(StorageBudget budget, CancellationToken ct)
    {
        var measurement = await MeasureAsync(ct);
        return Estimate(measurement, budget);
    }

    /// <summary>
    /// Asks PostgreSQL how big everything is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fact table's size is summed over <c>pg_partition_tree</c> and not read from the table
    /// itself, and that is not a detail. <c>pg_total_relation_size</c> of a partitioned parent
    /// returns <b>zero</b> however many rows are in it: the parent is an empty routing shell and
    /// every byte lives in a child. Measured directly, a 60 GB history reports as 0 bytes.
    /// </para>
    /// <para>
    /// Sizes include indexes and TOAST, because the operator's disk does too.
    /// </para>
    /// <para>
    /// The row count is the planner's estimate rather than <c>count(*)</c>. PostgreSQL has no
    /// count shortcut, so an exact count is a sequential scan of every partition -- seconds of
    /// I/O on a large history, every time somebody opens the settings page, to refine a ratio
    /// that then feeds a straight-line extrapolation. It falls back to an exact count when the
    /// estimate is missing, which is the case on a table that has never been analysed.
    /// </para>
    /// <para>
    /// That estimate sums <b>leaves only</b>. <c>ANALYZE</c> on a partitioned table also stores
    /// the whole table's row count on the parent, so summing the entire tree counts every row
    /// exactly twice -- and reports a history twice its real size at half its real cost per fact.
    /// The size sum above has no such problem, because the parent genuinely occupies nothing.
    /// </para>
    /// </remarks>
    public async Task<StorageMeasurement> MeasureAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var windowStart = now - RateWindow;

        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText =
            """
            SELECT
                (SELECT coalesce(sum(pg_total_relation_size(relid)), 0)
                   FROM pg_partition_tree('modbot_event'))                     AS fact_bytes,
                coalesce(pg_total_relation_size(to_regclass('modbot_daily_total')), 0)
                  + coalesce(pg_total_relation_size(to_regclass('modbot_daily_totals_state')), 0)
                                                                               AS daily_total_bytes,
                (SELECT coalesce(sum(greatest(c.reltuples, 0)), 0)
                   FROM pg_partition_tree('modbot_event') t
                   JOIN pg_class c ON c.oid = t.relid
                  WHERE t.isleaf)                                              AS estimated_rows,
                (SELECT min(observed_at) FROM modbot_event)                    AS first_observed,
                (SELECT count(*) FROM modbot_event WHERE observed_at >= @from) AS recent_count
            """;

        command.Parameters.Add(new NpgsqlParameter("from", windowStart));

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        var factBytes = reader.GetInt64(0);
        var dailyTotalBytes = reader.GetInt64(1);
        // reltuples is a float, and is -1 on a partition autovacuum has not reached yet -- which
        // on a fresh install is all of them. greatest() in the query floors those at zero, and
        // zero here means "no estimate available", not "no rows".
        var estimatedRows = (long)reader.GetDouble(2);
        DateTimeOffset? firstObserved =
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
        var recentCount = reader.GetInt64(4);

        await reader.CloseAsync();

        var factCount = estimatedRows > 0 ? estimatedRows : await ExactCountAsync(ct);

        // The rate is measured over whichever is shorter: the window, or how long Modbot has
        // actually been collecting. Dividing a two-day-old install's facts by thirty would report
        // a rate fifteen times below the truth, and it would keep doing so for a month.
        var observedDays = firstObserved is null
            ? 0
            : (now - (firstObserved.Value > windowStart ? firstObserved.Value : windowStart)).TotalDays;

        var factsPerDay = observedDays >= 1 ? recentCount / observedDays : 0;

        return new StorageMeasurement(
            factBytes,
            dailyTotalBytes,
            factCount,
            firstObserved,
            factsPerDay,
            observedDays);
    }

    /// <summary>
    /// The fallback when the planner has no estimate yet, which is a table nothing has analysed.
    /// </summary>
    /// <remarks>
    /// Safe precisely because it only runs in that case: a table autovacuum has never reached is
    /// a table that was created recently, and counting it is cheap. Once there is enough history
    /// for the count to hurt, there is an estimate and this never runs again.
    /// </remarks>
    private async Task<long> ExactCountAsync(CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT count(*) FROM modbot_event";
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Extrapolates the measurement forward, and says how much to believe it.
    /// </summary>
    /// <remarks>
    /// A straight line, on purpose. Growth is not linear -- a group that opens more instances
    /// generates more facts per member -- and a model that pretended otherwise would be
    /// confidently wrong in a way a straight line is not. The honest thing is a simple estimate
    /// carrying its observation period alongside it.
    /// </remarks>
    public StorageForecast Estimate(StorageMeasurement measurement, StorageBudget budget)
    {
        var confidence = measurement.ObservedDays switch
        {
            < 1 => ForecastConfidence.Insufficient,
            < 30 => ForecastConfidence.Low,
            _ => ForecastConfidence.Good,
        };

        if (confidence is ForecastConfidence.Insufficient)
        {
            // No horizons rather than horizons nobody should act on. A number on the screen gets
            // believed regardless of the label next to it.
            return new StorageForecast(measurement, confidence, [], CapacityExhausted: null);
        }

        var bytesPerDay = GrowthPerDay(measurement);

        var horizons = HorizonMonths
            .Select(months =>
            {
                var bytes = measurement.TotalBytes + (long)(bytesPerDay * months * DaysPerMonth);
                return new StorageHorizon(months, bytes, MonthlyCost(bytes, budget.CostPerGbMonth));
            })
            .ToArray();

        return new StorageForecast(
            measurement,
            confidence,
            horizons,
            Exhaustion(measurement, bytesPerDay, budget));
    }

    /// <summary>
    /// Facts and daily totals grow on different clocks, so they are estimated on different clocks.
    /// </summary>
    /// <remarks>
    /// Facts accrue per fact. Daily totals accrue per <em>day</em> -- one row per day per dimension,
    /// whether that day saw ten events or ten thousand -- so folding them into a per-fact average
    /// would make a quiet group look like it stores more per fact than a busy one does.
    /// </remarks>
    private double GrowthPerDay(StorageMeasurement measurement)
    {
        var factGrowth = measurement.BytesPerFact * measurement.FactsPerDay;

        var historyDays = measurement.OldestFact is null
            ? 0
            : (clock.UtcNow - measurement.OldestFact.Value).TotalDays;

        var dailyTotalGrowth = historyDays >= 1 ? measurement.DailyTotalBytes / historyDays : 0;

        return factGrowth + dailyTotalGrowth;
    }

    private static decimal? MonthlyCost(long bytes, decimal? costPerGbMonth)
        => costPerGbMonth is null
            ? null
            : Math.Round((decimal)(bytes / BytesPerGb) * costPerGbMonth.Value, 2);

    private DateTimeOffset? Exhaustion(
        StorageMeasurement measurement,
        double bytesPerDay,
        StorageBudget budget)
    {
        if (budget.CapacityBytes is not { } capacity) return null;

        // Already over. "Now" is the truthful answer and the one that gets acted on.
        if (measurement.TotalBytes >= capacity) return clock.UtcNow;

        if (bytesPerDay <= 0) return null;

        var days = (capacity - measurement.TotalBytes) / bytesPerDay;

        // A rate low enough to push the date past the point of absurdity means the same thing as
        // "never", and AddDays throws rather than saturating.
        return days > 36_500 ? null : clock.UtcNow.AddDays(days);
    }
}
