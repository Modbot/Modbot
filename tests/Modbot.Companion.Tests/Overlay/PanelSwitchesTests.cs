using System.Text.Json.Nodes;
using Modbot.Companion.Overlay;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// The lock and the click-through saved with each headset panel, and the notification panel's
/// place when a hand put it somewhere: written, read back, and kept across a restart.
/// </summary>
public class PanelSwitchesTests
{
    [Fact]
    public void TheMainPanelsSwitchesAreWrittenAndReadBack()
    {
        var placement = OverlayPlacement.Default with { Locked = true, ClickThrough = true };

        var read = OverlayPlacement.FromJson(JsonNode.Parse(placement.ToJson().ToJsonString()));

        Assert.True(read.Locked);
        Assert.True(read.ClickThrough);
        Assert.Equal(placement, read);
    }

    [Fact]
    public void AnOlderFileWithNoSwitchesLeavesBothOff()
    {
        var read = OverlayPlacement.FromJson(JsonNode.Parse("""{ "anchor": "head", "width": 0.5 }"""));

        Assert.False(read.Locked);
        Assert.False(read.ClickThrough);
    }

    [Fact]
    public void ANotificationPanelLetGoOnTheHeadIsKeptWhereItWasLeft()
    {
        var letGo = new OverlayPlacement(OverlayAnchor.Head, new OverlayPose(-0.3f, -0.2f, -0.9f, 0.1f, 0f, 0f, 0.995f), 0.5f, Locked: true);

        var settings = NotifyOverlaySettings.Default.WithPlacement(letGo);
        var placed = settings.ToPlacement();

        Assert.NotNull(settings.Placed);
        Assert.Equal(OverlayAnchor.Head, placed.Anchor);
        Assert.Equal(letGo.Offset.X, placed.Offset.X, 4);
        Assert.Equal(letGo.Offset.Y, placed.Offset.Y, 4);
        Assert.Equal(letGo.Offset.Z, placed.Offset.Z, 4);
        Assert.Equal(letGo.Offset.QX, placed.Offset.QX, 4);
        Assert.Equal(letGo.Width, placed.Width, 4);
        Assert.True(placed.Locked);
    }

    [Fact]
    public void WhileItIsCarriedOnlyTheSwitchesAreTaken()
    {
        var carried = new OverlayPlacement(OverlayAnchor.RightHand, new OverlayPose(0f, 0f, -0.4f), 0.5f, ClickThrough: true);

        var settings = NotifyOverlaySettings.Default.WithPlacement(carried);

        Assert.Null(settings.Placed);
        Assert.True(settings.ClickThrough);
        Assert.Equal(NotifyOverlaySettings.Default.Width, settings.Width);
    }

    [Fact]
    public void TheDistanceSlidesAPlacedPanelAlongTheLineFromTheHead()
    {
        var letGo = new OverlayPlacement(OverlayAnchor.Head, new OverlayPose(0.3f, 0f, -0.4f), 0.4f);
        var settings = NotifyOverlaySettings.Default.WithPlacement(letGo);

        Assert.Equal(0.5f, settings.Distance, 4);

        var further = (settings with { Distance = 1f }).ToPlacement();

        Assert.Equal(0.6f, further.Offset.X, 4);
        Assert.Equal(-0.8f, further.Offset.Z, 4);
    }

    [Fact]
    public void ThePlacedPanelIsWrittenAndReadBack()
    {
        var letGo = new OverlayPlacement(OverlayAnchor.Head, new OverlayPose(-0.3f, -0.2f, -0.9f, 0.1f, 0f, 0f, 0.995f), 0.5f, Locked: true, ClickThrough: true);
        var settings = NotifyOverlaySettings.Default.WithPlacement(letGo);

        var read = NotifyOverlaySettings.FromJson(JsonNode.Parse(settings.ToJson().ToJsonString()));

        Assert.Equal(settings, read);
    }

    [Fact]
    public void ChoosingASpotAgainPutsItBackOnTheSpot()
    {
        var letGo = new OverlayPlacement(OverlayAnchor.Head, new OverlayPose(-0.3f, -0.2f, -0.9f), 0.5f);
        var placed = NotifyOverlaySettings.Default.WithPlacement(letGo);

        var onSpot = (placed with { Spot = ScreenSpot.BottomLeft, Placed = null }).ToPlacement();

        Assert.True(onSpot.Offset.X < 0f);
        Assert.True(onSpot.Offset.Y < 0f);
        Assert.Equal(0f, onSpot.Offset.QX);
    }

    [Fact]
    public void ThePopUpsKeepTheirSizeNowThatTheBarIsUnderThem()
    {
        // The width a moderator set is the pop-ups' box; the panel is a little wider for the bar,
        // so the pop-ups themselves are drawn as big as they were before there was one.
        var placement = NotifyOverlaySettings.Default.ToPlacement();

        Assert.Equal(NotifyOverlaySettings.DefaultWidth * NotifyOverlaySettings.PanelPerBox, placement.Width, 4);
        Assert.Equal(
            NotifyOverlaySettings.DefaultWidth,
            placement.Width * NotifyOverlaySettings.BoxPixels / NotifyOverlaySettings.PanelPixels,
            4);
    }

    [Fact]
    public void AnUnreadablePlacedPoseIsNoPlaceAtAll()
    {
        var read = NotifyOverlaySettings.FromJson(JsonNode.Parse("""{ "placed": { "x": "left" } }"""));

        Assert.Null(read.Placed);
        Assert.False(read.Locked);
    }
}
