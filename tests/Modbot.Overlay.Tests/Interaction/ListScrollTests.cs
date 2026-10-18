using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// Where a list is and how fast it is still going: a drag let go while moving flings on, the fling
/// slows and stops by itself and at the ends, a touch stops it, a stick scrolls by how far it is pushed.
/// </summary>
public class ListScrollTests
{
    private const double Max = 1000;

    private static TimeSpan At(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>A drag at a steady speed (pixels a second), one poll every 10 ms, up to <paramref name="until"/> ms.</summary>
    private static ListScroll DraggedAt(double pixelsPerSecond, double until, double max = Max)
    {
        var scroll = new ListScroll();
        for (var ms = 10.0; ms <= until; ms += 10)
            scroll.Drag(pixelsPerSecond * 0.010, max, At(ms));
        return scroll;
    }

    [Fact]
    public void ADragMovesTheListByTheDistanceTheRayMoved()
    {
        var scroll = new ListScroll();

        scroll.Drag(40, Max, At(10));
        scroll.Drag(15, Max, At(20));

        Assert.Equal(55, scroll.Offset, 6);
        Assert.False(scroll.Flinging);
    }

    [Fact]
    public void ADragLetGoWhileMovingStartsAFlingAtTheSpeedItHad()
    {
        var scroll = DraggedAt(800, 200);

        scroll.Release(At(200));

        Assert.True(scroll.Flinging);
        Assert.Equal(800, scroll.Velocity, 3);
    }

    [Fact]
    public void ADragUpwardsFlingsUpwards()
    {
        var scroll = new ListScroll();
        scroll.Drag(900, Max, At(0));
        for (var ms = 10.0; ms <= 200; ms += 10)
            scroll.Drag(-5, Max, At(ms));

        scroll.Release(At(200));

        Assert.True(scroll.Velocity < 0);
    }

    [Fact]
    public void OnlyTheLastMomentsOfADragSetTheSpeed()
    {
        // Fast at first, then slowly over the last 100 ms: the fling follows the end of the drag.
        var scroll = DraggedAt(3000, 200);
        for (var ms = 210.0; ms <= 300; ms += 10)
            scroll.Drag(1, Max, At(ms));

        scroll.Release(At(300));

        Assert.True(Math.Abs(scroll.Velocity) < 200);
    }

    [Fact]
    public void ADragThatStoppedBeforeItWasLetGoGivesNoFling()
    {
        var scroll = DraggedAt(800, 200);

        // The hand rests for longer than the window, with no more movement reported.
        scroll.Release(At(200 + ListScroll.Window.TotalMilliseconds + 50));

        Assert.False(scroll.Flinging);
        Assert.Equal(0, scroll.Velocity);
    }

    [Fact]
    public void ADragThatKeptPollingWithoutMovingGivesNoFling()
    {
        var scroll = DraggedAt(800, 200);
        for (var ms = 210.0; ms <= 400; ms += 10)
            scroll.Drag(0, Max, At(ms));

        scroll.Release(At(400));

        Assert.False(scroll.Flinging);
    }

    [Fact]
    public void ADragAfterAReleaseWithoutAnyMovementGivesNoFling()
    {
        var scroll = new ListScroll();

        scroll.Release(At(10));

        Assert.False(scroll.Flinging);
    }

    [Fact]
    public void AFlingNeverStartsFasterThanTheTopSpeed()
    {
        var scroll = DraggedAt(60000, 100, max: 1_000_000);

        scroll.Release(At(100));

        Assert.Equal(ListScroll.TopSpeed, scroll.Velocity, 3);
    }

    [Fact]
    public void AFlingSlowsDownAndStopsBelowTheStopSpeed()
    {
        var scroll = DraggedAt(2000, 200, max: 1_000_000);
        scroll.Release(At(200));

        var speeds = new List<double>();
        var steps = 0;
        while (scroll.Flinging && steps < 10_000)
        {
            Assert.True(scroll.Step(0.011, 1_000_000));
            speeds.Add(Math.Abs(scroll.Velocity));
            steps++;
        }

        Assert.False(scroll.Flinging);
        Assert.True(steps < 10_000);
        Assert.Equal(0, scroll.Velocity);
        // Each step is slower than the one before, until it stops.
        for (var i = 1; i < speeds.Count - 1; i++)
            Assert.True(speeds[i] < speeds[i - 1]);
    }

    [Fact]
    public void AFlingGoesTheSameDistanceWhateverTheStepSize()
    {
        var slow = DraggedAt(2000, 200, max: 1_000_000);
        var fast = DraggedAt(2000, 200, max: 1_000_000);
        slow.Release(At(200));
        fast.Release(At(200));
        var start = slow.Offset;

        for (var i = 0; i < 100; i++)
            slow.Step(0.033, 1_000_000);
        for (var i = 0; i < 300; i++)
            fast.Step(0.011, 1_000_000);

        // The same 3.3 seconds, one polled at 30 Hz and one at about 90 Hz.
        Assert.Equal(slow.Offset - start, fast.Offset - start, 1);
    }

    [Fact]
    public void AFlingDownStopsAtTheBottom()
    {
        var scroll = DraggedAt(3000, 100, max: 500);
        scroll.Release(At(100));
        Assert.True(scroll.Flinging);

        for (var i = 0; i < 200 && scroll.Flinging; i++)
            scroll.Step(0.011, 500);

        Assert.Equal(500, scroll.Offset, 6);
        Assert.False(scroll.Flinging);
    }

    [Fact]
    public void AFlingUpStopsAtTheTop()
    {
        var scroll = new ListScroll();
        scroll.Drag(400, 2000, At(0));
        for (var ms = 10.0; ms <= 100; ms += 10)
            scroll.Drag(-30, 2000, At(ms));
        scroll.Release(At(100));
        Assert.True(scroll.Velocity < 0);

        for (var i = 0; i < 500 && scroll.Flinging; i++)
            scroll.Step(0.011, 2000);

        Assert.Equal(0, scroll.Offset, 6);
        Assert.False(scroll.Flinging);
    }

    [Fact]
    public void ADragNeverPassesEitherEnd()
    {
        var scroll = new ListScroll();

        scroll.Drag(5000, 300, At(10));
        Assert.Equal(300, scroll.Offset, 6);

        scroll.Drag(-9000, 300, At(20));
        Assert.Equal(0, scroll.Offset, 6);
    }

    [Fact]
    public void ATouchStopsAFlingWhereItIs()
    {
        var scroll = DraggedAt(2000, 200, max: 1_000_000);
        scroll.Release(At(200));
        scroll.Step(0.05, 1_000_000);
        var where = scroll.Offset;
        Assert.True(scroll.Flinging);

        scroll.Touch();

        Assert.False(scroll.Flinging);
        Assert.False(scroll.Step(0.05, 1_000_000));
        Assert.Equal(where, scroll.Offset);
    }

    [Fact]
    public void ATouchForgetsWhatADragBeforeItDid()
    {
        var scroll = DraggedAt(2000, 200);

        scroll.Touch();
        scroll.Release(At(205));

        Assert.False(scroll.Flinging);
    }

    [Fact]
    public void AStickMovesTheListByHowFarItIsPushed()
    {
        var half = new ListScroll();
        var full = new ListScroll();

        half.Push(0.5, 0.1, Max);
        full.Push(1, 0.1, Max);

        Assert.Equal(ListScroll.StickSpeed * 0.1, full.Offset, 6);
        Assert.Equal(full.Offset / 2, half.Offset, 6);
    }

    [Fact]
    public void AStickPushedUpMovesTheListUpAndStopsAtTheTop()
    {
        var scroll = new ListScroll();
        scroll.Push(1, 0.2, Max);
        var down = scroll.Offset;

        scroll.Push(-1, 0.05, Max);
        Assert.True(scroll.Offset < down);

        scroll.Push(-1, 5, Max);
        Assert.Equal(0, scroll.Offset, 6);
    }

    [Fact]
    public void AStickStopsAtTheBottom()
    {
        var scroll = new ListScroll();

        scroll.Push(1, 5, 300);

        Assert.Equal(300, scroll.Offset, 6);
    }

    [Fact]
    public void AStickStopsAFlingAndLettingItGoLeavesTheListStill()
    {
        var scroll = DraggedAt(2000, 200, max: 1_000_000);
        scroll.Release(At(200));
        Assert.True(scroll.Flinging);

        scroll.Push(0.3, 0.011, 1_000_000);

        Assert.False(scroll.Flinging);
    }

    [Fact]
    public void ClampBringsTheListBackWhenItsContentGotShorter()
    {
        var scroll = new ListScroll();
        scroll.Push(1, 0.5, Max);

        scroll.Clamp(100);

        Assert.Equal(100, scroll.Offset, 6);
        Assert.False(scroll.Flinging);
    }

    [Fact]
    public void ClampToNothingToScrollPutsTheListAtTheTop()
    {
        var scroll = new ListScroll();
        scroll.Push(1, 0.5, Max);

        scroll.Clamp(-20);

        Assert.Equal(0, scroll.Offset);
    }

    [Fact]
    public void ResetGoesBackToTheTopAndStill()
    {
        var scroll = DraggedAt(2000, 200);
        scroll.Release(At(200));
        Assert.True(scroll.Flinging);

        scroll.Reset();

        Assert.Equal(0, scroll.Offset);
        Assert.False(scroll.Flinging);
        scroll.Release(At(205));
        Assert.False(scroll.Flinging);
    }
}
