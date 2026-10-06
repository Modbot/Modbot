using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The overlay's dimmest words are lifted until they read on the panel and on a button, and left
/// alone when they already do.
/// </summary>
public class OverlayColoursTests
{
    private static readonly PaletteColour Panel = new(0x18, 0x12, 0x24);
    private static readonly PaletteColour Button = new(0x2A, 0x20, 0x44);
    private static readonly PaletteColour Text = new(0xF0, 0xF0, 0xF8);

    [Fact]
    public void WordsThatAlreadyReadAreNotChanged()
    {
        var subtext = new PaletteColour(0xB0, 0xA8, 0xC8);

        var lifted = OverlayColours.LiftedToRead(subtext, Text, OverlayColours.LeastSmallContrast, Panel, Button);

        Assert.Equal(subtext, lifted);
    }

    [Fact]
    public void WordsThatDoNotReadAreMovedTowardTheTextColourOnlyAsFarAsTheyNeed()
    {
        var dim = new PaletteColour(0x50, 0x48, 0x68);

        Assert.True(PaletteColour.Contrast(dim, Button) < OverlayColours.LeastSmallContrast);

        var lifted = OverlayColours.LiftedToRead(dim, Text, OverlayColours.LeastSmallContrast, Panel, Button);

        Assert.True(PaletteColour.Contrast(lifted, Panel) >= OverlayColours.LeastSmallContrast);
        Assert.True(PaletteColour.Contrast(lifted, Button) >= OverlayColours.LeastSmallContrast);
        Assert.NotEqual(dim, lifted);
        Assert.NotEqual(Text, lifted);
    }

    [Fact]
    public void WordsThatCanNeverReadBecomeTheFallback()
    {
        // Nothing between the two colours reads on a ground the same colour as both.
        var grey = new PaletteColour(0x80, 0x80, 0x80);

        Assert.Equal(grey, OverlayColours.LiftedToRead(grey, grey, OverlayColours.LeastSmallContrast, grey));
    }

    [Fact]
    public void EveryGroundIsReadOnNotJustTheFirst()
    {
        var onDark = new PaletteColour(0x70, 0x70, 0x70);
        var light = new PaletteColour(0xC0, 0xC0, 0xC0);

        var lifted = OverlayColours.LiftedToRead(onDark, new PaletteColour(0xFF, 0xFF, 0xFF), OverlayColours.LeastSmallContrast, Panel, light);

        // No colour reads at 4.5 on both a near-black and a light grey: it ends at the fallback.
        Assert.Equal(new PaletteColour(0xFF, 0xFF, 0xFF), lifted);
    }
}
