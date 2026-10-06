using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Modbot.Companion.Presentation;

namespace Modbot.Overlay.Views;

/// <summary>
/// How the overlay's screens are drawn: the headset's look as it has always been, or, on the
/// desktop window only, the look of VRChat's own menu in the palette the person selected there.
/// </summary>
/// <remarks>
/// <para>The headset panel is always <see cref="Headset"/> and never asks for anything else, so what
/// is drawn in a headset is exactly what it was before this existed. The desktop window asks for
/// <see cref="FromColours"/> while it has a usable palette, and for <see cref="Headset"/> when it
/// has none, which is the look that window has always had.</para>
/// <para>Plain drawing values; nothing is read here and nothing is sent. Made on the UI thread,
/// because the brushes are.</para>
/// </remarks>
public sealed class OverlayLook
{
    private OverlayLook(DesignTokens tokens, VRChatLook? vrchat)
    {
        Tokens = tokens;
        VRChat = vrchat;
    }

    /// <summary>The headset's tokens: the VR palette at the VR density. What every panel used before.</summary>
    public static OverlayLook Headset { get; } = new(DesignTokens.Vr, null);

    /// <summary>
    /// The look of VRChat's own menu for these colours. The headset's red, yellow and green for
    /// danger, warning and good stay as they are: those are what the data means, not decoration.
    /// </summary>
    public static OverlayLook FromColours(OverlayColours colours)
    {
        ArgumentNullException.ThrowIfNull(colours);

        static Color c(PaletteColour colour) => VRChatLook.ToColor(colour);

        var palette = ModbotPalette.VrDark with
        {
            Background = c(colours.Panel),
            Surface = c(colours.Button),
            Surface2 = c(colours.Button),
            Surface3 = c(colours.Button),
            Border = c(colours.Border),
            Border2 = c(colours.Border),
            Text = c(colours.Text),
            TextDim = c(colours.Subtext),
            TextFaint = c(colours.Subtext),
            Accent = c(colours.SelectedBorder),
            AccentForeground = c(colours.Icon),
            AccentDim = c(colours.Hover),
        };

        // The same scale as the headset's, one notch smaller: text from the mock's own sizes, a
        // two pixel edge, and the corners it has.
        var density = new Density(
            RowHeight: 48,
            ControlHeight: 30,
            TextBase: 16,
            TextSmall: 14,
            TextTiny: 12,
            Hairline: VRChatLook.EdgeWidth,
            Radius: VRChatLook.CardRadius);

        return new OverlayLook(DesignTokens.Create(palette, density), new VRChatLook(colours));
    }

    public DesignTokens Tokens { get; }

    /// <summary>The VRChat look's own colours and shapes, or null for the headset's.</summary>
    public VRChatLook? VRChat { get; }
}

/// <summary>
/// The parts of VRChat's menu that the headset's tokens have no word for: the title strip, the
/// selected tab, the icon colour and the shapes the mock gives them.
/// </summary>
/// <remarks>
/// The corner roundness and the spacing are by eye, from a picture of VRChat's menu; they are
/// constants here so that a closer look changes one number.
/// </remarks>
public sealed class VRChatLook
{
    /// <summary>How round the panel's own corners are.</summary>
    public const double FrameRadius = 12;

    /// <summary>How round a card, a row and a tab are.</summary>
    public const double CardRadius = 10;

    /// <summary>How round a button in the title strip is.</summary>
    public const double ButtonRadius = 8;

    /// <summary>How round a small pill is.</summary>
    public const double PillRadius = 6;

    /// <summary>The edge on the panel, the cards, the rows, the tabs and the buttons.</summary>
    public const double EdgeWidth = 2;

    internal VRChatLook(OverlayColours colours) => Colours = colours;

    public OverlayColours Colours { get; }

    /// <summary>The panel behind everything, thinned out to <paramref name="alpha"/>.</summary>
    public IBrush Panel(byte alpha = 255) => Ground(Colours.Panel, alpha);

    /// <summary>The title strip, thinned out to <paramref name="alpha"/>.</summary>
    public IBrush Bar(byte alpha = 255) => Ground(Colours.Bar, alpha);

    /// <summary>The panel's own edge, drawn in the cards' edge colour.</summary>
    public IBrush Edge => Solid(Colours.Border);

    public IBrush Button => Solid(Colours.Button);

    public IBrush Hover => Solid(Colours.Hover);

    public IBrush Selected => Solid(Colours.Selected);

    public IBrush SelectedEdge => Solid(Colours.SelectedBorder);

    public IBrush SelectedText => Solid(Colours.SelectedText);

    public IBrush Icon => Solid(Colours.Icon);

    public IBrush Text => Solid(Colours.Text);

    public IBrush Subtext => Solid(Colours.Subtext);

    /// <summary>The dark, half see-through fill the mock gives a small pill, so it reads on any button colour.</summary>
    public static IBrush PillGround { get; } = new ImmutableSolidColorBrush(Color.FromArgb(0x88, 0, 0, 0));

    internal static Color ToColor(PaletteColour colour) => Color.FromRgb(colour.R, colour.G, colour.B);

    private static IBrush Solid(PaletteColour colour) => DesignTokens.Brush(ToColor(colour));

    private static IBrush Ground(PaletteColour colour, byte alpha)
        => alpha == 255
            ? Solid(colour)
            : DesignTokens.Brush(Color.FromArgb(alpha, colour.R, colour.G, colour.B));
}
