using Modbot.Companion.Overlay;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// Where <strong>Put it back in front of me</strong> puts the panel. It is the button for a panel
/// nobody can find, so nothing it leaves behind may hide or strand the panel.
/// </summary>
public class StraightAheadTests
{
    private static readonly OverlayPlacement Ahead = OverlayPlacement.StraightAhead;

    [Fact]
    public void ItIsOnTheHeadStraightAheadAndBelowTheEyes()
    {
        Assert.Equal(OverlayAnchor.Head, Ahead.Anchor);
        Assert.Equal(0f, Ahead.Offset.X);
        Assert.InRange(Ahead.Offset.Y, -0.25f, -0.05f);
        Assert.InRange(-Ahead.Offset.Z, 0.6f, 1.0f);

        // Facing the eyes: no turn at all.
        Assert.Equal((0f, 0f, 0f, 1f), (Ahead.Offset.QX, Ahead.Offset.QY, Ahead.Offset.QZ, Ahead.Offset.QW));
    }

    [Fact]
    public void ItsTopEdgeIsNoHigherThanALittleAboveTheEyes()
    {
        // The texture is square, so the panel is as tall as it is wide.
        var top = Ahead.Offset.Y + (Ahead.Width / 2);
        var bottom = Ahead.Offset.Y - (Ahead.Width / 2);

        Assert.InRange(top, 0f, 0.15f);
        Assert.True(bottom > -0.5f);
    }

    [Fact]
    public void NothingThatHidesOrStrandsThePanelIsLeft()
    {
        Assert.Equal(OverlayPlacement.Default.Width, Ahead.Width);
        Assert.Equal(1f, Ahead.Opacity);
        Assert.Equal(0f, Ahead.Curve);
        Assert.False(Ahead.Locked);
        Assert.False(Ahead.ClickThrough);
    }

    [Fact]
    public void ItIsAlreadyInsideEveryBound()
        => Assert.Equal(Ahead, Ahead.Clamped());

    [Fact]
    public void ItSurvivesTheSettingsFile()
        => Assert.Equal(Ahead, OverlayPlacement.FromJson(Ahead.ToJson()));
}
