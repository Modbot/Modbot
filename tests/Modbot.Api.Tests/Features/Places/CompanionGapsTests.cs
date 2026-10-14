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
}
