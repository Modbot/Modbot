using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// The stick shortcut's counter: it counts a stick held one way, finishes once, and never starts on
/// a stick that is being used for something else.
/// </summary>
public class StickHoldTests
{
    private static readonly TimeSpan Five = TimeSpan.FromSeconds(5);

    private static readonly Vector2 Back = new(0f, -1f);

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    private static StickHoldResult Look(StickHold hold, Vector2 stick, double seconds, bool free = true, StickDirection way = StickDirection.Back)
        => hold.Update(stick, way, Five, free, At(seconds));

    [Fact]
    public void ACountdownFromFiveRunsDownToOneAndThenFinishes()
    {
        var hold = new StickHold();

        Assert.Equal(5, Look(hold, Back, 0).SecondsLeft);
        Assert.Equal(5, Look(hold, Back, 0.9).SecondsLeft);
        Assert.Equal(4, Look(hold, Back, 1.1).SecondsLeft);
        Assert.Equal(3, Look(hold, Back, 2.5).SecondsLeft);
        Assert.Equal(2, Look(hold, Back, 3.5).SecondsLeft);
        Assert.Equal(1, Look(hold, Back, 4.9).SecondsLeft);

        var done = Look(hold, Back, 5.0);
        Assert.True(done.Done);
        Assert.Null(done.SecondsLeft);
    }

    [Fact]
    public void HoldingOnAfterItFinishedDoesNotFinishAgainUntilTheStickIsReleased()
    {
        var hold = new StickHold();
        Look(hold, Back, 0);
        Assert.True(Look(hold, Back, 5).Done);

        foreach (var second in new[] { 5.5, 8, 12, 30 })
            Assert.Equal(StickHoldResult.Idle, Look(hold, Back, second));

        Look(hold, Vector2.Zero, 31);

        Assert.Equal(5, Look(hold, Back, 32).SecondsLeft);
        Assert.True(Look(hold, Back, 37).Done);
    }

    [Fact]
    public void LettingGoEarlyDropsTheCountAndTheNextHoldStartsAgain()
    {
        var hold = new StickHold();
        Look(hold, Back, 0);
        Assert.Equal(2, Look(hold, Back, 3.5).SecondsLeft);

        Assert.Equal(StickHoldResult.Idle, Look(hold, Vector2.Zero, 4));

        var again = Look(hold, Back, 4.1);
        Assert.Equal(5, again.SecondsLeft);
        Assert.False(again.Done);
    }

    [Fact]
    public void AStickThatWobblesBetweenTheTwoThresholdsKeepsCounting()
    {
        var hold = new StickHold();
        Look(hold, new Vector2(0f, -0.9f), 0);

        Assert.Equal(4, Look(hold, new Vector2(0f, -0.6f), 1.5).SecondsLeft);
        Assert.Equal(3, Look(hold, new Vector2(0f, -0.55f), 2.5).SecondsLeft);

        Assert.Null(Look(hold, new Vector2(0f, -0.4f), 3).SecondsLeft);
    }

    [Fact]
    public void AStickPushedOnlyPartWayDoesNotStart()
    {
        var hold = new StickHold();

        Assert.Equal(StickHoldResult.Idle, Look(hold, new Vector2(0f, -0.6f), 0));
        Assert.Equal(StickHoldResult.Idle, Look(hold, new Vector2(0f, -0.6f), 6));
    }

    [Fact]
    public void AStickHeldTheWrongWayDoesNotCount()
    {
        var hold = new StickHold();

        Assert.Equal(StickHoldResult.Idle, Look(hold, new Vector2(0f, 1f), 0));
        Assert.Equal(StickHoldResult.Idle, Look(hold, new Vector2(1f, 0f), 6));
        Assert.Equal(StickHoldResult.Idle, Look(hold, new Vector2(1f, -1f), 12));
    }

    [Theory]
    [InlineData(StickDirection.Back, 0f, -1f)]
    [InlineData(StickDirection.Forward, 0f, 1f)]
    [InlineData(StickDirection.Left, -1f, 0f)]
    [InlineData(StickDirection.Right, 1f, 0f)]
    public void EachDirectionIsTheStickPushedThatWay(StickDirection way, float x, float y)
    {
        var hold = new StickHold();

        Assert.Equal(5, Look(hold, new Vector2(x, y), 0, way: way).SecondsLeft);

        var other = new StickHold();
        Assert.Null(Look(other, new Vector2(-x, -y), 0, way: way).SecondsLeft);
    }

    [Fact]
    public void AStickInUseIsNeverCounted()
    {
        var hold = new StickHold();

        Assert.Equal(StickHoldResult.Idle, Look(hold, Back, 0, free: false));
        Assert.Equal(StickHoldResult.Idle, Look(hold, Back, 6, free: false));
    }

    [Fact]
    public void AStickStillPushedWhenItsUseEndsWaitsForTheMiddleBeforeItStarts()
    {
        var hold = new StickHold();

        // Pulling a carried panel closer: the stick is back for as long as that takes.
        Look(hold, Back, 0, free: false);
        Look(hold, Back, 4, free: false);

        // The panel is let go, and the stick is still back: that is not the start of a hold.
        Assert.Equal(StickHoldResult.Idle, Look(hold, Back, 4.1));
        Assert.Equal(StickHoldResult.Idle, Look(hold, Back, 20));

        Look(hold, Vector2.Zero, 21);

        Assert.Equal(5, Look(hold, Back, 22).SecondsLeft);
    }

    [Fact]
    public void AStickThatBecomesBusyMidHoldDropsTheCount()
    {
        var hold = new StickHold();
        Look(hold, Back, 0);
        Assert.Equal(3, Look(hold, Back, 2.5).SecondsLeft);

        Assert.Equal(StickHoldResult.Idle, Look(hold, Back, 3, free: false));
        Assert.Equal(StickHoldResult.Idle, Look(hold, Back, 4));
    }

    [Fact]
    public void ResetWaitsForTheStickToComeBackToTheMiddle()
    {
        var hold = new StickHold();
        Look(hold, Back, 0);

        hold.Reset();

        Assert.Equal(StickHoldResult.Idle, Look(hold, Back, 1));

        Look(hold, Vector2.Zero, 2);
        Assert.Equal(5, Look(hold, Back, 3).SecondsLeft);
    }

    [Fact]
    public void TheBarMovesInWholeStepsSoItDoesNotRedrawEveryLook()
    {
        var hold = new StickHold();
        Look(hold, Back, 0);

        var distinct = new HashSet<float>();
        for (var milliseconds = 0; milliseconds < 5000; milliseconds += 33)
            distinct.Add(Look(hold, Back, milliseconds / 1000.0).Progress);

        Assert.True(distinct.Count <= StickHold.ProgressSteps, $"{distinct.Count} different bars.");
        Assert.Equal(0f, Look(new StickHold(), Back, 0).Progress);
        Assert.All(distinct, p => Assert.InRange(p, 0f, 1f));
    }
}
