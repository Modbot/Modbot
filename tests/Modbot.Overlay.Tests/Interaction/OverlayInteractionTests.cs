using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// The rules for holding the panel, with hands made up in code: grab and let go, where it stays,
/// how it resizes, the double grip that brings it back, and clicks.
/// </summary>
public class OverlayInteractionTests
{
    private static readonly Vector3 Centre = new(0.35f, -0.28f, -1.0f);

    private static TimeSpan At(int ms) => TimeSpan.FromMilliseconds(ms);

    private static Pose FromOrigin() => Hands.AimingAt(Vector3.Zero, Centre);

    [Fact]
    public void NobodyTrackedMeansNoPointerAndNoChange()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);

        var result = interaction.Update(OverlayTracking.None, At(0));

        Assert.Null(result.Pointer);
        Assert.False(result.PlacementChanged);
        Assert.Empty(result.Clicks);
        Assert.Equal(OverlayPlacement.Default, result.Placement);
    }

    [Fact]
    public void AHandPointingAtThePanelIsThePointer()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);

        var result = interaction.Update(Hands.RightOnly(Hands.Hand(FromOrigin())), At(0));

        Assert.NotNull(result.Pointer);
        Assert.Equal(Hand.Right, result.Pointer.Value.Hand);
        Assert.Equal(0.5f, result.Pointer.Value.Across, 2);
        Assert.Equal(0.5f, result.Pointer.Value.Down, 2);
    }

    [Fact]
    public void GrabbingMovesThePanelWithTheHandAndLettingGoLeavesItInTheWorld()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);
        var aim = FromOrigin();

        var taken = interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true)), At(0));
        Assert.Equal(Hand.Right, taken.Holding);
        Assert.Equal(OverlayAnchor.RightHand, taken.Placement.Anchor);
        Assert.True(taken.PlacementChanged);

        // The hand moves half a metre to the right, still gripping: the panel goes with it.
        var moved = aim with { Position = new Vector3(0.5f, 0, 0) };
        var held = interaction.Update(Hands.RightOnly(Hands.Hand(moved, grab: true)), At(100));
        Assert.Equal(Hand.Right, held.Holding);

        var released = interaction.Update(Hands.RightOnly(Hands.Hand(moved)), At(200));
        Assert.Null(released.Holding);
        Assert.Equal(OverlayAnchor.World, released.Placement.Anchor);
        Assert.True(released.PlacementChanged);

        var where = Pose.From(released.Placement.Offset).Position;
        Assert.True(Vector3.Distance(Centre + new Vector3(0.5f, 0, 0), where) < 1e-3f, $"panel ended at {where}");
    }

    [Fact]
    public void LettingGoWithThePanelAtTheHandKeepsItOnTheHand()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);

        // Reaching out: the hand is five centimetres in front of the panel, gripping it.
        var close = Hands.AimingAt(Centre + new Vector3(0, 0, 0.05f), Centre);
        interaction.Update(Hands.RightOnly(Hands.Hand(close, grab: true)), At(0));

        var released = interaction.Update(Hands.RightOnly(Hands.Hand(close)), At(100));

        Assert.Equal(OverlayAnchor.RightHand, released.Placement.Anchor);
        Assert.True(Pose.From(released.Placement.Offset).Position.Length() < OverlayInteraction.WristReach);
    }

    [Fact]
    public void ScrollingWhileHoldingResizesAndPushesWithinBounds()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);
        var aim = FromOrigin();
        interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true)), At(0));

        for (var i = 0; i < 1000; i++)
            interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true, scroll: new Vector2(1, 1))), At(i));

        Assert.Equal(OverlayPlacement.MaxWidth, interaction.Placement.Width);
        Assert.Equal(OverlayPlacement.MaxDistance, Pose.From(interaction.Placement.Offset).Position.Length(), 3);

        for (var i = 0; i < 1000; i++)
            interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true, scroll: new Vector2(-1, -1))), At(2000 + i));

        Assert.Equal(OverlayPlacement.MinWidth, interaction.Placement.Width);
        Assert.Equal(OverlayPlacement.MinDistance, Pose.From(interaction.Placement.Offset).Position.Length(), 3);
    }

    [Fact]
    public void TwoQuickGripsPutThePanelBackInFrontOfTheHead()
    {
        var far = OverlayPlacement.Default with { Anchor = OverlayAnchor.World, Offset = new OverlayPose(3, 0, -3), Width = 0.8f };
        var interaction = new OverlayInteraction(far);
        var aim = Hands.AimingAt(Vector3.Zero, new Vector3(3, 0, -3));

        interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true)), At(0));
        interaction.Update(Hands.RightOnly(Hands.Hand(aim)), At(100));
        var reset = interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true)), At(300));

        Assert.Equal(OverlayAnchor.Head, reset.Placement.Anchor);
        Assert.Equal(OverlayPlacement.Default.Offset, reset.Placement.Offset);
        Assert.Equal(0.8f, reset.Placement.Width);
        Assert.Null(reset.Holding);
    }

    [Fact]
    public void AGripLongAfterTheLastIsAnOrdinaryGrab()
    {
        var far = OverlayPlacement.Default with { Anchor = OverlayAnchor.World, Offset = new OverlayPose(3, 0, -3) };
        var interaction = new OverlayInteraction(far);
        var aim = Hands.AimingAt(Vector3.Zero, new Vector3(3, 0, -3));

        interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true)), At(0));
        interaction.Update(Hands.RightOnly(Hands.Hand(aim)), At(100));
        var again = interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true)), At(2000));

        Assert.Equal(Hand.Right, again.Holding);
    }

    [Fact]
    public void TheTriggerClicksOnceWhereThePointerIs()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);
        var aim = FromOrigin();

        var pressed = interaction.Update(Hands.RightOnly(Hands.Hand(aim, click: true)), At(0));
        var stillPressed = interaction.Update(Hands.RightOnly(Hands.Hand(aim, click: true)), At(50));

        var click = Assert.Single(pressed.Clicks);
        Assert.Equal(0.5f, click.Across, 2);
        Assert.Empty(stillPressed.Clicks);
    }

    [Fact]
    public void ScrollingWhilePointingButNotHoldingIsPassedOn()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);

        var result = interaction.Update(Hands.RightOnly(Hands.Hand(FromOrigin(), scroll: new Vector2(0, -0.6f))), At(0));

        Assert.Equal(new Vector2(0, -0.6f), result.Scroll);
        Assert.False(result.PlacementChanged);
    }

    [Fact]
    public void TheNearerOfTwoPointingHandsWins()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default);
        var far = Hands.Hand(Hands.AimingAt(new Vector3(0, 0, 1), Centre));
        var near = Hands.Hand(Hands.AimingAt(new Vector3(0, 0, -0.5f), Centre));

        var result = interaction.Update(Hands.Both(far, near), At(0));

        Assert.Equal(Hand.Right, result.Pointer!.Value.Hand);
    }
}
