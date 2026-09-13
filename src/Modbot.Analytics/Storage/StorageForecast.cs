namespace Modbot.Analytics.Storage;

/// <summary>
/// What Modbot's data actually occupies right now, measured rather than estimated.
/// </summary>
/// <param name="FactBytes">
/// Every partition of the fact table, with its indexes and TOAST. Summed across the partition
/// tree, because the parent table holds no rows of its own -- see <see cref="StorageEstimator"/>.
/// </param>
/// <param name="DailyTotalBytes">The daily totals and their bookkeeping.</param>
/// <param name="FactCount">Rows in the fact log.</param>
/// <param name="OldestFact">When the history starts, or null if there is none.</param>
/// <param name="FactsPerDay">Observed arrival rate over <paramref name="ObservedDays"/>.</param>
/// <param name="ObservedDays">
/// How much history that rate was measured over. It is the honesty of the whole forecast: a rate
/// taken from six hours of a fresh install says nothing about next year.
/// </param>
public sealed record StorageMeasurement(
    long FactBytes,
    long DailyTotalBytes,
    long FactCount,
    DateTimeOffset? OldestFact,
    double FactsPerDay,
    double ObservedDays)
{
    public long TotalBytes => FactBytes + DailyTotalBytes;

    /// <summary>
    /// Measured cost of one fact, including its share of every index.
    /// </summary>
    /// <remarks>
    /// Deliberately derived from real bytes rather than added up from column widths. Index
    /// overhead, page fill and row headers are most of the difference between the two, and the
    /// column-width answer is always the wrong one.
    /// </remarks>
    public double BytesPerFact => FactCount == 0 ? 0 : (double)FactBytes / FactCount;
}

/// <summary>
/// How much the forecast should be trusted, which is a function of how long Modbot has been
/// watching rather than of anything about the arithmetic.
/// </summary>
public enum ForecastConfidence
{
    /// <summary>Too little history to extrapolate. No horizons are produced at all.</summary>
    Insufficient,

    /// <summary>Enough to be indicative. A single busy weekend still moves it a lot.</summary>
    Low,

    /// <summary>A month or more of observation, spanning ordinary weekly rhythm.</summary>
    Good,
}

/// <param name="Months">How far out, from today.</param>
/// <param name="EstimatedBytes">Total size at that point if the observed rate continues.</param>
/// <param name="MonthlyCost">
/// Cost of that much storage per month, when the operator has told Modbot their per-GB price.
/// </param>
public sealed record StorageHorizon(int Months, long EstimatedBytes, decimal? MonthlyCost);

/// <summary>
/// The answer to "what does keeping everything cost me?", which is the question an operator has to
/// be able to answer before choosing a retention window is a decision rather than a guess
/// (spec 5.5).
/// </summary>
/// <param name="CapacityExhausted">
/// When the operator's disk fills at the current rate. Null when they gave no capacity, or when
/// the rate is too low to ever reach it.
/// </param>
public sealed record StorageForecast(
    StorageMeasurement Measurement,
    ForecastConfidence Confidence,
    IReadOnlyList<StorageHorizon> Horizons,
    DateTimeOffset? CapacityExhausted);

/// <summary>
/// What the operator told Modbot about their hosting, so the estimate can be stated in their
/// terms instead of in gigabytes.
/// </summary>
/// <param name="CostPerGbMonth">For hosted deployments, e.g. 0.25m.</param>
/// <param name="CapacityBytes">For home hosting: the disk it has to fit on.</param>
/// <remarks>
/// Both optional and independent. An operator who provides neither still gets sizes, which is the
/// most important number on the page.
/// </remarks>
public sealed record StorageBudget(decimal? CostPerGbMonth = null, long? CapacityBytes = null);
