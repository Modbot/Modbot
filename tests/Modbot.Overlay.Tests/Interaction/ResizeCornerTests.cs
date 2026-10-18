using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// The resize corner at the panel's bottom right: a trigger pressed on it and dragged makes the
/// panel taller and wider, between 1 and 2 times, with its top left staying where it is in the
/// room. Locked and click-through panels have no corner, and a press elsewhere is a tap as ever.
/// </summary>
public class ResizeCornerTests
{
    private static readonly Vector3 Centre = new(0.35f, -0.28f, -1.0f);

    private const float Tolerance = 0.002f;

    private static TimeSpan At(int ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>The corner as the host would say it: the last tenth across and the last tenth down.</summary>
    private static bool Corner(float across, float down) => across > 0.9f && down > 0.9f;

    /// <summary>
    /// A point on a head-fixed, unturned panel at <paramref name="across"/> and <paramref name="down"/>
    /// (fractions of its square), in the room, with the head at the origin.
    /// </summary>
    private static Vector3 On(OverlayPlacement panel, float across, float down)
        => new(
            panel.Offset.X - (panel.Side / 2f) + (across * panel.Side),
            panel.Offset.Y + (panel.Side / 2f) - (down * panel.Side),
            panel.Offset.Z);

    private static HandState Pressing(Vector3 at) => Hands.Hand(Hands.AimingAt(Vector3.Zero, at), click: true);

    private static OverlayInteraction With(OverlayPlacement placement)
        => new(placement) { IsOnCorner = Corner };

    /// <summary>Presses on the corner of the panel as it is now, and drags the ray by (<paramref name="x"/>, <paramref name="y"/>) metres.</summary>
    private static InteractionResult Drag(OverlayInteraction interaction, float x, float y)
    {
        var from = On(interaction.Placement, 0.95f, 0.95f);
        interaction.Update(Hands.RightOnly(Pressing(from)), At(0));
        return interaction.Update(Hands.RightOnly(Pressing(from + new Vector3(x, y, 0f))), At(33));
    }

    [Fact]
    public void TheDefaultPanelStartsWhereTheNumbersSay()
    {
        // The numbers the rest of these tests are worked out from.
        var panel = OverlayPlacement.Default;

        Assert.Equal(0.45f, panel.Side, 4);
        Assert.Equal(0.5525f, On(panel, 0.95f, 0.95f).X, Tolerance);
        Assert.Equal(-0.4825f, On(panel, 0.95f, 0.95f).Y, Tolerance);
    }

    [Fact]
    public void PressingOnTheCornerIsNotATap()
    {
        var interaction = With(OverlayPlacement.Default);

        var result = interaction.Update(Hands.RightOnly(Pressing(On(OverlayPlacement.Default, 0.95f, 0.95f))), At(0));

        Assert.Empty(result.Clicks);
        Assert.False(result.PlacementChanged);
        Assert.Null(result.Holding);
    }

    [Fact]
    public void DraggingTheCornerMakesThePanelTallerAndWiderAndKeepsItsTopLeftStill()
    {
        var interaction = With(OverlayPlacement.Default);
        var topLeft = On(OverlayPlacement.Default, 0f, 0f);

        // A quarter of the width again, in each direction: the width is 0.45 m, so 0.225 m is half of it.
        var result = Drag(interaction, 0.225f, -0.225f);

        Assert.True(result.PlacementChanged);
        Assert.Equal(1.5f, result.Placement.Wide, Tolerance);
        Assert.Equal(1.5f, result.Placement.Tall, Tolerance);
        Assert.Equal(0.45f, result.Placement.Width, 4);
        Assert.Equal(0.4625f, result.Placement.Offset.X, Tolerance);
        Assert.Equal(-0.3925f, result.Placement.Offset.Y, Tolerance);
        Assert.Equal(-1f, result.Placement.Offset.Z, Tolerance);

        var after = On(result.Placement, 0f, 0f);
        Assert.Equal(topLeft.X, after.X, Tolerance);
        Assert.Equal(topLeft.Y, after.Y, Tolerance);
        Assert.Equal(topLeft.Z, after.Z, Tolerance);
    }

    [Fact]
    public void TheWidthAndTheHeightAreDraggedApart()
    {
        var interaction = With(OverlayPlacement.Default);

        // Only wider: the ray moves right and stays level.
        var result = Drag(interaction, 0.225f, 0f);

        Assert.Equal(1.5f, result.Placement.Wide, Tolerance);
        Assert.Equal(1f, result.Placement.Tall, Tolerance);

        // The square is as long as the longer side, so the middle moved by half of that growth.
        Assert.Equal(0.4625f, result.Placement.Offset.X, Tolerance);
        Assert.Equal(-0.3925f, result.Placement.Offset.Y, Tolerance);
    }

    [Fact]
    public void ThePanelCannotBeMadeMoreThanTwiceItsShape()
    {
        var interaction = With(OverlayPlacement.Default);
        var topLeft = On(OverlayPlacement.Default, 0f, 0f);

        var result = Drag(interaction, 2f, -2f);

        Assert.Equal(2f, result.Placement.Wide, Tolerance);
        Assert.Equal(2f, result.Placement.Tall, Tolerance);

        var after = On(result.Placement, 0f, 0f);
        Assert.Equal(topLeft.X, after.X, Tolerance);
        Assert.Equal(topLeft.Y, after.Y, Tolerance);
    }

    [Fact]
    public void ThePanelCannotBeMadeSmallerThanItsOrdinaryShape()
    {
        var big = OverlayPlacement.Default with { Wide = 1.5f, Tall = 1.5f };
        var interaction = With(big);
        var topLeft = On(big, 0f, 0f);

        var result = Drag(interaction, -2f, 2f);

        Assert.Equal(1f, result.Placement.Wide, Tolerance);
        Assert.Equal(1f, result.Placement.Tall, Tolerance);

        var after = On(result.Placement, 0f, 0f);
        Assert.Equal(topLeft.X, after.X, Tolerance);
        Assert.Equal(topLeft.Y, after.Y, Tolerance);
    }

    [Fact]
    public void LettingGoOfTheTriggerKeepsTheShapeAndEndsTheResize()
    {
        var interaction = With(OverlayPlacement.Default);
        var grown = Drag(interaction, 0.225f, -0.225f).Placement;

        // Trigger up, then the ray moves a long way without it: nothing follows.
        var far = Hands.Hand(Hands.AimingAt(Vector3.Zero, new Vector3(0.9f, -0.9f, -1f)));
        var released = interaction.Update(Hands.RightOnly(far), At(66));
        var later = interaction.Update(Hands.RightOnly(far), At(99));

        Assert.Equal(grown, released.Placement);
        Assert.Equal(grown, later.Placement);
        Assert.False(later.PlacementChanged);
        Assert.Empty(released.Clicks);
    }

    [Fact]
    public void ThePanelFollowsTheRayStepByStepAndNotJustAtTheEnd()
    {
        var interaction = With(OverlayPlacement.Default);
        var from = On(OverlayPlacement.Default, 0.95f, 0.95f);
        interaction.Update(Hands.RightOnly(Pressing(from)), At(0));

        var first = interaction.Update(Hands.RightOnly(Pressing(from + new Vector3(0.1125f, -0.1125f, 0f))), At(33));
        Assert.Equal(1.25f, first.Placement.Wide, Tolerance);

        // The ray goes back to where it took hold: the panel is its ordinary size again.
        var back = interaction.Update(Hands.RightOnly(Pressing(from)), At(66));
        Assert.Equal(1f, back.Placement.Wide, Tolerance);
        Assert.Equal(1f, back.Placement.Tall, Tolerance);
        Assert.Equal(OverlayPlacement.Default.Offset.X, back.Placement.Offset.X, Tolerance);
        Assert.Equal(OverlayPlacement.Default.Offset.Y, back.Placement.Offset.Y, Tolerance);
    }

    [Fact]
    public void ALockedPanelCannotBeResized()
    {
        var locked = OverlayPlacement.Default with { Locked = true };
        var interaction = With(locked);

        var result = Drag(interaction, 0.225f, -0.225f);

        Assert.False(result.PlacementChanged);
        Assert.Equal(locked, result.Placement);
    }

    [Fact]
    public void APanelLettingRaysThroughCannotBeResized()
    {
        var through = OverlayPlacement.Default with { ClickThrough = true };
        var interaction = With(through);
        interaction.IsOnBar = (_, _) => false;

        var result = Drag(interaction, 0.225f, -0.225f);

        Assert.False(result.PlacementChanged);
        Assert.Equal(through, result.Placement);
    }

    [Fact]
    public void APressOffTheCornerIsATapAsEver()
    {
        var interaction = With(OverlayPlacement.Default);

        var result = interaction.Update(Hands.RightOnly(Pressing(Centre)), At(0));

        Assert.Single(result.Clicks);
        Assert.False(result.PlacementChanged);
    }

    [Fact]
    public void WithoutACornerAPressInTheSpotIsATap()
    {
        // The corner is only there in edit mode, and the host leaves the callback off the rest of the time.
        var interaction = new OverlayInteraction(OverlayPlacement.Default);

        var result = interaction.Update(Hands.RightOnly(Pressing(On(OverlayPlacement.Default, 0.95f, 0.95f))), At(0));

        Assert.Single(result.Clicks);
    }

    [Fact]
    public void TheOtherHandsGripDoesNotTakeThePanelWhileTheCornerIsHeld()
    {
        var interaction = With(OverlayPlacement.Default);
        var from = On(OverlayPlacement.Default, 0.95f, 0.95f);
        var left = Hands.AimingAt(new Vector3(-0.2f, 0f, 0f), Centre);

        interaction.Update(Hands.RightOnly(Pressing(from)), At(0));

        // The right ray goes right off the square while the left hand squeezes its grip.
        var result = interaction.Update(
            Hands.Both(Hands.Hand(left, grab: true), Pressing(from + new Vector3(0.5f, -0.5f, 0f))),
            At(33));

        Assert.Null(result.Holding);
        Assert.Equal(OverlayAnchor.Head, result.Placement.Anchor);
        Assert.Equal(2f, result.Placement.Wide, Tolerance);
        Assert.Equal(2f, result.Placement.Tall, Tolerance);
    }

    [Fact]
    public void AfterTheCornerIsLetGoTheGripTakesThePanelAgain()
    {
        var interaction = With(OverlayPlacement.Default);
        Drag(interaction, 0.225f, -0.225f);
        var onPanel = Hands.Hand(Hands.AimingAt(Vector3.Zero, On(interaction.Placement, 0.5f, 0.5f)), grab: true);

        var result = interaction.Update(Hands.RightOnly(onPanel), At(100));

        Assert.Equal(Hand.Right, result.Holding);
    }

    [Fact]
    public void ARoundOfTheShapeShareScalesHowFarTheRayHasToGo()
    {
        // A pop-up panel draws its boxes on 256 of 300 pixels of the width, so the same drag counts for more.
        var interaction = With(OverlayPlacement.Default);
        interaction.ShapeShare = 0.5f;

        var result = Drag(interaction, 0.1125f, -0.1125f);

        Assert.Equal(1.5f, result.Placement.Wide, Tolerance);
        Assert.Equal(1.5f, result.Placement.Tall, Tolerance);
    }

    [Fact]
    public void TwoQuickGripsKeepTheShapeOfAMainPanel()
    {
        var shaped = OverlayPlacement.Default with
        {
            Anchor = OverlayAnchor.World,
            Wide = 1.5f,
            Tall = 1.25f,
        };
        var interaction = With(shaped);
        var right = Hands.AimingAt(Vector3.Zero, Centre);

        interaction.Update(Hands.RightOnly(Hands.Hand(right, grab: true)), At(0));
        interaction.Update(Hands.RightOnly(Hands.Hand(right)), At(100));
        interaction.Update(Hands.RightOnly(Hands.Hand(right, grab: true)), At(200));

        Assert.Equal(OverlayAnchor.Head, interaction.Placement.Anchor);
        Assert.Equal(1.5f, interaction.Placement.Wide, Tolerance);
        Assert.Equal(1.25f, interaction.Placement.Tall, Tolerance);
    }

    [Theory]
    [InlineData(false, 1.5f)]
    [InlineData(true, 1f)]
    public void TwoQuickGripsOnAPopUpPanelResetTheShapeOnlyWhereTheHomeResetsTheSize(bool homeResetsWidth, float expected)
    {
        var shaped = OverlayPlacement.Default with { Wide = 1.5f, Tall = 1.5f };
        var interaction = new OverlayInteraction(shaped, OverlayPlacement.Default, OverlayAnchor.Head, homeResetsWidth);
        var right = Hands.AimingAt(Vector3.Zero, Centre);

        interaction.Update(Hands.RightOnly(Hands.Hand(right, grab: true)), At(0));
        interaction.Update(Hands.RightOnly(Hands.Hand(right)), At(100));
        interaction.Update(Hands.RightOnly(Hands.Hand(right, grab: true)), At(200));

        Assert.Equal(expected, interaction.Placement.Wide, Tolerance);
        Assert.Equal(expected, interaction.Placement.Tall, Tolerance);
    }

    [Fact]
    public void ALetGoPanelInTheRoomKeepsItsShape()
    {
        var shaped = OverlayPlacement.Default with { Wide = 1.5f, Tall = 1.25f };
        var interaction = With(shaped);
        var aim = Hands.AimingAt(Vector3.Zero, Centre);

        interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true)), At(0));
        var held = interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true)), At(50));
        Assert.Equal(1.5f, held.Placement.Wide, Tolerance);

        var released = interaction.Update(Hands.RightOnly(Hands.Hand(aim)), At(100));

        Assert.Equal(OverlayAnchor.World, released.Placement.Anchor);
        Assert.Equal(1.5f, released.Placement.Wide, Tolerance);
        Assert.Equal(1.25f, released.Placement.Tall, Tolerance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BringingAShapedPanelToTheWristPutsItBackToItsOrdinaryShape(bool left)
    {
        var interaction = With(OverlayPlacement.Default with { Wide = 1.5f, Tall = 1.75f });

        var close = Hands.Hand(Hands.AimingAt(Centre + new Vector3(0, 0, 0.05f), Centre), grab: true);
        var letGo = close with { Grab = false };

        interaction.Update(left ? Hands.Both(close, HandState.Missing) : Hands.RightOnly(close), At(0));
        var released = interaction.Update(left ? Hands.Both(letGo, HandState.Missing) : Hands.RightOnly(letGo), At(100));

        Assert.Equal(left ? OverlayAnchor.LeftHand : OverlayAnchor.RightHand, released.Placement.Anchor);
        Assert.Equal(OverlayPlacement.WristWidth, released.Placement.Width);
        Assert.Equal(1f, released.Placement.Wide);
        Assert.Equal(1f, released.Placement.Tall);
    }
}
