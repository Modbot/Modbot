using Modbot.Companion.Overlay;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// Where a panel worn on a wrist is drawn while that hand's controller is gone. A Quest's
/// controllers drop out whenever they are put down, and the panel used to wait on the headset at
/// the wrist's own offset, which put it in the moderator's face.
/// </summary>
public class WhileTheHandIsGoneTests
{
    private static OverlayPlacement OnTheWrist(OverlayAnchor hand) => OverlayPlacement.Default with
    {
        Anchor = hand,
        Offset = OverlayPlacement.WristOffset,
        Width = OverlayPlacement.WristWidth,
        Opacity = 0.8f,
        Curve = 0.2f,
    };

    [Theory]
    [InlineData(OverlayAnchor.LeftHand)]
    [InlineData(OverlayAnchor.RightHand)]
    public void AWristPanelWaitsInFrontOfTheHeadAtAReadableSize(OverlayAnchor hand)
    {
        var waiting = OnTheWrist(hand).WhileTheHandIsGone();

        Assert.Equal(OverlayAnchor.Head, waiting.Anchor);
        Assert.Equal(OverlayPlacement.Default.Offset, waiting.Offset);
        Assert.Equal(OverlayPlacement.Default.Width, waiting.Width);
    }

    [Fact]
    public void OpacityAndCurveStay()
    {
        var waiting = OnTheWrist(OverlayAnchor.LeftHand).WhileTheHandIsGone();

        Assert.Equal(0.8f, waiting.Opacity);
        Assert.Equal(0.2f, waiting.Curve);
    }

    [Fact]
    public void AHandPanelMadeWideKeepsItsWidth()
    {
        var wide = OnTheWrist(OverlayAnchor.RightHand) with { Width = 0.6f };

        Assert.Equal(0.6f, wide.WhileTheHandIsGone().Width);
    }

    [Theory]
    [InlineData(OverlayAnchor.Head)]
    [InlineData(OverlayAnchor.World)]
    public void APanelNotOnAHandHasNothingToWaitFor(OverlayAnchor anchor)
    {
        var placement = OverlayPlacement.Default with { Anchor = anchor, Offset = new OverlayPose(1f, 1f, -2f) };

        Assert.Same(placement, placement.WhileTheHandIsGone());
    }
}
