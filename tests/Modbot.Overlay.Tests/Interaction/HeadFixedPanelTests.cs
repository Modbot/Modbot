using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// The notification panel's rule for letting go: it goes back onto the head, where the hand left
/// it as seen from the eyes, rather than staying in the room or going on a wrist.
/// </summary>
public class HeadFixedPanelTests
{
    private static TimeSpan At(int ms) => TimeSpan.FromMilliseconds(ms);

    private static readonly OverlayPlacement Corner = NotifyOverlaySettings.Default.ToPlacement();

    private static Vector3 CornerCentre => new(Corner.Offset.X, Corner.Offset.Y, Corner.Offset.Z);

    private static OverlayInteraction HeadFixed(OverlayPlacement? placement = null)
        => new(placement ?? Corner, home: NotifyOverlaySettings.Default.SpotPlacement(), keepOnHead: true);

    [Fact]
    public void LetGoItStaysOnTheHeadWhereTheHandLeftIt()
    {
        var interaction = HeadFixed();
        var start = Hands.AimingAt(Vector3.Zero, CornerCentre);
        interaction.Update(Hands.RightOnly(Hands.Hand(start, grab: true)), At(0));

        // Carried 20 cm to the left, then let go.
        var moved = start with { Position = new Vector3(-0.2f, 0, 0) };
        interaction.Update(Hands.RightOnly(Hands.Hand(moved, grab: true)), At(33));
        var result = interaction.Update(Hands.RightOnly(Hands.Hand(moved)), At(66));

        Assert.Null(result.Holding);
        Assert.Equal(OverlayAnchor.Head, result.Placement.Anchor);
        Assert.Equal(Corner.Offset.X - 0.2f, result.Placement.Offset.X, 3);
        Assert.Equal(Corner.Offset.Y, result.Placement.Offset.Y, 3);
        Assert.Equal(Corner.Offset.Z, result.Placement.Offset.Z, 3);
    }

    [Fact]
    public void WhereItIsKeptIsMeasuredFromTheHeadNotTheRoom()
    {
        // The head has stepped a metre to the right while the panel was carried. Where it is let
        // go is measured from there, so it follows the head from then on.
        var interaction = HeadFixed();
        var start = Hands.AimingAt(Vector3.Zero, CornerCentre);
        interaction.Update(Hands.RightOnly(Hands.Hand(start, grab: true)), At(0));

        var head = new Pose(new Vector3(1f, 0, 0), Quaternion.Identity);
        var result = interaction.Update(
            new OverlayTracking(head, HandState.Missing, Hands.Hand(start)),
            At(33));

        Assert.Equal(OverlayAnchor.Head, result.Placement.Anchor);
        Assert.Equal(Corner.Offset.X - 1f, result.Placement.Offset.X, 3);
    }

    [Fact]
    public void BroughtToTheWristItStillGoesBackOnTheHead()
    {
        var interaction = HeadFixed();
        var aim = Hands.AimingAt(Vector3.Zero, CornerCentre);

        // Held from right against the panel, so the panel is within reach of the wrist.
        var device = new Pose(CornerCentre + new Vector3(0, 0, 0.05f), Quaternion.Identity);
        interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true, device: device)), At(0));
        var result = interaction.Update(Hands.RightOnly(Hands.Hand(aim, device: device)), At(33));

        Assert.Equal(OverlayAnchor.Head, result.Placement.Anchor);
        Assert.NotEqual(OverlayPlacement.WristWidth, result.Placement.Width);
    }

    [Fact]
    public void AHandThatVanishesMidCarryPutsItBackWhereItWasPickedUp()
    {
        var interaction = HeadFixed();
        var start = Hands.AimingAt(Vector3.Zero, CornerCentre);
        interaction.Update(Hands.RightOnly(Hands.Hand(start, grab: true)), At(0));

        var result = interaction.Update(OverlayTracking.None, At(33));

        Assert.Equal(Corner.Anchor, result.Placement.Anchor);
        Assert.Equal(Corner.Offset, result.Placement.Offset);
    }

    [Fact]
    public void TwoQuickGripsSendItToItsSpotAndKeepItsSize()
    {
        var wide = Corner with
        {
            Offset = new OverlayPose(-0.3f, -0.4f, -0.8f),
            Width = 0.6f,
        };
        var interaction = HeadFixed(wide);
        var aim = Hands.AimingAt(Vector3.Zero, new Vector3(-0.3f, -0.4f, -0.8f));

        interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true)), At(0));
        interaction.Update(Hands.RightOnly(Hands.Hand(aim)), At(100));
        var result = interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true)), At(200));

        var home = NotifyOverlaySettings.Default.SpotPlacement();
        Assert.Equal(OverlayAnchor.Head, result.Placement.Anchor);
        Assert.Equal(home.Offset, result.Placement.Offset);
        Assert.Equal(0.6f, result.Placement.Width, 3);
    }
}
