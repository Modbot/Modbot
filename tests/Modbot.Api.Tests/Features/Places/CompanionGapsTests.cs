using Modbot.Api.Features.Places;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Tests.Features.Places;

public class CompanionGapsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 20, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int minutes, int seconds = 0) => T0.AddMinutes(minutes).AddSeconds(seconds);

    private static long _next;

    private static PresenceReport Report(string type, string? subject, DateTimeOffset at, string? device)
        => new(++_next, type, subject, at, device);

    private static PresenceReport Joined(string who, DateTimeOffset at, string? device = "d1")
        => Report(FactType.InstanceJoined, who, at, device);

    private static PresenceReport Seen(string who, DateTimeOffset at, string? device = "d1")
        => Report(FactType.InstancePresenceObserved, who, at, device);

    private static PresenceReport Left(string who, DateTimeOffset at, string? device = "d1")
        => Report(FactType.InstanceLeft, who, at, device);

    private static PresenceReport Stopped(string moderator, DateTimeOffset at, string? device = "d1")
        => Report(FactType.InstanceLogStopped, moderator, at, device);

    private static PresenceStay StayOf(IReadOnlyList<PresenceStay> stays, string who)
        => Assert.Single(stays, s => s.UserId == who);

    [Fact]
    public void NoReports_IsNoStays()
    {
        Assert.Empty(CompanionGaps.Stays([]));
    }

    [Fact]
    public void OnlyAStop_IsNoStays()
    {
        Assert.Empty(CompanionGaps.Stays([Stopped("usr_mod", At(5))]));
    }

    /// <summary>Facts that name no client are one client: the old rule, nothing inferred.</summary>
    [Fact]
    public void NoDevice_FollowsTheOrdinaryRule()
    {
        var stays = CompanionGaps.Stays(
        [
            Seen("usr_b", At(0), device: null),
            Joined("usr_a", At(5), device: null),
            Left("usr_a", At(10), device: null),
        ]);

        Assert.Equal(2, stays.Count);

        var a = StayOf(stays, "usr_a");
        Assert.Equal((At(5), At(10), true, true), (a.Started, a.Ended, a.ArrivalSeen, a.LeaveSeen));

        // Nobody saw Bea arrive or leave: she is there from the report until the last report.
        var b = StayOf(stays, "usr_b");
        Assert.Equal((At(0), At(10), false, false), (b.Started, b.Ended, b.ArrivalSeen, b.LeaveSeen));
    }

    /// <summary>
    /// One moderator's client stopped at 10:00 and came back at 20:00 listing only Ada and itself.
    /// Bea and Dan were not in that list, so they left in the gap, and count as gone from its start.
    /// </summary>
    [Fact]
    public void ACompanionThatCrashedAndCameBack_LosesWhoWasMissingFromItsNewList()
    {
        var stays = CompanionGaps.Stays(
        [
            Seen("usr_a", At(0)),
            Seen("usr_b", At(0, 1)),
            Joined("usr_mod", At(0, 2)),
            Joined("usr_d", At(5)),
            Stopped("usr_mod", At(10)),
            Seen("usr_a", At(20)),
            Seen("usr_mod", At(20, 1)),
            Joined("usr_mod", At(20, 2)),
            Left("usr_a", At(30)),
        ]);

        Assert.Equal(4, stays.Count);

        var a = StayOf(stays, "usr_a");
        Assert.Equal((At(0), At(30), false, true), (a.Started, a.Ended, a.ArrivalSeen, a.LeaveSeen));

        var b = StayOf(stays, "usr_b");
        Assert.Equal((At(0, 1), At(10), false, false), (b.Started, b.Ended, b.ArrivalSeen, b.LeaveSeen));

        var d = StayOf(stays, "usr_d");
        Assert.Equal((At(5), At(10), true, false), (d.Started, d.Ended, d.ArrivalSeen, d.LeaveSeen));

        // The moderator is on the new list, so the stay carries through the gap to the last report.
        var moderator = StayOf(stays, "usr_mod");
        Assert.Equal((At(0, 2), At(30), true, false), (moderator.Started, moderator.Ended, moderator.ArrivalSeen, moderator.LeaveSeen));
    }

    /// <summary>
    /// With no stop reported the gap starts at the last thing the client said before the new list --
    /// the last moment it is known to have been looking.
    /// </summary>
    [Fact]
    public void ACrashWithNoStopReported_StartsTheGapAtTheClientsLastReport()
    {
        var stays = CompanionGaps.Stays(
        [
            Seen("usr_a", At(0)),
            Seen("usr_b", At(0, 1)),
            Joined("usr_c", At(5)),
            Seen("usr_a", At(20)),
            Left("usr_a", At(30)),
        ]);

        Assert.Equal((At(0, 1), At(5)), (StayOf(stays, "usr_b").Started, StayOf(stays, "usr_b").Ended));
        Assert.Equal((At(5), At(5)), (StayOf(stays, "usr_c").Started, StayOf(stays, "usr_c").Ended));
        Assert.Equal(At(30), StayOf(stays, "usr_a").Ended);
    }

    /// <summary>
    /// Two moderators were watching. The first crashed and came back without Bea and Xavier. The
    /// second kept watching Bea throughout, so she stays; nobody vouched for Xavier, so he is gone.
    /// </summary>
    [Fact]
    public void APersonAnotherCompanionStillSaw_IsNotDropped()
    {
        var stays = CompanionGaps.Stays(
        [
            Seen("usr_a", At(0)),
            Seen("usr_b", At(0, 1)),
            Seen("usr_x", At(0, 2)),
            Joined("usr_mod1", At(0, 3)),
            Seen("usr_a", At(1), "d2"),
            Seen("usr_b", At(1, 1), "d2"),
            Joined("usr_mod2", At(1, 2), "d2"),
            Stopped("usr_mod1", At(10)),
            Seen("usr_a", At(20)),
            Joined("usr_mod1", At(20, 1)),
            Left("usr_a", At(30), "d2"),
        ]);

        var b = StayOf(stays, "usr_b");
        Assert.Equal((At(0, 1), At(30), false), (b.Started, b.Ended, b.LeaveSeen));

        var x = StayOf(stays, "usr_x");
        Assert.Equal((At(0, 2), At(10), false), (x.Started, x.Ended, x.LeaveSeen));

        var a = StayOf(stays, "usr_a");
        Assert.Equal((At(0), At(30), true), (a.Started, a.Ended, a.LeaveSeen));
    }

    /// <summary>
    /// The second moderator's view of Bea ended (they stopped too) before the first one's gap began,
    /// so nobody was vouching for her at the start of it.
    /// </summary>
    [Fact]
    public void APersonNobodySawAtTheStartOfTheGap_IsDropped()
    {
        var stays = CompanionGaps.Stays(
        [
            Seen("usr_b", At(0)),
            Joined("usr_mod1", At(0, 1)),
            Seen("usr_b", At(1), "d2"),
            Joined("usr_mod2", At(1, 1), "d2"),
            Stopped("usr_mod2", At(5), "d2"),
            Stopped("usr_mod1", At(10)),
            Seen("usr_mod1", At(20)),
            Joined("usr_mod1", At(20, 1)),
        ]);

        var b = StayOf(stays, "usr_b");
        Assert.Equal((At(0), At(10), false), (b.Started, b.Ended, b.LeaveSeen));
    }

    /// <summary>
    /// A forty-five person roster logged at 0.7 s apiece takes over thirty seconds, three times the
    /// burst allowance. Each report is timed from the one before it, so it is still one roster and
    /// nobody in its first part is read as having left when its tail arrives.
    /// </summary>
    [Fact]
    public void ALongRoster_LoggedOverMoreThanTheAllowance_IsStillOneRoster()
    {
        var people = Enumerable.Range(0, 45).Select(i => $"usr_{i:00}").ToList();

        List<PresenceReport> Roster(DateTimeOffset start)
            => people.Select((who, i) => Seen(who, start.AddMilliseconds(700 * i))).ToList();

        var first = Roster(At(0));
        var second = Roster(At(10));

        var stays = CompanionGaps.Stays([.. first, .. second]);

        Assert.Equal(45, stays.Count);

        var lastReport = second[^1].At;

        for (var i = 0; i < people.Count; i++)
        {
            var stay = StayOf(stays, people[i]);
            Assert.Equal((first[i].At, lastReport, false), (stay.Started, stay.Ended, stay.LeaveSeen));
        }
    }

    /// <summary>
    /// Two moderators' clients come back together. The fact writer keeps only the first report of a
    /// person within its dedup window, so the second moderator's list reaches the database as just its
    /// own join. Ada and Bea were on the first moderator's list at the same moment, so they are still
    /// there: one stay each, not a gap and a second visit. Cy and Dan were on neither list and left in
    /// the gap.
    /// </summary>
    [Fact]
    public void TwoCompanionsRejoiningTogether_KeepsWhoTheOtherOneStillListed()
    {
        var stays = CompanionGaps.Stays(
        [
            Seen("usr_a", At(0)),
            Seen("usr_b", At(0, 1)),
            Seen("usr_c", At(0, 2)),
            Seen("usr_d", At(0, 3)),
            Joined("usr_mod1", At(0, 4)),
            // The second client arrives a minute later, outside the dedup window of the first one's
            // reports, so its list is recorded in full. Had it arrived within five seconds, the
            // fact writer would have thrown the copies away and only its own join would be here.
            Seen("usr_a", At(1), "d2"),
            Seen("usr_b", At(1, 1), "d2"),
            Seen("usr_c", At(1, 2), "d2"),
            Seen("usr_d", At(1, 3), "d2"),
            Joined("usr_mod2", At(1, 4), "d2"),
            Stopped("usr_mod1", At(10)),
            Stopped("usr_mod2", At(10), "d2"),
            // Both come back at once: the first one's list is kept, the second one's copies of Ada
            // and Bea (within five seconds of the first's) are thrown away as duplicates and only
            // its own join survives.
            Seen("usr_a", At(20)),
            Seen("usr_b", At(20, 1)),
            Joined("usr_mod1", At(20, 2)),
            Joined("usr_mod2", At(20, 4), "d2"),
        ]);

        var lastReport = At(20, 4);

        foreach (var who in new[] { "usr_a", "usr_b" })
        {
            var stay = StayOf(stays, who);
            Assert.Equal((lastReport, false), (stay.Ended, stay.LeaveSeen));
        }

        foreach (var who in new[] { "usr_c", "usr_d" })
        {
            var stay = StayOf(stays, who);
            Assert.Equal((At(10), false), (stay.Ended, stay.LeaveSeen));
        }
    }

    /// <summary>
    /// The second moderator's restatement is a long roster and only its tail survived the fact writer.
    /// Ada was early in it, so her copy was thrown away as a twin of the first moderator's report,
    /// which is more than five seconds before the first surviving report. She is still there.
    /// </summary>
    [Fact]
    public void ADuplicateEarlyInALongRestatement_IsStillKept()
    {
        var stays = CompanionGaps.Stays(
        [
            Seen("usr_a", At(0)),
            Joined("usr_mod1", At(0, 1)),
            Seen("usr_a", At(1), "d2"),
            Seen("usr_p1", At(1, 1), "d2"),
            Seen("usr_p2", At(1, 2), "d2"),
            Seen("usr_p3", At(1, 3), "d2"),
            Joined("usr_mod2", At(1, 4), "d2"),
            Stopped("usr_mod1", At(10)),
            Stopped("usr_mod2", At(10), "d2"),
            Seen("usr_a", At(20)),
            Joined("usr_mod1", At(20, 1)),
            // The second moderator's reports up to 20:11 were thrown away as twins of the first's;
            // these are what is left of its restatement.
            Seen("usr_p2", At(20, 12), "d2"),
            Seen("usr_p3", At(20, 13), "d2"),
            Joined("usr_mod2", At(20, 14), "d2"),
        ]);

        var a = StayOf(stays, "usr_a");
        Assert.Equal((At(0), At(20, 14), false), (a.Started, a.Ended, a.LeaveSeen));

        var p1 = StayOf(stays, "usr_p1");
        Assert.Equal((At(10), false), (p1.Ended, p1.LeaveSeen));
    }

    /// <summary>
    /// The surviving stragglers are so far apart that they read as two runs. Ada, kept by the first
    /// run because the first moderator reported her, is not dropped by the second.
    /// </summary>
    [Fact]
    public void SparseStragglers_SplitIntoTwoRuns_DoNotLetTheSecondDropWhoTheFirstKept()
    {
        var stays = CompanionGaps.Stays(
        [
            Seen("usr_a", At(0)),
            Joined("usr_mod1", At(0, 1)),
            Seen("usr_a", At(1), "d2"),
            Seen("usr_p1", At(1, 1), "d2"),
            Seen("usr_p2", At(1, 2), "d2"),
            Stopped("usr_mod1", At(10)),
            Stopped("usr_mod2", At(10), "d2"),
            Seen("usr_a", At(20)),
            Joined("usr_mod1", At(20, 1)),
            Seen("usr_p1", At(20, 5), "d2"),
            Seen("usr_p2", At(20, 30), "d2"),
        ]);

        var a = StayOf(stays, "usr_a");
        Assert.Equal((At(0), At(20, 30), false), (a.Started, a.Ended, a.LeaveSeen));
    }

    /// <summary>
    /// A restatement thirty minutes after a long roster, with nothing of the first overlapping it:
    /// everybody in the first roster the second leaves out left in the gap, from the first roster's
    /// last report.
    /// </summary>
    [Fact]
    public void ARestatementLongAfterALongRoster_DropsEveryoneItLeavesOut()
    {
        var first = Enumerable.Range(0, 45)
            .Select(i => Seen($"usr_{i:00}", At(0).AddMilliseconds(700 * i)))
            .ToList();

        var stays = CompanionGaps.Stays(
        [
            .. first,
            Seen("usr_00", At(30)),
            Seen("usr_01", At(30, 1)),
        ]);

        Assert.Equal(45, stays.Count);
        Assert.Equal((first[^1].At, false), (StayOf(stays, "usr_44").Ended, StayOf(stays, "usr_44").LeaveSeen));
        Assert.Equal(At(30, 1), StayOf(stays, "usr_00").Ended);
    }

    /// <summary>
    /// The dedup window is a setting. The first moderator lists Ada ten seconds after the second
    /// moderator's only surviving report: a window of two seconds does not make that a twin of
    /// anything, so Ada was lost at the gap and comes back; a window of ten does, so she stays.
    /// </summary>
    [Fact]
    public void ADifferentDedupWindow_ChangesWhoCountsAsTheSameReport()
    {
        PresenceReport[] Reports() =>
        [
            Seen("usr_a", At(0)),
            Joined("usr_mod1", At(0, 1)),
            Seen("usr_a", At(1), "d2"),
            Joined("usr_mod2", At(1, 1), "d2"),
            Stopped("usr_mod1", At(9)),
            Stopped("usr_mod2", At(10), "d2"),
            Joined("usr_mod2", At(20, 4), "d2"),
            Seen("usr_a", At(20, 10)),
            Joined("usr_mod1", At(20, 11)),
        ];

        Assert.Equal(2, CompanionGaps.Stays(Reports(), TimeSpan.FromSeconds(2)).Count(s => s.UserId == "usr_a"));
        Assert.Single(CompanionGaps.Stays(Reports(), TimeSpan.FromSeconds(10)), s => s.UserId == "usr_a");
    }

    /// <summary>
    /// Both moderators' logs stopped at 10:00. The first one's restatement leaves Ada out and the
    /// second one's keeps her, and the second was watching up to that very instant, so she is not
    /// dropped.
    /// </summary>
    [Fact]
    public void AModeratorWhoStoppedAtTheSameInstant_StillVouchesForTheDrop()
    {
        var stays = CompanionGaps.Stays(
        [
            Seen("usr_a", At(0)),
            Joined("usr_mod1", At(0, 1)),
            Seen("usr_a", At(1), "d2"),
            Joined("usr_mod2", At(1, 1), "d2"),
            Stopped("usr_mod1", At(10)),
            Stopped("usr_mod2", At(10), "d2"),
            Joined("usr_mod1", At(20)),
            Seen("usr_a", At(21), "d2"),
            Joined("usr_mod2", At(21, 1), "d2"),
        ]);

        var a = StayOf(stays, "usr_a");
        Assert.Equal((At(0), At(21, 1), false), (a.Started, a.Ended, a.LeaveSeen));
    }

    /// <summary>
    /// Both moderators stopped at 10:00 and neither one's restatement lists Ada: nobody is left to
    /// vouch for her, so she left at 10:00.
    /// </summary>
    [Fact]
    public void TwoModeratorsWhoBothLoseAPersonAtTheSameInstant_DropThem()
    {
        var stays = CompanionGaps.Stays(
        [
            Seen("usr_a", At(0)),
            Joined("usr_mod1", At(0, 1)),
            Seen("usr_a", At(1), "d2"),
            Joined("usr_mod2", At(1, 1), "d2"),
            Stopped("usr_mod1", At(10)),
            Stopped("usr_mod2", At(10), "d2"),
            Joined("usr_mod1", At(20)),
            Seen("usr_mod2", At(21), "d2"),
        ]);

        var a = StayOf(stays, "usr_a");
        Assert.Equal((At(10), false), (a.Ended, a.LeaveSeen));
    }
}
