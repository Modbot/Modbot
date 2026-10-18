using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// Scrolling a list the way a phone does: the trigger held and dragged moves it with the ray, a
/// short tap is still a click, and either stick scrolls the list under the ray.
/// </summary>
public class ListDragTests
{
    private static readonly Vector3 Centre = new(0.35f, -0.28f, -1.0f);

    private static TimeSpan At(int ms) => TimeSpan.FromMilliseconds(ms);

    private static Pose Aim(float lower = 0f) => Hands.AimingAt(Vector3.Zero, Centre + new Vector3(0f, -lower, 0f));

    private static OverlayInteraction OnAList() => new(OverlayPlacement.Default) { IsScrollableAt = (_, _) => true };

    [Fact]
    public void ATapOnAListClicksWhenTheTriggerComesUpAndNotWhenItGoesDown()
    {
        var interaction = OnAList();

        var down = interaction.Update(Hands.RightOnly(Hands.Hand(Aim(), click: true)), At(0));
        Assert.Empty(down.Clicks);
        Assert.Null(down.Drag);

        var up = interaction.Update(Hands.RightOnly(Hands.Hand(Aim())), At(50));
        var click = Assert.Single(up.Clicks);
        Assert.Equal(0.5f, click.Across, 2);
        Assert.Equal(0.5f, click.Down, 2);
    }

    [Fact]
    public void APressAwayFromAnyListClicksAtOnceAsBefore()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default) { IsScrollableAt = (_, _) => false };

        var down = interaction.Update(Hands.RightOnly(Hands.Hand(Aim(), click: true)), At(0));

        Assert.Single(down.Clicks);
    }

    [Fact]
    public void AHandThatWandersALittleStillTaps()
    {
        var interaction = OnAList();
        interaction.Update(Hands.RightOnly(Hands.Hand(Aim(), click: true)), At(0));

        // Well under the dead zone: a few millimetres on a panel this size.
        var shaking = interaction.Update(Hands.RightOnly(Hands.Hand(Aim(0.003f), click: true)), At(33));
        Assert.Null(shaking.Drag);

        var up = interaction.Update(Hands.RightOnly(Hands.Hand(Aim(0.003f))), At(66));
        Assert.Single(up.Clicks);
    }

    [Fact]
    public void DraggingPastTheDeadZoneScrollsAndLettingGoDoesNotClick()
    {
        var interaction = OnAList();
        interaction.Update(Hands.RightOnly(Hands.Hand(Aim(), click: true)), At(0));

        var first = interaction.Update(Hands.RightOnly(Hands.Hand(Aim(0.1f), click: true)), At(33));
        var second = interaction.Update(Hands.RightOnly(Hands.Hand(Aim(0.2f), click: true)), At(66));

        // The ray went down the panel, so the list is told to go down with it.
        Assert.NotNull(first.Drag);
        Assert.NotNull(second.Drag);
        Assert.True(first.Drag!.Value.Down > 0f);
        Assert.Equal(0.5f, first.Drag.Value.From.Down, 2);

        // Every poll reports only what it moved, so the two add up to the whole way.
        var whole = first.Drag.Value.Down + second.Drag!.Value.Down;
        var end = interaction.Update(Hands.RightOnly(Hands.Hand(Aim(0.2f))), At(100));
        Assert.Empty(end.Clicks);
        Assert.Null(end.Drag);
        Assert.Equal(0.2f / OverlayPlacement.Default.Width, whole, 3);
    }

    [Fact]
    public void DraggingUpReportsANegativeMove()
    {
        var interaction = OnAList();
        interaction.Update(Hands.RightOnly(Hands.Hand(Aim(), click: true)), At(0));

        var moved = interaction.Update(Hands.RightOnly(Hands.Hand(Aim(-0.1f), click: true)), At(33));

        Assert.True(moved.Drag!.Value.Down < 0f);
    }

    [Fact]
    public void ADragThatComesBackToWhereItStartedIsStillADrag()
    {
        var interaction = OnAList();
        interaction.Update(Hands.RightOnly(Hands.Hand(Aim(), click: true)), At(0));
        interaction.Update(Hands.RightOnly(Hands.Hand(Aim(0.1f), click: true)), At(33));
        interaction.Update(Hands.RightOnly(Hands.Hand(Aim(), click: true)), At(66));

        var end = interaction.Update(Hands.RightOnly(Hands.Hand(Aim())), At(100));

        Assert.Empty(end.Clicks);
    }

    [Fact]
    public void ARayThatMissesThePanelPlaneMidDragStandsStillAndDoesNotEndTheDrag()
    {
        var interaction = OnAList();
        var away = Hands.AimingAt(Vector3.Zero, new Vector3(5f, 0f, 1f));
        interaction.Update(Hands.RightOnly(Hands.Hand(Aim(), click: true)), At(0));
        interaction.Update(Hands.RightOnly(Hands.Hand(Aim(0.1f), click: true)), At(33));

        var missed = interaction.Update(Hands.RightOnly(Hands.Hand(away, click: true)), At(66));
        var back = interaction.Update(Hands.RightOnly(Hands.Hand(Aim(0.1f), click: true)), At(99));
        var end = interaction.Update(Hands.RightOnly(Hands.Hand(Aim(0.1f))), At(132));

        Assert.Equal(0f, missed.Drag!.Value.Down);
        Assert.Equal(0f, back.Drag!.Value.Down, 4);
        Assert.Empty(end.Clicks);
    }

    [Fact]
    public void AFreshPressAfterADragIsATapAgain()
    {
        var interaction = OnAList();
        interaction.Update(Hands.RightOnly(Hands.Hand(Aim(), click: true)), At(0));
        interaction.Update(Hands.RightOnly(Hands.Hand(Aim(0.2f), click: true)), At(33));
        interaction.Update(Hands.RightOnly(Hands.Hand(Aim(0.2f))), At(66));

        interaction.Update(Hands.RightOnly(Hands.Hand(Aim(0.2f), click: true)), At(100));
        var up = interaction.Update(Hands.RightOnly(Hands.Hand(Aim(0.2f))), At(133));

        Assert.Single(up.Clicks);
    }

    [Fact]
    public void ThePanelBeingTakenDropsAPressWithoutAClick()
    {
        var interaction = OnAList();
        interaction.Update(Hands.RightOnly(Hands.Hand(Aim(), click: true)), At(0));

        var taken = interaction.Update(Hands.RightOnly(Hands.Hand(Aim(), click: true, grab: true)), At(33));
        Assert.Equal(Hand.Right, taken.Holding);

        interaction.Update(Hands.RightOnly(Hands.Hand(Aim())), At(66));
        var later = interaction.Update(Hands.RightOnly(Hands.Hand(Aim())), At(99));

        Assert.Empty(taken.Clicks);
        Assert.Empty(later.Clicks);
    }

    [Fact]
    public void ThePressingHandKeepsTheListWhileTheOtherHandIsNearer()
    {
        var interaction = OnAList();
        var far = Aim();
        var near = Hands.AimingAt(new Vector3(0f, 0f, -0.5f), Centre);

        interaction.Update(Hands.RightOnly(Hands.Hand(far, click: true)), At(0));
        var moved = interaction.Update(Hands.Both(Hands.Hand(near), Hands.Hand(Aim(0.1f), click: true)), At(33));

        Assert.NotNull(moved.Drag);
        Assert.Equal(Hand.Right, moved.Pointer!.Value.Hand);
    }

    [Fact]
    public void TheStickOfTheHandNotPointingScrollsToo()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);
        var away = Hands.AimingAt(Vector3.Zero, new Vector3(5f, 0f, 1f));

        // The left hand holds the ray; the right stick is the one pushed.
        var result = interaction.Update(
            Hands.Both(Hands.Hand(Aim()), Hands.Hand(away, scroll: new Vector2(0f, -0.6f))),
            At(0));

        Assert.Equal(Hand.Left, result.Pointer!.Value.Hand);
        Assert.Equal(-0.5f, result.Scroll.Y, 4);
    }

    [Fact]
    public void ThePointingHandsStickIsReadFirst()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);
        var away = Hands.AimingAt(Vector3.Zero, new Vector3(5f, 0f, 1f));

        var result = interaction.Update(
            Hands.Both(Hands.Hand(Aim(), scroll: new Vector2(0f, 1f)), Hands.Hand(away, scroll: new Vector2(0f, -1f))),
            At(0));

        Assert.Equal(1f, result.Scroll.Y, 4);
    }

    [Fact]
    public void AHandBusyWithAnotherPanelDoesNotScrollThisOne()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);
        var away = Hands.AimingAt(Vector3.Zero, new Vector3(5f, 0f, 1f));

        var result = interaction.Update(
            Hands.Both(Hands.Hand(Aim()), Hands.Hand(away, scroll: new Vector2(0f, -1f))),
            At(0),
            elsewhere: Hand.Right);

        Assert.Equal(Vector2.Zero, result.Scroll);
    }

    [Fact]
    public void EitherStickReadsTheSecondHandOnlyWhenTheFirstIsAtRest()
    {
        var rest = Hands.Hand(Pose.Identity, scroll: new Vector2(0.1f, 0.05f));
        var pushed = Hands.Hand(Pose.Identity, scroll: new Vector2(0f, 1f));
        var other = Hands.Hand(Pose.Identity, scroll: new Vector2(1f, 0f));

        Assert.Equal(new Vector2(1f, 0f), OverlayInteraction.EitherStick(rest, other));
        Assert.Equal(new Vector2(0f, 1f), OverlayInteraction.EitherStick(pushed, other));
        Assert.Equal(Vector2.Zero, OverlayInteraction.EitherStick(rest, HandState.Missing));
    }

    [Fact]
    public void TheListsRowHeightIsMeasuredFromWhatIsDrawn()
    {
        var screen = new OverlayScreen(
            "Cat Lounge",
            new Cached<InstanceContext>(
                new InstanceContext("39911", [.. Enumerable.Range(0, 6).Select(i => new RosterMember($"usr_{i}", $"Person {i}", RosterStanding.Member, 0, []))]),
                Freshness.Fresh,
                TimeSpan.Zero),
            Freshness.Fresh);

        var (height, rows, onGround) = AvaloniaTestHost.Run(() =>
        {
            var root = OverlayView.Build(screen);
            root.Measure(new Size(480, 640));
            root.Arrange(new Rect(0, 0, 480, 640));

            var list = OverlayTargets.Find(root).First(t => t.Target is OverlayTarget.Roster);
            var person = OverlayTargets.Find(root).First(t => t.Target is OverlayTarget.Person);

            return (OverlayTargets.RowHeight(root, person.Bounds.Center), person.Bounds.Height, OverlayTargets.RowHeight(root, new Point(2, 636)));
        });

        Assert.NotNull(height);

        // A row and the gap after it: a little more than the row alone.
        Assert.InRange(height!.Value, rows, rows * 2);
        Assert.Null(onGround);
    }
}
