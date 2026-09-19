using Modbot.Core.Data.Entities;
using Modbot.Core.Live;

namespace Modbot.Core.Tests.Live;

/// <summary>
/// How long somebody had been in an instance when they were kicked — and, more often, that Modbot
/// cannot say.
/// </summary>
public class TimeInInstanceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 20, 0, 0, TimeSpan.Zero);

    private static readonly Guid AdaDevice = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid BenDevice = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static readonly Dictionary<Guid, string> Owners = new()
    {
        [AdaDevice] = "usr_ada",
        [BenDevice] = "usr_ben",
    };

    private static readonly InstanceKey Instance = new("wrld_a", "1001");

    private const string Joined = FactType.InstanceJoined;
    private const string Here = FactType.InstancePresenceObserved;
    private const string Left = FactType.InstanceLeft;
    private const string Stopped = FactType.InstanceLogStopped;

    private static PresenceMark M(
        string type,
        string subject,
        double minutes,
        Guid? device = null,
        string instance = "1001",
        string world = "wrld_a")
        => new(type, subject, T0.AddMinutes(minutes), world, instance, device);

    private static TimeThere? Work(double kickedAtMinutes, params PresenceMark[] marks)
        => TimeInInstance.Work(
            Instance,
            marks,
            Owners,
            new InstanceMoment("usr_cid", "wrld_a", "1001", T0.AddMinutes(kickedAtMinutes)));

    [Fact]
    public void SeenArriving_IsExactlyHowLongTheyWereThere()
    {
        var there = Work(30,
            M(Joined, "usr_ada", 0, AdaDevice),
            M(Joined, "usr_cid", 10, AdaDevice));

        Assert.NotNull(there);
        Assert.True(there.SeenArriving);
        Assert.Equal(TimeSpan.FromMinutes(20), there.HowLong);
    }

    /// <summary>
    /// Ada walked in and found Cid already there. Cid arrived at some earlier time nobody saw, so
    /// twenty minutes is a floor and not a measurement.
    /// </summary>
    [Fact]
    public void OnlyEverSeenAlreadyThere_IsALowerBound()
    {
        var there = Work(30,
            M(Here, "usr_cid", 10, AdaDevice),
            M(Joined, "usr_ada", 10, AdaDevice));

        Assert.NotNull(there);
        Assert.False(there.SeenArriving);
        Assert.Equal(TimeSpan.FromMinutes(20), there.HowLong);
    }

    /// <summary>The case this feature must never get wrong: no companion, so nothing to say.</summary>
    [Fact]
    public void NoPresenceFactsAtAll_SaysNothing()
    {
        Assert.Null(Work(30));
    }

    /// <summary>
    /// Facts exist, but no paired client reported them, so nobody was watching and the roster is
    /// not believed. Nothing to say — not zero.
    /// </summary>
    [Fact]
    public void NoPairedClientReporting_SaysNothing()
    {
        Assert.Null(Work(30,
            M(Joined, "usr_cid", 10),
            M(Here, "usr_dee", 10, device: Guid.NewGuid())));
    }

    /// <summary>
    /// The kick throws them out, so their own leave lands within a second or two of VRChat's audit
    /// entry — and VRChat's timestamps are whole seconds, so it can land either side of it. Counting
    /// it would answer "they were not there" about the very person who was just thrown out.
    /// </summary>
    [Fact]
    public void TheirOwnLeaveAtTheKick_DoesNotEraseThem()
    {
        var there = Work(30,
            M(Joined, "usr_ada", 0, AdaDevice),
            M(Joined, "usr_cid", 10, AdaDevice),
            M(Left, "usr_cid", 30, AdaDevice));

        Assert.NotNull(there);
        Assert.Equal(TimeSpan.FromMinutes(20), there.HowLong);
    }

    [Fact]
    public void TheirOwnLeaveASecondBeforeTheKick_DoesNotEraseThemEither()
    {
        var there = Work(30,
            M(Joined, "usr_ada", 0, AdaDevice),
            M(Joined, "usr_cid", 10, AdaDevice),
            M(Left, "usr_cid", 30 - (1.0 / 60), AdaDevice));

        Assert.NotNull(there);
        Assert.Equal(TimeSpan.FromMinutes(20), there.HowLong);
    }

    /// <summary>A leave well before the kick is a real departure, and they were not there.</summary>
    [Fact]
    public void LeftLongBeforeAndNeverCameBack_SaysNothing()
    {
        Assert.Null(Work(30,
            M(Joined, "usr_ada", 0, AdaDevice),
            M(Joined, "usr_cid", 10, AdaDevice),
            M(Left, "usr_cid", 15, AdaDevice)));
    }

    /// <summary>The stay that counts is the one they were in, not the one before it.</summary>
    [Fact]
    public void LeftAndCameBack_IsMeasuredFromTheReturn()
    {
        var there = Work(30,
            M(Joined, "usr_ada", 0, AdaDevice),
            M(Joined, "usr_cid", 5, AdaDevice),
            M(Left, "usr_cid", 10, AdaDevice),
            M(Joined, "usr_cid", 25, AdaDevice));

        Assert.NotNull(there);
        Assert.True(there.SeenArriving);
        Assert.Equal(TimeSpan.FromMinutes(5), there.HowLong);
    }

    /// <summary>
    /// Ada's log stopped, so nobody was watching until Ben walked in and found Cid there. Whether
    /// Cid stayed through the dark stretch is unknown, so the answer starts at Ben's arrival and is
    /// a lower bound — never the hour Ada's old facts would suggest.
    /// </summary>
    [Fact]
    public void AGapInWatching_StartsTheAnswerAgain()
    {
        var there = Work(70,
            M(Joined, "usr_ada", 0, AdaDevice),
            M(Joined, "usr_cid", 5, AdaDevice),
            M(Stopped, "usr_ada", 10, AdaDevice),
            M(Here, "usr_cid", 60, BenDevice),
            M(Joined, "usr_ben", 60, BenDevice));

        Assert.NotNull(there);
        Assert.False(there.SeenArriving);
        Assert.Equal(TimeSpan.FromMinutes(10), there.HowLong);
    }

    /// <summary>
    /// A moderator who walks in and kicks at once knows only "they were here", which the entry
    /// already says. Nothing is written for that.
    /// </summary>
    [Fact]
    public void AlreadyThereAtTheVeryMomentOfTheKick_SaysNothing()
    {
        Assert.Null(Work(0,
            M(Here, "usr_cid", 0, AdaDevice),
            M(Joined, "usr_ada", 0, AdaDevice)));
    }

    /// <summary>
    /// Seen arriving and thrown out in the same second is a real measurement of no time at all,
    /// and is worth recording — unlike the lower bound of nothing above.
    /// </summary>
    [Fact]
    public void SeenArrivingAndKickedInTheSameSecond_IsAMeasuredNothing()
    {
        var there = Work(10,
            M(Joined, "usr_ada", 0, AdaDevice),
            M(Joined, "usr_cid", 10, AdaDevice));

        Assert.NotNull(there);
        Assert.True(there.SeenArriving);
        Assert.Equal(TimeSpan.Zero, there.HowLong);
    }

    /// <summary>Facts about another instance say nothing about this one.</summary>
    [Fact]
    public void PresenceInADifferentInstance_SaysNothing()
    {
        Assert.Null(Work(30,
            M(Joined, "usr_ada", 0, AdaDevice, instance: "2002"),
            M(Joined, "usr_cid", 10, AdaDevice, instance: "2002")));
    }
}
