using System.Text.Json.Nodes;
using Modbot.Companion.Overlay;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// How tall and how wide a panel has been made with its corner: saved with its place, read back,
/// kept between 1 and 2, and always 1 on a hand.
/// </summary>
public class ResizeShapeTests
{
    [Fact]
    public void ThePanelIsItsOrdinaryShapeUntilItIsMadeBigger()
    {
        Assert.Equal(1f, OverlayPlacement.Default.Wide);
        Assert.Equal(1f, OverlayPlacement.Default.Tall);
        Assert.Equal(1f, NotifyOverlaySettings.Default.Wide);
        Assert.Equal(1f, NotifyOverlaySettings.Default.Tall);
    }

    [Fact]
    public void TheShapeIsWrittenAndReadBack()
    {
        var placement = OverlayPlacement.Default with { Wide = 1.5f, Tall = 1.25f };

        var json = placement.ToJson();
        var read = OverlayPlacement.FromJson(JsonNode.Parse(json.ToJsonString()));

        Assert.Equal(1.5f, (float)json["wide"]!);
        Assert.Equal(1.25f, (float)json["tall"]!);
        Assert.Equal(1.5f, read.Wide);
        Assert.Equal(1.25f, read.Tall);
        Assert.Equal(placement, read);
    }

    [Fact]
    public void AnOlderFileWithNoShapeLeavesBothAtOne()
    {
        var read = OverlayPlacement.FromJson(JsonNode.Parse("""{ "anchor": "head", "width": 0.5 }"""));

        Assert.Equal(1f, read.Wide);
        Assert.Equal(1f, read.Tall);
    }

    [Theory]
    [InlineData(0.2f, 0.5f, 1f, 1f)]
    [InlineData(-3f, 0f, 1f, 1f)]
    [InlineData(2.5f, 9f, 2f, 2f)]
    [InlineData(1.5f, 2f, 1.5f, 2f)]
    public void AShapeOutsideOneToTwoIsBroughtInside(float wide, float tall, float expectedWide, float expectedTall)
    {
        var clamped = (OverlayPlacement.Default with { Wide = wide, Tall = tall }).Clamped();

        Assert.Equal(expectedWide, clamped.Wide);
        Assert.Equal(expectedTall, clamped.Tall);
    }

    [Fact]
    public void AHandEditedFileCannotMakeTheShapeTooBigOrSmall()
    {
        var read = OverlayPlacement.FromJson(JsonNode.Parse("""{ "anchor": "head", "wide": 7, "tall": 0.1 }"""));

        Assert.Equal(2f, read.Wide);
        Assert.Equal(1f, read.Tall);
    }

    [Fact]
    public void TheSquareIsAsLongAsTheLongerSide()
    {
        var placement = OverlayPlacement.Default with { Width = 0.4f, Wide = 1.25f, Tall = 1.5f };

        Assert.Equal(1.5f, placement.Longest);
        Assert.Equal(0.6f, placement.Side, 4);
        Assert.Equal(0.4f, (OverlayPlacement.Default with { Width = 0.4f }).Side, 4);
    }

    [Fact]
    public void APanelOnAHandIsAlwaysReadAtTheOrdinaryShape()
    {
        var read = OverlayPlacement.FromJson(JsonNode.Parse("""{ "anchor": "righthand", "width": 0.16, "wide": 1.8, "tall": 2 }"""));

        Assert.Equal(OverlayAnchor.RightHand, read.Anchor);
        Assert.Equal(1f, read.Wide);
        Assert.Equal(1f, read.Tall);
    }

    [Fact]
    public void ThePopUpPanelsShapeIsWrittenAndReadBack()
    {
        var settings = NotifyOverlaySettings.Default with { Wide = 1.75f, Tall = 2f };

        var json = settings.ToJson();
        var read = NotifyOverlaySettings.FromJson(JsonNode.Parse(json.ToJsonString()));

        Assert.Equal(1.75f, (float)json["wide"]!);
        Assert.Equal(2f, (float)json["tall"]!);
        Assert.Equal(settings, read);
    }

    [Fact]
    public void ThePopUpPanelsShapeIsKeptBetweenOneAndTwo()
    {
        var clamped = (NotifyOverlaySettings.Default with { Wide = 5f, Tall = 0.3f }).Clamped();

        Assert.Equal(2f, clamped.Wide);
        Assert.Equal(1f, clamped.Tall);
    }

    [Fact]
    public void ThePopUpPanelCarriesItsShapeToTheRuntimeAndBack()
    {
        var settings = NotifyOverlaySettings.Default with { Wide = 1.5f, Tall = 1.25f };

        var placed = settings.ToPlacement();

        Assert.Equal(1.5f, placed.Wide);
        Assert.Equal(1.25f, placed.Tall);

        var back = NotifyOverlaySettings.Default.WithPlacement(placed);

        Assert.Equal(1.5f, back.Wide);
        Assert.Equal(1.25f, back.Tall);
    }

    [Fact]
    public void APopUpPanelLetGoOnTheHeadKeepsTheShapeItWasMadeAt()
    {
        var letGo = new OverlayPlacement(OverlayAnchor.Head, new OverlayPose(-0.3f, -0.2f, -0.9f), 0.5f, Wide: 2f, Tall: 1.5f);

        var settings = NotifyOverlaySettings.Default.WithPlacement(letGo);
        var read = NotifyOverlaySettings.FromJson(JsonNode.Parse(settings.ToJson().ToJsonString()));

        Assert.Equal(2f, settings.Wide);
        Assert.Equal(1.5f, settings.Tall);
        Assert.Equal(2f, read.ToPlacement().Wide);
        Assert.Equal(1.5f, read.ToPlacement().Tall);
    }

    [Fact]
    public void TheSpotPlacementCarriesTheShapeToo()
    {
        var home = (NotifyOverlaySettings.Default with { Wide = 1.5f, Tall = 2f }).SpotPlacement();

        Assert.Equal(1.5f, home.Wide);
        Assert.Equal(2f, home.Tall);
    }
}
