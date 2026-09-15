using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.DailyTotals;

/// <summary>
/// Spec 5.2.1: the counted-only path, for events where the aggregate is the datum and a per-event
/// fact would be volume and a social graph for a query nobody runs.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DailyTotalCounterTests : AnalyticsTestBase
{
    /// <summary>A metric with no fact behind it, counted directly.</summary>
    private const string CountedMetric = "test.counted";

    public DailyTotalCounterTests(PostgresFixture fixture) : base(fixture) { }

    private DailyTotalCounter NewCounter(Core.Data.ModbotContext context) => new(context, Clock);

    [Fact]
    public async Task IncrementsAccumulateWithoutWritingAnyFact()
    {
        await using var context = Database.NewContext();
        var counter = NewCounter(context);

        await counter.IncrementAsync(CountedMetric, "user-1", ct: Ct);
        await counter.IncrementAsync(CountedMetric, "user-1", ct: Ct);
        await counter.IncrementAsync(CountedMetric, "user-2", amount: 5m, ct: Ct);

        var today = DayOf(Clock.UtcNow);

        Assert.Equal(2m, await ValueAsync(today, CountedMetric, "user-1"));
        Assert.Equal(5m, await ValueAsync(today, CountedMetric, "user-2"));

        // The whole point: nothing was written to the fact log.
        Assert.Empty(context.Events);
    }

    [Fact]
    public async Task CountsAgainstTheClockDayUnlessToldOtherwise()
    {
        await using var context = Database.NewContext();
        var counter = NewCounter(context);

        await counter.IncrementAsync(CountedMetric, ct: Ct);
        Clock.Advance(TimeSpan.FromDays(1));
        await counter.IncrementAsync(CountedMetric, ct: Ct);
        await counter.IncrementAsync(CountedMetric, day: DayOf(Start).AddDays(-3), ct: Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(Start), CountedMetric));
        Assert.Equal(1m, await ValueAsync(DayOf(Start).AddDays(1), CountedMetric));
        Assert.Equal(1m, await ValueAsync(DayOf(Start).AddDays(-3), CountedMetric));
    }

    /// <summary>
    /// These rows are the only copy of their data. A rebuild -- the supported fix for every
    /// aggregation bug -- must leave them exactly alone.
    /// </summary>
    [Fact]
    public async Task ARebuildLeavesCountedRowsUntouched()
    {
        await WriteAsync(Fact(FactType.MemberJoined, Start));

        await using var context = Database.NewContext();
        await NewCounter(context).IncrementAsync(CountedMetric, "user-1", 42m, ct: Ct);

        await NewJob(context).RebuildAsync(Ct);

        Assert.Equal(42m, await ValueAsync(DayOf(Start), CountedMetric, "user-1"));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersJoined));
    }

    [Fact]
    public async Task RefusesToCountIntoAMetricTheJobComputes()
    {
        await using var context = Database.NewContext();
        var counter = NewCounter(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => counter.IncrementAsync(DailyTotalMetrics.MembersJoined, ct: Ct));
    }

    /// <summary>
    /// The registry check above can be bypassed by a metric that is added to the job later. The
    /// database is the second line: an increment must never silently add itself to a computed row.
    /// </summary>
    [Fact]
    public async Task RefusesToCountIntoAnExistingComputedRow()
    {
        await WriteAsync(Fact(FactType.MemberJoined, Start));

        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);

        await using (var direct = Database.NewContext())
        {
            // Stand in for a metric that used to be counted and is now computed.
            direct.DailyTotals.Add(new DailyTotal
            {
                Day = DayOf(Start),
                Metric = "some.metric",
                Dimension = string.Empty,
                Value = 7m,
                Origin = DailyTotalOrigin.Computed,
            });

            await direct.SaveChangesAsync(Ct);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewCounter(context).IncrementAsync("some.metric", day: DayOf(Start), ct: Ct));

        Assert.Equal(7m, await ValueAsync(DayOf(Start), "some.metric"));
    }
}
