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

        // The dimmest words, which are the small ones: lifted toward the palette's own text colour
        // only as far as they need to read on the panel and on a button, and not at all when they
        // already do.
        colours = colours with
        {
            Subtext = OverlayColours.LiftedToRead(
                colours.Subtext,
                colours.Text,
                OverlayColours.LeastSmallContrast,
                colours.Panel,
                colours.Button),
        };

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
        // two pixel edge, and the corners it has. A notch bigger than it was, because VRChat's own
        // menu is read from further off than a window is.
        var density = new Density(
            RowHeight: 52,
            ControlHeight: 36,
            TextBase: 18,
            TextSmall: 15,
            TextTiny: 13,
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
    public const double FrameRadius = 14;

    /// <summary>How round a card, a row and a tab are.</summary>
    public const double CardRadius = 10;

    /// <summary>How round a small pill is.</summary>
    public const double PillRadius = 8;

    /// <summary>The edge on the panel, the cards, the rows, the tabs and the buttons.</summary>
    public const double EdgeWidth = 2;

    /// <summary>
    /// How wide and tall one of the round icon buttons in the title strip is: the size VRChat's
    /// side panels give theirs, as near as it can be read off a picture.
    /// </summary>
    public const double RoundButton = 40;

    /// <summary>How far in from the window's edge the panel is drawn, so its shadow has room to fall.</summary>
    public const double ShadowRoom = 8;

    private static readonly PaletteColour White = new(0xFF, 0xFF, 0xFF);

    private readonly Dictionary<(PaletteColour Fill, PaletteColour Edge), IBrush> _raised = [];

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

    /// <summary>The icon colour as it reads on the cards' edge colour, which a person's stand-in picture is filled with.</summary>
    public IBrush IconOnEdge => Solid(OverlayColours.LiftedToRead(
        Colours.Icon,
        Colours.Text,
        OverlayColours.LeastContrast,
        Colours.Border));

    /// <summary>
    /// The fill of the tab showing: the highlights colour itself, at full strength, where the
    /// <see cref="Selected"/> fill is a notch darker.
    /// </summary>
    public IBrush Bright => Solid(Colours.SelectedBorder);

    /// <summary>Words on <see cref="Bright"/>: the palette's text while it reads there, else near-white or near-black.</summary>
    public IBrush BrightText => Solid(OverlayColours.ReadableOn(Colours.SelectedBorder, Colours.Text));

    /// <summary>
    /// The edge of a button, a tab or a chip as VRChat draws one standing up: lighter along the top,
    /// darker along the bottom. Worked out from the button colour and its edge, so it follows any palette.
    /// </summary>
    public IBrush RaisedEdge => Raised(Colours.Button, Colours.Border);

    /// <summary>The same edge for the one that is on: the highlights colour, lighter on top and darker below.</summary>
    public IBrush RaisedBrightEdge => Raised(Colours.SelectedBorder, Colours.SelectedBorder);

    /// <summary>The soft shadow under the panel. Falls into <see cref="ShadowRoom"/>.</summary>
    /// <remarks>
    /// Its reach is the offset plus the blur, 8 px at the bottom and less elsewhere, which is exactly
    /// <see cref="ShadowRoom"/>, so the window's edge never cuts it. Change one, change the other.
    /// </remarks>
    public BoxShadows PanelShadow => new(new BoxShadow { OffsetX = 0, OffsetY = ShadowOffset, Blur = ShadowRoom - ShadowOffset, Color = ShadowColour(0xA0) });

    private const double ShadowOffset = 2;

    /// <summary>The softer one under a row, a tab or a chip.</summary>
    public BoxShadows CardShadow => new(new BoxShadow { OffsetX = 0, OffsetY = 2, Blur = 5, Color = ShadowColour(0x70) });

    /// <summary>
    /// A top-to-bottom edge: <paramref name="edge"/> lightened at the top, <paramref name="fill"/>
    /// darkened at the bottom. One brush for each pair, kept.
    /// </summary>
    private IBrush Raised(PaletteColour fill, PaletteColour edge)
    {
        if (_raised.TryGetValue((fill, edge), out var known))
            return known;

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(ToColor(edge.Toward(White, 0.35)), 0),
                new GradientStop(ToColor(fill.Times(0.5)), 1),
            },
        };

        return _raised[(fill, edge)] = brush;
    }

    /// <summary>A shadow is the bar's colour taken a long way down, so it is dark in every palette and never black on a dark one.</summary>
    private Color ShadowColour(byte alpha)
    {
        var dark = Colours.Bar.Times(0.2);
        return Color.FromArgb(alpha, dark.R, dark.G, dark.B);
    }

    /// <summary>The dark, half see-through fill the mock gives a small pill, so it reads on any button colour.</summary>
    public static IBrush PillGround { get; } = new ImmutableSolidColorBrush(Color.FromArgb(0x88, 0, 0, 0));

    internal static Color ToColor(PaletteColour colour) => Color.FromRgb(colour.R, colour.G, colour.B);

    private static IBrush Solid(PaletteColour colour) => DesignTokens.Brush(ToColor(colour));

    private static IBrush Ground(PaletteColour colour, byte alpha)
        => alpha == 255
            ? Solid(colour)
            : DesignTokens.Brush(Color.FromArgb(alpha, colour.R, colour.G, colour.B));
}
