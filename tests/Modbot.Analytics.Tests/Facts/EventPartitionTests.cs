using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Facts;

/// <summary>
/// Spec 5.7.2: monthly range partitions, so retention pruning is a partition drop rather than a
/// mass DELETE. The consequence the plan flagged is that an insert with no matching partition
/// *fails* -- partition creation is a hard requirement of ingest, not an optimisation.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventPartitionTests : FactTestBase
{
    public EventPartitionTests(PostgresFixture db) : base(db) { }

    private static readonly DateTimeOffset Quiet = new(2031, 4, 10, 9, 0, 0, TimeSpan.Zero);

    private async Task<bool> PartitionExistsAsync(DateTimeOffset month, CancellationToken ct)
    {
        await using var context = Db.NewContext();
        var name = EventPartitionMaintainer.PartitionName(month);

        return await context.Database
            .SqlQuery<bool>($"SELECT to_regclass({name}) IS NOT NULL AS \"Value\"")
            .SingleAsync(ct);
    }

    [Fact]
    public async Task EnsureAsync_CoversTheCurrentMonthAndSeveralAhead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = Db.NewContext();
        var maintainer = new EventPartitionMaintainer(context, new FakeClock(Quiet));

        await maintainer.EnsureAsync(ct);

        Assert.True(await PartitionExistsAsync(Quiet, ct));
        Assert.True(await PartitionExistsAsync(Quiet.AddMonths(1), ct));
        Assert.True(await PartitionExistsAsync(Quiet.AddMonths(2), ct));

        // The previous month too: a sync diff can date a fact to before the process started.
        Assert.True(await PartitionExistsAsync(Quiet.AddMonths(-1), ct));
    }

    [Fact]
    public async Task EnsureAsync_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = Db.NewContext();
        var maintainer = new EventPartitionMaintainer(context, new FakeClock(Quiet.AddYears(1)));

        var first = await maintainer.EnsureAsync(ct);
        var second = await maintainer.EnsureAsync(ct);

        Assert.NotEmpty(first);
        Assert.Empty(second);
    }

    [Fact]
    public async Task CrossingAMonthBoundary_KeepsIngestWorking()
    {
        var ct = TestContext.Current.CancellationToken;
        var start = new DateTimeOffset(2029, 1, 20, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);

        await using (var setup = Db.NewContext())
            await new EventPartitionMaintainer(setup, clock).EnsureAsync(ct);

        // Two months on -- past everything the first run pre-created.
        clock.Advance(TimeSpan.FromDays(75));

        await using (var maintenance = Db.NewContext())
            await new EventPartitionMaintainer(maintenance, clock).EnsureAsync(ct);

        await using var context = Db.NewContext();
        var writer = NewWriter(context, clock);
        var subject = UniqueId("usr");

        await writer.WriteAsync(Presence(subject, clock.UtcNow), ct);

        Assert.Equal(1, await context.Events.CountAsync(e => e.SubjectId == subject, ct));
    }

    [Fact]
    public async Task WithoutTheJob_AnInsertIntoAnUncoveredMonthFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var uncovered = new DateTimeOffset(2035, 7, 1, 0, 0, 0, TimeSpan.Zero);
        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(uncovered));

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => writer.WriteAsync(Presence(UniqueId("usr"), uncovered), ct));

        Assert.Contains("partition", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureForAsync_CoversABackfilledMonthOnDemand()
    {
        var ct = TestContext.Current.CancellationToken;
        var old = new DateTimeOffset(2022, 2, 14, 0, 0, 0, TimeSpan.Zero);
        await using var context = Db.NewContext();
        var maintainer = new EventPartitionMaintainer(context, new FakeClock(Quiet));

        await maintainer.EnsureForAsync(old, ct);

        Assert.True(await PartitionExistsAsync(old, ct));
    }

    [Fact]
    public void PartitionName_IsDerivedFromTheUtcMonth()
    {
        // The retention job (spec 5.5) drops partitions by name, so the naming is part of the
        // contract between the two jobs rather than a detail of this one.
        Assert.Equal(
            "modbot_event_2026_06",
            EventPartitionMaintainer.PartitionName(new DateTimeOffset(2026, 6, 30, 23, 59, 0, TimeSpan.Zero)));

        // An instant that is a different month in local time is still bucketed by UTC.
        Assert.Equal(
            "modbot_event_2026_07",
            EventPartitionMaintainer.PartitionName(
                new DateTimeOffset(2026, 6, 30, 20, 0, 0, TimeSpan.FromHours(-8))));
    }
}
