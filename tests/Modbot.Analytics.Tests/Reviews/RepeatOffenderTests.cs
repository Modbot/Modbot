using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Reviews;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Reviews;

/// <summary>
/// Spec 5.8.4: per-person counts across every moderator and instance, as a cache rebuilt from
/// facts, with a status whose rule is stated.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class RepeatOffenderTests : ReviewTestBase
{
    public RepeatOffenderTests(PostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task CountsEveryKind_TheWindows_AndDistinctModerators()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddDays(-1), subjectId: "usr_p", actorId: "alice", worldId: "wrld_a", instanceId: "1"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-2), subjectId: "usr_p", actorId: "bob", worldId: "wrld_a", instanceId: "2"),
            Fact(FactType.GroupInstanceWarn, Start.AddDays(-40), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.MemberBanned, Start.AddDays(-100), subjectId: "usr_p", actorId: "carol"),
            Fact(FactType.MemberUnbanned, Start.AddDays(-95), subjectId: "usr_p", actorId: "carol"),
            Fact(FactType.JoinRequestRejected, Start.AddDays(-50), subjectId: "usr_p", actorId: "bob"),
            Fact(FactType.MemberKicked, Start.AddDays(-60), subjectId: "usr_p"));

        await RunAsync();

        var row = await OffenderAsync("usr_p");
        Assert.NotNull(row);

        Assert.Equal(2, row.InstanceKicks);
        Assert.Equal(1, row.Warns);
        Assert.Equal(1, row.Bans);
        Assert.Equal(1, row.Unbans);
        Assert.Equal(1, row.Removals);
        Assert.Equal(1, row.Rejections);

        // Six actions against them; the unban is relief and does not count.
        Assert.Equal(6, row.Actions);
        Assert.Equal(2, row.ActionsLast30Days);
        Assert.Equal(5, row.ActionsLast90Days);

        // Three named moderators ever; the removal had no actor and counts as nobody's.
        Assert.Equal(3, row.Moderators);
        Assert.Equal(2, row.ModeratorsLast90Days);

        Assert.Equal(Start.AddDays(-100), row.FirstActionAt);
        Assert.Equal(Start.AddDays(-1), row.LastActionAt);
        Assert.Equal(FactType.GroupInstanceKick, row.LastActionType);
        Assert.Equal("alice", row.LastActorId);

        Assert.Equal(RepeatOffenderStatus.MoreThanOnce, row.Status);
        Assert.Equal(Start, row.ComputedAt);
    }

    [Fact]
    public async Task Status_IsRepeatAtThreeActionsInThirtyDays_AndOnceForASingleAction()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddDays(-1), subjectId: "usr_repeat", actorId: "alice"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-10), subjectId: "usr_repeat", actorId: "alice"),
            Fact(FactType.GroupInstanceWarn, Start.AddDays(-20), subjectId: "usr_repeat", actorId: "bob"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-3), subjectId: "usr_once", actorId: "alice"));

        await RunAsync();

        Assert.Equal(RepeatOffenderStatus.Repeat, (await OffenderAsync("usr_repeat"))!.Status);
        Assert.Equal(RepeatOffenderStatus.Once, (await OffenderAsync("usr_once"))!.Status);
    }

    [Fact]
    public async Task TheRepeatThreshold_IsASetting()
    {
        await SetThresholdsAsync(ReviewThresholds.Default with { RepeatOffenderActionsIn30Days = 5 });

        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddDays(-1), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-2), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-3), subjectId: "usr_p", actorId: "alice"));

        await RunAsync();

        Assert.Equal(RepeatOffenderStatus.MoreThanOnce, (await OffenderAsync("usr_p"))!.Status);
    }

    [Fact]
    public async Task SomebodyOnlyEverUnbanned_HasNoRow()
    {
        await WriteAsync(Fact(FactType.MemberUnbanned, Start.AddDays(-1), subjectId: "usr_free", actorId: "alice"));

        await RunAsync();

        Assert.Null(await OffenderAsync("usr_free"));
    }

    /// <summary>
    /// Nothing new happens to the person, but time passes and an action falls out of the
    /// 30-day window. The incremental run has to notice without a fact to prompt it.
    /// </summary>
    [Fact]
    public async Task IncrementalRun_RefreshesARow_WhenAnActionFallsOutOfTheWindow()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddDays(-29), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-28), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-27), subjectId: "usr_p", actorId: "bob"));

        await RunAsync();

        var before = await OffenderAsync("usr_p");
        Assert.Equal(RepeatOffenderStatus.Repeat, before!.Status);
        Assert.Equal(3, before.ActionsLast30Days);
        Assert.Equal(Start.AddDays(-29).AddDays(30), before.CountsChangeAt);

        // A newer fact about somebody else moves the watermark past usr_p's facts, so the only
        // thing that can bring usr_p's row back into the run is the counts-change time.
        Clock.Advance(TimeSpan.FromDays(1.5));
        await WriteAsync(Fact(FactType.GroupInstanceKick, Clock.UtcNow, subjectId: "usr_other", actorId: "alice"));
        await RunAsync();

        var after = await OffenderAsync("usr_p");
        Assert.Equal(2, after!.ActionsLast30Days);
        Assert.Equal(RepeatOffenderStatus.MoreThanOnce, after.Status);
        Assert.Equal(Clock.UtcNow, after.ComputedAt);
    }

    [Fact]
    public async Task IncrementalRun_PicksUpANewFact_AndRebuildAgrees()
    {
        await WriteAsync(Fact(FactType.GroupInstanceKick, Start.AddDays(-5), subjectId: "usr_p", actorId: "alice"));
        await RunAsync();

        Assert.Equal(1, (await OffenderAsync("usr_p"))!.Actions);

        Clock.Advance(TimeSpan.FromHours(1));
        await WriteAsync(Fact(FactType.GroupInstanceWarn, Start.AddDays(-1), subjectId: "usr_p", actorId: "bob"));
        await RunAsync();

        var incremental = await OffenderAsync("usr_p");
        Assert.Equal(2, incremental!.Actions);
        Assert.Equal(2, incremental.Moderators);

        await RunAsync(rebuild: true);

        var rebuilt = await OffenderAsync("usr_p");
        Assert.Equal(incremental.Actions, rebuilt!.Actions);
        Assert.Equal(incremental.Moderators, rebuilt.Moderators);
        Assert.Equal(incremental.Status, rebuilt.Status);
        Assert.Equal(incremental.LastActionAt, rebuilt.LastActionAt);
    }

    [Fact]
    public async Task ARowDisappears_WhenThePersonsFactsAreGone()
    {
        await WriteAsync(Fact(FactType.GroupInstanceKick, Start.AddDays(-5), subjectId: "usr_gone", actorId: "alice"));
        await RunAsync();
        Assert.NotNull(await OffenderAsync("usr_gone"));

        await using (var context = Database.NewContext())
            await context.Database.ExecuteSqlRawAsync("DELETE FROM modbot_event WHERE subject_id = 'usr_gone'", Ct);

        await RunAsync(rebuild: true);

        Assert.Null(await OffenderAsync("usr_gone"));
    }
}
