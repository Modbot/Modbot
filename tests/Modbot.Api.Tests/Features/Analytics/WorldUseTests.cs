using Modbot.Api.Features.Analytics.Worlds;

namespace Modbot.Api.Tests.Features.Analytics;

public class WorldUseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 9, 26, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void NoInstances_IsNothing_NotZeroPeople()
    {
        var use = WorldUse.Of([], Now);

        Assert.Equal(0, use.Instances);
        Assert.Equal(0m, use.MinutesOpen);
        Assert.Null(use.MostAtOnce);
        Assert.Null(use.LastOpenedAt);
    }

    /// <summary>Back to back and overlapping, the way Sunset Bar ran three on Sep 26.</summary>
    [Fact]
    public void Overlaps_CountOnce()
    {
        var use = WorldUse.Of(
        [
            new WorldInstance(At(0, 59), At(1, 2), 3, false),
            new WorldInstance(At(1, 2), At(2, 21), 46, false),
            new WorldInstance(At(2, 20), At(3, 16), 16, false),
        ], Now);

        Assert.Equal(3, use.Instances);
        Assert.Equal(137m, use.MinutesOpen);
        Assert.Equal(46, use.MostAtOnce);
        Assert.Equal(At(2, 20), use.LastOpenedAt);
    }

    [Fact]
    public void Gaps_AreNotOpenTime()
    {
        var use = WorldUse.Of(
        [
            new WorldInstance(At(1), At(2), 5, false),
            new WorldInstance(At(5), At(5, 30), 2, false),
        ], Now);

        Assert.Equal(90m, use.MinutesOpen);
    }

    [Fact]
    public void AnOpenInstance_RunsToNow()
    {
        var use = WorldUse.Of([new WorldInstance(Now.AddMinutes(-45), null, 4, false)], Now);

        Assert.Equal(45m, use.MinutesOpen);
    }

    [Fact]
    public void NoCounts_IsNoPeak()
    {
        var use = WorldUse.Of([new WorldInstance(At(1), At(2), null, false)], Now);

        Assert.Equal(1, use.Instances);
        Assert.Null(use.MostAtOnce);
        Assert.False(use.MostAtOnceUnsure);
    }

    [Fact]
    public void Unsure_OnlyWhenEveryInstanceAtThePeakIsUnsure()
    {
        var unsure = WorldUse.Of(
        [
            new WorldInstance(At(1), At(2), 12, true),
            new WorldInstance(At(3), At(4), 8, false),
        ], Now);

        var sure = WorldUse.Of(
        [
            new WorldInstance(At(1), At(2), 12, true),
            new WorldInstance(At(3), At(4), 12, false),
        ], Now);

        Assert.True(unsure.MostAtOnceUnsure);
        Assert.False(sure.MostAtOnceUnsure);
    }
}
