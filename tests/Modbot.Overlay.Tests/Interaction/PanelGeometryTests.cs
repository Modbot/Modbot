using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>Poses compose the way the runtimes expect, and rays land where they should.</summary>
public class PanelGeometryTests
{
    private static readonly Vector3 Centre = new(0.35f, -0.28f, -1.0f);

    [Fact]
    public void APoseRelativeToAnotherComesBackWhenPlacedAgain()
    {
        var parent = new Pose(new Vector3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f));
        var pose = new Pose(new Vector3(-0.5f, 0.25f, -2), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.3f));

        var back = parent.Then(pose.RelativeTo(parent));

        Assert.True(Vector3.Distance(pose.Position, back.Position) < 1e-4f);
        Assert.True(MathF.Abs(Quaternion.Dot(pose.Rotation, back.Rotation)) > 0.9999f);
    }

    [Fact]
    public void ForwardIsMinusZTurnedByTheRotation()
    {
        var facingRight = new Pose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2));

        Assert.True(Vector3.Distance(Vector3.UnitX, facingRight.Forward) < 1e-5f);
        Assert.True(Vector3.Distance(-Vector3.UnitZ, Pose.Identity.Forward) < 1e-5f);
    }

    [Fact]
    public void TheDefaultPanelSitsInFrontOfTheHead()
    {
        var head = new Pose(new Vector3(0, 1.6f, 0), Quaternion.Identity);
        var tracking = new OverlayTracking(head, HandState.Missing, HandState.Missing);

        var panel = PanelGeometry.PanelPose(OverlayPlacement.Default, tracking);

        Assert.NotNull(panel);
        Assert.True(Vector3.Distance(new Vector3(0.35f, 1.32f, -1.0f), panel.Value.Position) < 1e-5f);
    }

    [Fact]
    public void AHandAnchoredPanelHasNoPlaceWhileTheHandIsNotTracked()
    {
        var placement = OverlayPlacement.Default with { Anchor = OverlayAnchor.LeftHand };

        Assert.Null(PanelGeometry.PanelPose(placement, OverlayTracking.None));
        Assert.NotNull(PanelGeometry.PanelPose(placement, Hands.Both(Hands.Hand(Pose.Identity), HandState.Missing)));
    }

    [Fact]
    public void ARayAtTheCentreLandsInTheMiddle()
    {
        var panel = PanelGeometry.PanelPose(OverlayPlacement.Default, OverlayTracking.None)!.Value;

        var hit = PanelGeometry.Hit(panel, OverlayPlacement.Default.Width, Hands.AimingAt(Vector3.Zero, Centre));

        Assert.NotNull(hit);
        Assert.True(hit.Value.IsOnPanel);
        Assert.Equal(0.5f, hit.Value.Across, 3);
        Assert.Equal(0.5f, hit.Value.Down, 3);
        Assert.Equal(Centre.Length(), hit.Value.Distance, 3);
    }

    [Fact]
    public void AcrossAndDownFollowThePanelsRightAndUp()
    {
        var panel = PanelGeometry.PanelPose(OverlayPlacement.Default, OverlayTracking.None)!.Value;
        var width = OverlayPlacement.Default.Width;

        // A quarter of the width to the right and a quarter up from the centre, on the panel's plane.
        var target = Centre + new Vector3(width / 4, width / 4, 0);
        var hit = PanelGeometry.Hit(panel, width, Hands.AimingAt(new Vector3(0, 0, 0.2f), target));

        Assert.NotNull(hit);
        Assert.Equal(0.75f, hit.Value.Across, 3);
        Assert.Equal(0.25f, hit.Value.Down, 3);
    }

    [Fact]
    public void ARayPastTheEdgeIsOffThePanel()
    {
        var panel = PanelGeometry.PanelPose(OverlayPlacement.Default, OverlayTracking.None)!.Value;
        var width = OverlayPlacement.Default.Width;

        var hit = PanelGeometry.Hit(panel, width, Hands.AimingAt(Vector3.Zero, Centre + new Vector3(width, 0, 0)));

        Assert.NotNull(hit);
        Assert.False(hit.Value.IsOnPanel);
    }

    [Fact]
    public void ARayFromBehindOrAwayNeverLands()
    {
        var panel = PanelGeometry.PanelPose(OverlayPlacement.Default, OverlayTracking.None)!.Value;
        var width = OverlayPlacement.Default.Width;

        // From behind the panel, pointing at its back.
        Assert.Null(PanelGeometry.Hit(panel, width, Hands.AimingAt(Centre + new Vector3(0, 0, -1), Centre)));

        // From in front, pointing the other way.
        Assert.Null(PanelGeometry.Hit(panel, width, Hands.AimingAt(Vector3.Zero, Vector3.UnitZ * 2)));
    }
}
