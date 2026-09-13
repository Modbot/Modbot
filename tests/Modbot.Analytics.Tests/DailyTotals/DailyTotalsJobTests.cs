using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.DailyTotals;

/// <summary>
/// Spec 5.4: daily aggregates over the fact log, and spec 5.3's rule that an imprecise fact must
/// not be pretended to be an exact one.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DailyTotalsJobTests : AnalyticsTestBase
{
    public DailyTotalsJobTests(PostgresFixture fixture) : base(fixture) { }

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

        Assert.Equal(2m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersJoined));
        Assert.Equal(1m, await ValueAsync(DayOf(Start.AddDays(1)), DailyTotalMetrics.MembersJoined));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersLeft));
    }

    [Fact]
    public async Task MembersNetIsTheRunningNetOfJoinsAndLeaves()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Start),
            Fact(FactType.MemberJoined, Start.AddMinutes(5)),
            Fact(FactType.MemberJoined, Start.AddDays(1)),
            Fact(FactType.MemberLeft, Start.AddDays(1).AddHours(1)),
            Fact(FactType.MemberLeft, Start.AddDays(2)));

        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(2m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersNet));
        Assert.Equal(2m, await ValueAsync(DayOf(Start.AddDays(1)), DailyTotalMetrics.MembersNet));
        Assert.Equal(1m, await ValueAsync(DayOf(Start.AddDays(2)), DailyTotalMetrics.MembersNet));
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

        Assert.Equal(3m, await ValueAsync(DayOf(Start), DailyTotalMetrics.BansAdded));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorBans, "vrchat:alice"));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorRemovals, "vrchat:alice"));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorBans, "vrchat:bob"));
        Assert.Null(await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorBans));
    }

    /// <summary>
    /// VRChat writes an approved join request as a member.join whose actor is the moderator, and
    /// a self-service join as one whose actor is the joiner. Only the first is anybody's work.
    /// </summary>
    [Fact]
    public async Task ApprovalsAreJoinsSomebodyElseCaused()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Start, subjectId: "usr_new", actorId: "alice"),
            Fact(FactType.MemberJoined, Start.AddMinutes(1), subjectId: "usr_self", actorId: "usr_self"),
            Fact(FactType.MemberJoined, Start.AddMinutes(2), subjectId: "usr_unattributed"));

        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(3m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersJoined));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorApprovals, "vrchat:alice"));
        Assert.Null(await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorApprovals, "vrchat:usr_self"));
    }

    [Fact]
    public async Task EveryKindOfModeratorActionHasItsOwnRow()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start, actorId: "alice"),
            Fact(FactType.GroupInstanceWarn, Start, actorId: "alice"),
            Fact(FactType.MemberUnbanned, Start, actorId: "alice"),
            Fact(FactType.InviteCreated, Start, actorId: "alice"),
            Fact(FactType.JoinRequestRejected, Start, actorId: "alice"),
            Fact(FactType.JoinRequestBlocked, Start, actorId: "alice"),
            Fact(FactType.RoleGranted, Start, actorId: "alice"),
            Fact(FactType.RoleRevoked, Start, actorId: "alice"));

        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorInstanceKicks, "vrchat:alice"));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorWarns, "vrchat:alice"));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorUnbans, "vrchat:alice"));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorInvites, "vrchat:alice"));
        Assert.Equal(2m, await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorRejections, "vrchat:alice"));
        Assert.Equal(2m, await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorRoleChanges, "vrchat:alice"));

        // The undimensioned counterparts still count what the group received.
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.InvitesSent));
    }

    [Fact]
    public async Task InstancesAndRequestsAreCountedPerDay()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceCreated, Start, worldId: "wrld_a", instanceId: "1"),
            Fact(FactType.GroupInstanceCreated, Start.AddHours(1), worldId: "wrld_a", instanceId: "2"),
            Fact(FactType.GroupInstanceCreated, Start.AddHours(2), worldId: "wrld_b", instanceId: "3"),
            Fact(FactType.GroupInstanceClosed, Start.AddHours(3), worldId: "wrld_a", instanceId: "1"),
            Fact(FactType.JoinRequestCreated, Start));

        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(3m, await ValueAsync(DayOf(Start), DailyTotalMetrics.InstancesOpened));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.InstancesClosed));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.RequestsReceived));

        // Per world, keyed by the id exactly as the fact carried it.
        Assert.Equal(2m, await ValueAsync(DayOf(Start), DailyTotalMetrics.WorldInstances, "wrld_a"));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.WorldInstances, "wrld_b"));
    }

    /// <summary>
    /// Two moderators walking into the same room an hour apart each report every occupant, so
    /// facts per person per day is really facts per watching moderator. People per day is the
    /// number that means something.
    /// </summary>
    [Fact]
    public async Task WorldVisitorsCountsEachPersonOncePerDay()
    {
        await WriteAsync(
            Fact(FactType.InstanceJoined, Start, subjectId: "usr_a", worldId: "wrld_a", instanceId: "1", source: FactSource.Client),
            Fact(FactType.InstancePresenceObserved, Start.AddHours(1), subjectId: "usr_a", worldId: "wrld_a", instanceId: "1", source: FactSource.Client),
            Fact(FactType.InstancePresenceObserved, Start.AddHours(1), subjectId: "usr_b", worldId: "wrld_a", instanceId: "1", source: FactSource.Client),
            Fact(FactType.InstanceJoined, Start.AddHours(2), subjectId: "usr_a", worldId: "wrld_b", instanceId: "9", source: FactSource.Client),

            // Leaving is not a visit, and a fact with no world has no world row.
            Fact(FactType.InstanceLeft, Start.AddHours(3), subjectId: "usr_a", worldId: "wrld_a", instanceId: "1", source: FactSource.Client),
            Fact(FactType.InstanceJoined, Start.AddHours(4), subjectId: "usr_c", source: FactSource.Client));

        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(2m, await ValueAsync(DayOf(Start), DailyTotalMetrics.WorldVisitors, "wrld_a"));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.WorldVisitors, "wrld_b"));
        Assert.Null(await ValueAsync(DayOf(Start), DailyTotalMetrics.WorldVisitors));
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

        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersLeft));
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

        Assert.Equal(0.75m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersLeft));
        Assert.Equal(0.25m, await ValueAsync(DayOf(midnight), DailyTotalMetrics.MembersLeft));
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

        Assert.Equal(0.1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersLeft));
        Assert.Equal(0.9m, await ValueAsync(DayOf(midnight), DailyTotalMetrics.MembersLeft));
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

        Assert.Equal(3m, await ValueAsync(DayOf(Start.AddDays(4)), DailyTotalMetrics.MembersNet));

        Clock.Advance(TimeSpan.FromHours(1));
        await WriteAsync(Fact(FactType.MemberLeft, Start.AddDays(1)));

        await using (var context = Database.NewContext())
            await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersNet));
        Assert.Equal(0m, await ValueAsync(DayOf(Start.AddDays(1)), DailyTotalMetrics.MembersNet));
        Assert.Equal(1m, await ValueAsync(DayOf(Start.AddDays(2)), DailyTotalMetrics.MembersNet));
        Assert.Equal(2m, await ValueAsync(DayOf(Start.AddDays(4)), DailyTotalMetrics.MembersNet));
    }

    /// <summary>
    /// Spec 5.5: presence facts age out at 90 days, daily totals are kept forever, and "charts
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

        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersJoined));

        // Stand in for retention having dropped the older partition.
        await using (var context = Database.NewContext())
        {
            await context.Database.ExecuteSqlAsync(
                $"DELETE FROM modbot_event WHERE occurred_at < {Start.AddDays(1)}", Ct);
        }

        await using (var context = Database.NewContext())
            await NewJob(context).RebuildAsync(Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersJoined));
        Assert.Equal(1m, await ValueAsync(DayOf(Start.AddDays(5)), DailyTotalMetrics.MembersJoined));

        // And the running total still carries the departed history forward rather than restarting.
        Assert.Equal(2m, await ValueAsync(DayOf(Start.AddDays(5)), DailyTotalMetrics.MembersNet));
    }

    [Fact]
    public async Task RecomputingADayWhoseFactsAreGoneRemovesItsRow()
    {
        await WriteAsync(Fact(FactType.MemberJoined, Start));

        await using (var context = Database.NewContext())
            await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersJoined));

        await using (var context = Database.NewContext())
            await context.Database.ExecuteSqlAsync($"DELETE FROM modbot_event", Ct);

        await using (var context = Database.NewContext())
            await NewJob(context).RecomputeDaysAsync([DayOf(Start)], Ct);

        Assert.Null(await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersJoined));
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
