namespace Modbot.Companion.Presentation;

/// <summary>
/// The colours the desktop overlay is drawn in when it takes after VRChat's own menu, worked out
/// from the six colours of the palette selected in VRChat.
/// </summary>
/// <remarks>
/// <para><strong>Measured and guessed.</strong> The shares below were measured on VRChat's own menu
/// with the Thy Kingdom palette selected. The button fill, its border and the selected tab are good
/// readings. The panel and the title strip are estimates, close to what was seen but not exact.
/// All of them are here and nowhere else, so a look at another palette can correct a number in one
/// place.</para>
/// <para><strong>A palette that cannot be read is not used.</strong> <see cref="From"/> gives null,
/// and the overlay is drawn as it always was, when a colour is missing, or when the words would be
/// hard to read: a palette is VRChat's idea of readable on VRChat's own menu, and this panel
/// carries more small text than that menu does.</para>
/// <para>Plain arithmetic; nothing is read and nothing is sent.</para>
/// </remarks>
public sealed record OverlayColours(
    PaletteColour Panel,
    PaletteColour Bar,
    PaletteColour Button,
    PaletteColour Border,
    PaletteColour Hover,
    PaletteColour Selected,
    PaletteColour SelectedBorder,
    PaletteColour SelectedText,
    PaletteColour Icon,
    PaletteColour Text,
    PaletteColour Subtext)
{
    /// <summary>The panel behind everything: the palette's backgrounds colour, darkened. An estimate.</summary>
    public const double PanelShare = 0.38;

    /// <summary>The strip along the top: the backgrounds colour, darkened further. An estimate.</summary>
    public const double BarShare = 0.25;

    /// <summary>A button, a row and a card: the buttons colour, darkened.</summary>
    public const double ButtonShare = 0.6;

    /// <summary>Their edge: the buttons colour, a little less darkened than the fill.</summary>
    public const double BorderShare = 0.8;

    /// <summary>The selected tab: the highlights colour, a little darkened. Its edge is the highlights colour itself.</summary>
    public const double SelectedShare = 0.82;

    /// <summary>
    /// How far a row's fill goes toward its border while the mouse is over it. The mock has no
    /// hover, so this is a quiet step in the palette's own colours, small enough that the words on
    /// the row read as well as they did.
    /// </summary>
    public const double HoverPart = 0.5;

    /// <summary>The lowest contrast the words may have against the panel and against the button fill.</summary>
    public const double LeastContrast = 3.0;

    /// <summary>
    /// The lowest contrast small words may have where a palette's dimmest text sits on the panel and
    /// on a button. A step up from <see cref="LeastContrast"/>, because the row's small words, such as
    /// when somebody joined, are read at a glance and VRChat's own menu has no such words.
    /// </summary>
    public const double LeastSmallContrast = 4.5;

    /// <summary>The two colours a selected tab's words fall back to when the palette's text would not read on it.</summary>
    public static readonly PaletteColour LightWords = new(0xF5, 0xF5, 0xF5);

    public static readonly PaletteColour DarkWords = new(0x10, 0x10, 0x10);

    /// <summary>
    /// The colours for a palette, or null when it should not be used: none was read, or the words
    /// would be below <see cref="LeastContrast"/> against the panel or against the button fill.
    /// </summary>
    public static OverlayColours? From(VRChatPalette? palette)
    {
        if (palette is null)
            return null;

        var panel = palette.Backgrounds.Times(PanelShare);
        var button = palette.Buttons.Times(ButtonShare);
        var border = palette.Buttons.Times(BorderShare);
        var selected = palette.Highlights.Times(SelectedShare);

        if (PaletteColour.Contrast(palette.Text, panel) < LeastContrast
            || PaletteColour.Contrast(palette.Text, button) < LeastContrast)
        {
            return null;
        }

        return new OverlayColours(
            Panel: panel,
            Bar: palette.Backgrounds.Times(BarShare),
            Button: button,
            Border: border,
            Hover: button.Toward(border, HoverPart),
            Selected: selected,
            SelectedBorder: palette.Highlights,
            SelectedText: ReadableOn(selected, palette.Text),
            Icon: palette.Icons,
            Text: palette.Text,
            Subtext: palette.Subtext);
    }

    /// <summary>
    /// <paramref name="words"/> as they are while they read at <paramref name="least"/> on every ground,
    /// and otherwise moved toward <paramref name="toward"/>, a tenth at a time, until they do.
    /// </summary>
    /// <remarks>
    /// Keeps as much of the palette's own colour as the contrast allows, so a palette whose dim text
    /// is already clear of the panel is drawn exactly as VRChat has it.
    /// </remarks>
    public static PaletteColour LiftedToRead(PaletteColour words, PaletteColour toward, double least, params PaletteColour[] grounds)
    {
        ArgumentNullException.ThrowIfNull(grounds);

        for (var tenth = 0; tenth <= 10; tenth++)
        {
            var candidate = words.Toward(toward, tenth / 10.0);

            if (grounds.All(ground => PaletteColour.Contrast(candidate, ground) >= least))
                return candidate;
        }

        return toward;
    }

    /// <summary>
    /// The palette's own text colour while it reads on <paramref name="ground"/>, and otherwise
    /// whichever of near-white and near-black reads better. A selected tab is the one place the
    /// text sits on the highlights colour, which VRChat's menu does not have to read on.
    /// </summary>
    public static PaletteColour ReadableOn(PaletteColour ground, PaletteColour preferred)
    {
        if (PaletteColour.Contrast(preferred, ground) >= LeastContrast)
            return preferred;

        return PaletteColour.Contrast(LightWords, ground) >= PaletteColour.Contrast(DarkWords, ground)
            ? LightWords
            : DarkWords;
    }
}
