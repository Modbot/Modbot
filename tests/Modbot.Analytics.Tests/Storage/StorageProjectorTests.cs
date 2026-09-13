using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Storage;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Storage;

/// <summary>
/// Spec 5.5: an operator gets no default retention window, so they have to be shown what keeping
/// everything actually costs before choosing one is a decision rather than a guess.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class StorageProjectorTests : AnalyticsTestBase
{
    public StorageProjectorTests(PostgresFixture fixture) : base(fixture) { }

    private StorageProjector NewProjector(Core.Data.ModbotContext context) => new(context, Clock);

    /// <summary>
    /// Writes <paramref name="count"/> facts as though they arrived <paramref name="daysAgo"/>
    /// days ago, by moving the clock the writer stamps <c>observed_at</c> from.
    /// </summary>
    private async Task WriteAgedAsync(int count, double daysAgo)
    {
        var real = Clock.UtcNow;
        Clock.UtcNow = real.AddDays(-daysAgo);

        var when = Clock.UtcNow;
        await WriteAsync(Enumerable
            .Range(0, count)
            .Select(i => Fact(FactType.InstanceJoined, when, subjectId: $"usr_{i}"))
            .ToArray());

        Clock.UtcNow = real;
    }

    /// <summary>
    /// The landmine this class exists around: <c>pg_total_relation_size</c> on a partitioned
    /// parent returns zero no matter how many rows the table holds, because the parent is an empty
    /// routing shell and every byte is in a child.
    /// </summary>
    /// <remarks>
    /// Measured the obvious way, a group with years of history would be told it was using 0 bytes
    /// and that keeping everything is free -- which is the one wrong answer this whole feature
    /// exists to prevent. This test fails against that implementation and passes against the
    /// <c>pg_partition_tree</c> one.
    /// </remarks>
    [Fact]
    public async Task SizeIsSummedAcrossPartitionsRatherThanReadFromTheParent()
    {
        await WriteAgedAsync(500, daysAgo: 5);

        await using var context = Database.NewContext();
        var measurement = await NewProjector(context).MeasureAsync(Ct);

        Assert.Equal(500, measurement.FactCount);
        Assert.True(
            measurement.FactBytes > 0,
            "The fact table measured 0 bytes with 500 rows in it, which is what reading the "
            + "partitioned parent's own size reports.");
    }

    /// <summary>
    /// Once the planner has an estimate, that is what gets used -- and it has to be close.
    /// </summary>
    /// <remarks>
    /// Every other test here takes the exact-count fallback, because a table created seconds ago
    /// has no statistics. This one runs <c>ANALYZE</c> first so the branch that actually runs in
    /// production is the branch under test. The tolerance is loose on purpose: <c>reltuples</c>
    /// is an estimate and is allowed to be off. It is not allowed to be absent, zero, or an order
    /// of magnitude out, and all three are what a broken catalogue join would produce.
    /// </remarks>
    [Fact]
    public async Task ThePlannerEstimateIsUsedOnceItExists()
    {
        await WriteAgedAsync(5_000, daysAgo: 5);

        await using var context = Database.NewContext();
        await context.Database.ExecuteSqlRawAsync("ANALYZE modbot_event", Ct);

        var measurement = await NewProjector(context).MeasureAsync(Ct);

        Assert.InRange(measurement.FactCount, 4_500, 5_500);
    }

    /// <summary>
    /// The figure the whole projection rests on. It is the reason this is measured rather than
    /// estimated: adding up column widths misses index overhead, row headers and page fill, which
    /// together are most of the real cost.
    /// </summary>
    [Fact]
    public async Task BytesPerFactIsDerivedFromRealPages()
    {
        await WriteAgedAsync(2_000, daysAgo: 5);

        await using var context = Database.NewContext();
        var measurement = await NewProjector(context).MeasureAsync(Ct);

        // Bounds, not a target. A fact is a handful of short strings and some fixed-width columns
        // across four indexes, so it cannot plausibly be under a hundred bytes or over four
        // kilobytes -- and either of those would mean the sum is measuring the wrong relations.
        Assert.InRange(measurement.BytesPerFact, 100, 4_000);
    }

    /// <summary>
    /// A young install's rate is divided by how long it has been collecting, not by the width of
    /// the window.
    /// </summary>
    /// <remarks>
    /// Dividing by the full thirty days would report a ten-day-old deployment's traffic at a third
    /// of the truth, and it would keep understating it every day for the rest of the month --
    /// exactly while the operator is deciding whether to turn retention on.
    /// </remarks>
    [Fact]
    public async Task TheRateIsMeasuredOverTimeCollectedNotTheWholeWindow()
    {
        await WriteAgedAsync(1_000, daysAgo: 10);

        await using var context = Database.NewContext();
        var measurement = await NewProjector(context).MeasureAsync(Ct);

        Assert.Equal(10, measurement.ObservedDays, precision: 1);
        Assert.Equal(100, measurement.FactsPerDay, precision: 0);
    }

    /// <summary>
    /// An install collecting for hours gets no projection at all.
    /// </summary>
    /// <remarks>
    /// A number on a screen gets believed regardless of the caveat printed next to it, so the
    /// honest output here is no number. Extrapolating an evening's traffic to twenty-four months
    /// produces a figure that is wrong by an order of magnitude in whichever direction the evening
    /// happened to go.
    /// </remarks>
    [Fact]
    public async Task AFreshInstallIsNotExtrapolated()
    {
        await WriteAgedAsync(400, daysAgo: 0.2);

        await using var context = Database.NewContext();
        var forecast = await NewProjector(context).ForecastAsync(new StorageBudget(), Ct);

        Assert.Equal(ForecastConfidence.Insufficient, forecast.Confidence);
        Assert.Empty(forecast.Horizons);
        Assert.Equal(0, forecast.Measurement.FactsPerDay);

        // The size is still real and still reported -- it is the extrapolation that is withheld.
        Assert.Equal(400, forecast.Measurement.FactCount);
        Assert.True(forecast.Measurement.TotalBytes > 0);
    }

    /// <summary>
    /// Thirty days of observation is the line, and it is where the rate window saturates:
    /// <see cref="StorageMeasurement.ObservedDays"/> counts days <em>inside</em> that window, so
    /// it reaches 30 exactly once history is at least that old and never exceeds it.
    /// </summary>
    [Fact]
    public async Task AMonthOfHistoryIsEnoughToBeTrusted()
    {
        await WriteAgedAsync(300, daysAgo: 35);
        await WriteAgedAsync(300, daysAgo: 1);

        await using var context = Database.NewContext();
        var forecast = await NewProjector(context).ForecastAsync(new StorageBudget(), Ct);

        Assert.Equal(ForecastConfidence.Good, forecast.Confidence);
        Assert.Equal([6, 12, 24], forecast.Horizons.Select(h => h.Months));

        // Monotonic, and strictly growing: a forecast where next year costs the same as this one
        // is not a forecast.
        Assert.True(forecast.Horizons[0].ProjectedBytes < forecast.Horizons[1].ProjectedBytes);
        Assert.True(forecast.Horizons[1].ProjectedBytes < forecast.Horizons[2].ProjectedBytes);
    }

    /// <summary>
    /// The hosted operator's input: a per-GB price, turned into the number they are actually
    /// deciding about.
    /// </summary>
    [Fact]
    public async Task APerGbPriceBecomesAMonthlyCost()
    {
        await using var context = Database.NewContext();

        // Ten days observed, so the forecast is produced; 2 GB now, growing by 1 GB a month.
        var measurement = new StorageMeasurement(
            FactBytes: 2L * 1024 * 1024 * 1024,
            RollupBytes: 0,
            FactCount: 1_000_000,
            OldestFact: Clock.UtcNow.AddDays(-10),
            FactsPerDay: 100_000,
            ObservedDays: 10);

        var forecast = NewProjector(context).Project(measurement, new StorageBudget(CostPerGbMonth: 0.25m));

        var year = forecast.Horizons.Single(h => h.Months == 12);
        var gb = year.ProjectedBytes / (1024d * 1024 * 1024);

        Assert.NotNull(year.MonthlyCost);
        Assert.Equal((decimal)gb * 0.25m, year.MonthlyCost.Value, precision: 1);

        await Task.CompletedTask;
    }

    /// <summary>The home operator's input: a disk, turned into the date it fills.</summary>
    [Fact]
    public async Task ADiskCapacityBecomesTheDateItFills()
    {
        await using var context = Database.NewContext();

        // 1 GB used, 10 MB a day arriving, on a 2 GB disk: a little under 100 days of headroom.
        const long gb = 1024L * 1024 * 1024;
        var measurement = new StorageMeasurement(
            FactBytes: gb,
            RollupBytes: 0,
            FactCount: 1_000_000,
            OldestFact: Clock.UtcNow.AddDays(-40),
            FactsPerDay: 1_000_000 / 40d,
            ObservedDays: 40);

        var forecast = NewProjector(context).Project(measurement, new StorageBudget(CapacityBytes: 2 * gb));

        Assert.NotNull(forecast.CapacityExhausted);
        Assert.True(forecast.CapacityExhausted > Clock.UtcNow);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Past the disk already. "Now" is the truthful answer, and the one that gets acted on.
    /// </summary>
    [Fact]
    public async Task ADiskAlreadyFullSaysSoRatherThanProjectingBackwards()
    {
        await using var context = Database.NewContext();

        const long gb = 1024L * 1024 * 1024;
        var measurement = new StorageMeasurement(
            FactBytes: 3 * gb,
            RollupBytes: 0,
            FactCount: 1_000,
            OldestFact: Clock.UtcNow.AddDays(-40),
            FactsPerDay: 25,
            ObservedDays: 40);

        var forecast = NewProjector(context).Project(measurement, new StorageBudget(CapacityBytes: 2 * gb));

        Assert.Equal(Clock.UtcNow, forecast.CapacityExhausted);

        await Task.CompletedTask;
    }

    /// <summary>
    /// A group that has stopped generating facts never fills the disk, and must not be handed a
    /// date computed by dividing by zero.
    /// </summary>
    [Fact]
    public async Task NoGrowthMeansNoExhaustionDate()
    {
        await using var context = Database.NewContext();

        var measurement = new StorageMeasurement(
            FactBytes: 1024,
            RollupBytes: 0,
            FactCount: 10,
            OldestFact: Clock.UtcNow.AddDays(-40),
            FactsPerDay: 0,
            ObservedDays: 40);

        var forecast = NewProjector(context).Project(
            measurement,
            new StorageBudget(CapacityBytes: 1024L * 1024 * 1024));

        Assert.Null(forecast.CapacityExhausted);

        await Task.CompletedTask;
    }

    /// <summary>
    /// An operator who has entered neither a price nor a disk still gets sizes, which are the most
    /// important numbers on the page.
    /// </summary>
    [Fact]
    public async Task SizesAreReportedWithoutAnyBudgetEntered()
    {
        await WriteAgedAsync(500, daysAgo: 20);

        await using var context = Database.NewContext();
        var forecast = await NewProjector(context).ForecastAsync(new StorageBudget(), Ct);

        Assert.NotEmpty(forecast.Horizons);
        Assert.All(forecast.Horizons, h => Assert.Null(h.MonthlyCost));
        Assert.Null(forecast.CapacityExhausted);
        Assert.All(forecast.Horizons, h => Assert.True(h.ProjectedBytes > 0));
    }

    /// <summary>
    /// An empty database is a real state -- it is every deployment on its first day -- and must
    /// not divide by a fact count of zero.
    /// </summary>
    [Fact]
    public async Task AnEmptyDatabaseMeasuresCleanly()
    {
        await using var context = Database.NewContext();
        var forecast = await NewProjector(context).ForecastAsync(
            new StorageBudget(CostPerGbMonth: 0.25m, CapacityBytes: 1024L * 1024 * 1024),
            Ct);

        Assert.Equal(0, forecast.Measurement.FactCount);
        Assert.Equal(0, forecast.Measurement.BytesPerFact);
        Assert.Null(forecast.Measurement.OldestFact);
        Assert.Equal(ForecastConfidence.Insufficient, forecast.Confidence);
        Assert.Empty(forecast.Horizons);
        Assert.Null(forecast.CapacityExhausted);
    }
}
