using Avalonia.Media;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The pieces of VRChat's look that are worked out from a palette's colours: the dimmest text, the
/// raised edges and the shadow, in a dark palette and in a light one.
/// </summary>
public class VRChatLookTests
{
    private static OverlayColours Colours(
        PaletteColour? panel = null,
        PaletteColour? button = null,
        PaletteColour? subtext = null,
        PaletteColour? text = null)
        => new(
            Panel: panel ?? new PaletteColour(0x18, 0x12, 0x24),
            Bar: new(0x10, 0x0C, 0x1A),
            Button: button ?? new PaletteColour(0x2A, 0x20, 0x44),
            Border: new(0x38, 0x2C, 0x5A),
            Hover: new(0x30, 0x26, 0x4E),
            Selected: new(0x50, 0x3A, 0x90),
            SelectedBorder: new(0x80, 0x60, 0xE0),
            SelectedText: new(0xF5, 0xF5, 0xF5),
            Icon: new(0xA0, 0x90, 0xD0),
            Text: text ?? new PaletteColour(0xF0, 0xF0, 0xF8),
            Subtext: subtext ?? new PaletteColour(0xB0, 0xA8, 0xC8));

    /// <summary>A palette at the light end: the darkened shares still leave a mid grey panel and buttons.</summary>
    private static OverlayColours LightColours() => Colours(
        panel: new(0xA8, 0xA8, 0xB4),
        button: new(0x88, 0x88, 0x98),
        subtext: new(0x60, 0x60, 0x70),
        text: new(0x10, 0x10, 0x18));

    private static double Brightness(Color colour)
        => new PaletteColour(colour.R, colour.G, colour.B).Brightness;

    [Fact]
    public void TheDimmestTextIsLiftedUntilItReadsOnThePanelAndOnAButton()
    {
        var dim = new PaletteColour(0x50, 0x48, 0x68);
        var colours = Colours(subtext: dim);

        var look = AvaloniaTestHost.Run(() => OverlayLook.FromColours(colours));
        var drawn = look.VRChat!.Colours.Subtext;

        Assert.NotEqual(dim, drawn);
        Assert.True(PaletteColour.Contrast(drawn, colours.Panel) >= OverlayColours.LeastSmallContrast);
        Assert.True(PaletteColour.Contrast(drawn, colours.Button) >= OverlayColours.LeastSmallContrast);
        Assert.Equal(VRChatLookColour(drawn), look.Tokens.Palette.TextDim);
    }

    private static Color VRChatLookColour(PaletteColour colour) => Color.FromRgb(colour.R, colour.G, colour.B);

    [Fact]
    public void TextThatAlreadyReadsIsTheOneThePaletteGave()
    {
        var colours = Colours();

        var look = AvaloniaTestHost.Run(() => OverlayLook.FromColours(colours));

        Assert.Equal(colours.Subtext, look.VRChat!.Colours.Subtext);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARaisedEdgeIsLighterAlongTheTopThanTheBottomInAnyPalette(bool light)
    {
        var colours = light ? LightColours() : Colours();

        var (top, bottom, topBright, bottomBright) = AvaloniaTestHost.Run(() =>
        {
            var v = OverlayLook.FromColours(colours).VRChat!;
            var plain = Assert.IsType<LinearGradientBrush>(v.RaisedEdge);
            var bright = Assert.IsType<LinearGradientBrush>(v.RaisedBrightEdge);
            return (
                Brightness(plain.GradientStops[0].Color),
                Brightness(plain.GradientStops[1].Color),
                Brightness(bright.GradientStops[0].Color),
                Brightness(bright.GradientStops[1].Color));
        });

        Assert.True(top > bottom);
        Assert.True(topBright > bottomBright);
    }

    [Fact]
    public void TheSameEdgeIsOneBrushNotANewOneEachTime()
    {
        var (first, second) = AvaloniaTestHost.Run(() =>
        {
            var v = OverlayLook.FromColours(Colours()).VRChat!;
            return (v.RaisedEdge, v.RaisedEdge);
        });

        Assert.Same(first, second);
    }

    [Fact]
    public void TheShadowIsDarkAndSeeThrough()
    {
        var shadow = AvaloniaTestHost.Run(() => OverlayLook.FromColours(Colours()).VRChat!.PanelShadow[0]);

        Assert.InRange(shadow.Color.A, 1, 254);
        Assert.True(Brightness(shadow.Color) < 0.02);
    }

    [Fact]
    public void TheTabShowingIsTheHighlightsColourWithWordsThatReadOnIt()
    {
        var colours = Colours();

        var (fill, words) = AvaloniaTestHost.Run(() =>
        {
            var v = OverlayLook.FromColours(colours).VRChat!;
            return (Assert.IsAssignableFrom<ISolidColorBrush>(v.Bright).Color, Assert.IsAssignableFrom<ISolidColorBrush>(v.BrightText).Color);
        });

        Assert.Equal(VRChatLookColour(colours.SelectedBorder), fill);
        Assert.True(PaletteColour.Contrast(new PaletteColour(words.R, words.G, words.B), colours.SelectedBorder) >= OverlayColours.LeastContrast);
    }

    [Fact]
    public void ALightPaletteDrawsRowsTabsAndChipsWithoutThrowing()
    {
        var screen = new OverlayScreen(
            "Cat Lounge",
            new Cached<InstanceContext>(
                new InstanceContext("39911", [new RosterMember("usr_a", "Kai", RosterStanding.Staff, 0, [])]),
                Freshness.Fresh,
                TimeSpan.Zero),
            Freshness.Fresh);

        var pixels = AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(480, 640);
            return renderer.Render(OverlayView.Build(screen, null, OverlayLook.FromColours(LightColours()))).ToArray();
        });

        Assert.Equal(480 * 640 * 4, pixels.Length);
        Assert.True(pixels.Chunk(4).Select(p => (p[0], p[1], p[2])).Distinct().Count() > 3);
    }
}
