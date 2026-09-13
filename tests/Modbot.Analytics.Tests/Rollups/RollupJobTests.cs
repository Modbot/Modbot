using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Rollups;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Rollups;

/// <summary>
/// Spec 5.4: daily aggregates over the fact log, and spec 5.3's rule that an imprecise fact must
/// not be pretended to be an exact one.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class RollupJobTests : AnalyticsTestBase
{
    public RollupJobTests(PostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task CountsFactsIntoTheDayTheyHappened()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Start),
            Fact(FactType.MemberJoined, Start.AddHours(2)),
            Fact(FactType.MemberJoined, Start.AddDays(1)),
            Fact(FactType.MemberLeft, Start));

        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(2m, await ValueAsync(DayOf(Start), RollupMetrics.MembersJoined));
        Assert.Equal(1m, await ValueAsync(DayOf(Start.AddDays(1)), RollupMetrics.MembersJoined));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), RollupMetrics.MembersLeft));
    }

    [Fact]
    public async Task MembersTotalIsTheRunningNetOfJoinsAndLeaves()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Start),
            Fact(FactType.MemberJoined, Start.AddMinutes(5)),
            Fact(FactType.MemberJoined, Start.AddDays(1)),
            Fact(FactType.MemberLeft, Start.AddDays(1).AddHours(1)),
            Fact(FactType.MemberLeft, Start.AddDays(2)));

        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(2m, await ValueAsync(DayOf(Start), RollupMetrics.MembersTotal));
        Assert.Equal(2m, await ValueAsync(DayOf(Start.AddDays(1)), RollupMetrics.MembersTotal));
        Assert.Equal(1m, await ValueAsync(DayOf(Start.AddDays(2)), RollupMetrics.MembersTotal));
    }

    [Fact]
    public async Task BansAndModeratorActionsAreCounted()
    {
        await WriteAsync(
            Fact(FactType.MemberBanned, Start, actorId: "alice"),
            Fact(FactType.MemberBanned, Start.AddHours(1), actorId: "bob"),
            Fact(FactType.MemberKicked, Start.AddHours(2), actorId: "alice"),

            // No actor: VRChat does not always name one. It still counts as a ban; it cannot
            // count towards anybody's moderator total.
            Fact(FactType.MemberBanned, Start.AddHours(3)));

        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(3m, await ValueAsync(DayOf(Start), RollupMetrics.BansAdded));
        Assert.Equal(2m, await ValueAsync(DayOf(Start), RollupMetrics.ModeratorActions, "vrchat:alice"));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), RollupMetrics.ModeratorActions, "vrchat:bob"));
        Assert.Null(await ValueAsync(DayOf(Start), RollupMetrics.ModeratorActions));
    }

    /// <summary>
    /// The common imprecise case: a sync diff knows only that someone left during the five
    /// minutes between two polls. At daily granularity that is still one day, and counting it as
    /// a whole event on that day is exactly right.
    /// </summary>
    [Fact]
    public async Task ImpreciseFactWithinOneDayCountsWhollyOnThatDay()
    {
        await WriteAsync(Fact(
            FactType.MemberLeft,
            Start,
            occurredBefore: Start.AddMinutes(5),
            source: FactSource.SyncDiff));

        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(Start), RollupMetrics.MembersLeft));
    }

    /// <summary>
    /// The case that makes the honesty concrete. All Modbot knows is that they left somewhere in
    /// a window straddling midnight, so the day it fell on gets the fraction of the window it
    /// covers -- and neither day gets a whole departure it cannot support.
    /// </summary>
    [Fact]
    public async Task ImpreciseFactStraddlingMidnightIsSplitAcrossBothDays()
    {
        var midnight = new DateTimeOffset(Start.Year, Start.Month, Start.Day, 0, 0, 0, TimeSpan.Zero)
            .AddDays(1);

        await WriteAsync(Fact(
            FactType.MemberLeft,
            midnight.AddMinutes(-45),
            occurredBefore: midnight.AddMinutes(15),
            source: FactSource.SyncDiff));

        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(0.75m, await ValueAsync(DayOf(Start), RollupMetrics.MembersLeft));
        Assert.Equal(0.25m, await ValueAsync(DayOf(midnight), RollupMetrics.MembersLeft));
    }

    /// <summary>
    /// The point of the window is that it moves the count. Collapsing it to <c>occurred_at</c>
    /// would put the whole departure on the earlier day and invent a precision Modbot does not
    /// have (spec 5.3).
    /// </summary>
    [Fact]
    public async Task ImpreciseFactIsNotCollapsedOntoItsLowerBound()
    {
        var midnight = new DateTimeOffset(Start.Year, Start.Month, Start.Day, 0, 0, 0, TimeSpan.Zero)
            .AddDays(1);

        await WriteAsync(Fact(
            FactType.MemberLeft,
            midnight.AddMinutes(-1),
            occurredBefore: midnight.AddMinutes(9),
            source: FactSource.SyncDiff));

        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(0.1m, await ValueAsync(DayOf(Start), RollupMetrics.MembersLeft));
        Assert.Equal(0.9m, await ValueAsync(DayOf(midnight), RollupMetrics.MembersLeft));
    }

    /// <summary>
    /// Facts arrive out of order. A departure dated to last Tuesday has to move every running
    /// total after last Tuesday, or the member count is permanently wrong by one from then on.
    /// </summary>
    [Fact]
    public async Task ALateFactMovesEveryLaterRunningTotal()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Start),
            Fact(FactType.MemberJoined, Start.AddDays(2)),
            Fact(FactType.MemberJoined, Start.AddDays(4)));

        await using (var context = Database.NewContext())
            await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(3m, await ValueAsync(DayOf(Start.AddDays(4)), RollupMetrics.MembersTotal));

        Clock.Advance(TimeSpan.FromHours(1));
        await WriteAsync(Fact(FactType.MemberLeft, Start.AddDays(1)));

        await using (var context = Database.NewContext())
            await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(Start), RollupMetrics.MembersTotal));
        Assert.Equal(0m, await ValueAsync(DayOf(Start.AddDays(1)), RollupMetrics.MembersTotal));
        Assert.Equal(1m, await ValueAsync(DayOf(Start.AddDays(2)), RollupMetrics.MembersTotal));
        Assert.Equal(2m, await ValueAsync(DayOf(Start.AddDays(4)), RollupMetrics.MembersTotal));
    }

    /// <summary>
    /// Spec 5.5: presence facts age out at 90 days, rollups are kept forever, and "charts
    /// therefore keep their full history even after the underlying events age out". A rebuild
    /// that started from zero would delete precisely the history that promise is about.
    /// </summary>
    [Fact]
    public async Task RebuildKeepsDaysTheFactLogNoLongerCovers()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Start),
            Fact(FactType.MemberJoined, Start.AddDays(5)));

        await using (var context = Database.NewContext())
            await NewJob(context).RebuildAsync(Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(Start), RollupMetrics.MembersJoined));

        // Stand in for retention having dropped the older partition.
        await using (var context = Database.NewContext())
        {
            await context.Database.ExecuteSqlAsync(
                $"DELETE FROM modbot_event WHERE occurred_at < {Start.AddDays(1)}", Ct);
        }

        await using (var context = Database.NewContext())
            await NewJob(context).RebuildAsync(Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(Start), RollupMetrics.MembersJoined));
        Assert.Equal(1m, await ValueAsync(DayOf(Start.AddDays(5)), RollupMetrics.MembersJoined));

        // And the running total still carries the departed history forward rather than restarting.
        Assert.Equal(2m, await ValueAsync(DayOf(Start.AddDays(5)), RollupMetrics.MembersTotal));
    }

    [Fact]
    public async Task RecomputingADayWhoseFactsAreGoneRemovesItsRow()
    {
        await WriteAsync(Fact(FactType.MemberJoined, Start));

        await using (var context = Database.NewContext())
            await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(Start), RollupMetrics.MembersJoined));

        await using (var context = Database.NewContext())
            await context.Database.ExecuteSqlAsync($"DELETE FROM modbot_event", Ct);

        await using (var context = Database.NewContext())
            await NewJob(context).RecomputeDaysAsync([DayOf(Start)], Ct);

        Assert.Null(await ValueAsync(DayOf(Start), RollupMetrics.MembersJoined));
    }

    [Fact]
    public async Task AnEmptyFactLogProducesNothingAndDoesNotThrow()
    {
        await using var context = Database.NewContext();

        var incremental = await NewJob(context).RunIncrementalAsync(Ct);
        var rebuild = await NewJob(context).RebuildAsync(Ct);

        Assert.Equal(0, incremental.RowsWritten);
        Assert.Equal(0, rebuild.RowsWritten);
        Assert.Empty(await SnapshotAsync());
    }

    [Fact]
    public async Task RunningTwiceChangesNothing()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Start),
            Fact(FactType.MemberLeft, Start.AddDays(1), occurredBefore: Start.AddDays(1).AddMinutes(5)),
            Fact(FactType.MemberBanned, Start.AddDays(1), actorId: "alice"));

        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);
        var first = await SnapshotAsync();

        await NewJob(context).RunIncrementalAsync(Ct);
        await NewJob(context).RebuildAsync(Ct);

        Assert.NotEmpty(first);
        Assert.Equal(first, await SnapshotAsync());
    }
}
