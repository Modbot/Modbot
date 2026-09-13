using Avalonia;
using Avalonia.Media;

namespace Modbot.Overlay;

/// <summary>
/// Modbot's colours, as the desktop and the headset each need them.
/// </summary>
/// <remarks>
/// <para>The dark palette is the default everywhere, because moderators work at night and in
/// headsets. <see cref="VrDark"/> is the same palette with the extremes pulled inward: headset
/// panels bloom at pure black and pure white, and low-contrast greys that read fine on a monitor
/// vanish entirely.</para>
/// <para>Most of these are <c>Modbot.Web/src/index.css</c>'s tokens by value, and
/// <c>DesignTokenDriftTests</c> fails naming any that drift. The extra surface steps and the
/// <c>…Dim</c> tints come from the reference prototype in <c>explore/design/index.html</c>, which
/// carries a slightly richer set than the stylesheet does — a table needs a third surface to
/// separate a header from a row from a hover, and a badge needs a background tint of its own
/// colour.</para>
/// </remarks>
public sealed record ModbotPalette
{
    public required Color Background { get; init; }

    /// <summary>Cards and panels: one step up from the page.</summary>
    public required Color Surface { get; init; }

    /// <summary>Controls and table headers: two steps up.</summary>
    public required Color Surface2 { get; init; }

    /// <summary>Hover and pressed states: three steps up.</summary>
    public required Color Surface3 { get; init; }

    public required Color Border { get; init; }

    /// <summary>The stronger border, for controls that need an edge of their own.</summary>
    public required Color Border2 { get; init; }

    public required Color Text { get; init; }

    public required Color TextDim { get; init; }

    /// <summary>Labels and counts: present, deliberately quiet.</summary>
    public required Color TextFaint { get; init; }

    public required Color Accent { get; init; }

    public required Color AccentForeground { get; init; }

    public required Color AccentDim { get; init; }

    public required Color Danger { get; init; }

    public required Color DangerDim { get; init; }

    public required Color Warn { get; init; }

    public required Color WarnDim { get; init; }

    public required Color Ok { get; init; }

    public required Color OkDim { get; init; }

    public required Color Info { get; init; }

    public required Color InfoDim { get; init; }

    /// <summary>The desktop dark palette: the client window, and the web UI's <c>.dark</c>.</summary>
    public static ModbotPalette Dark { get; } = new()
    {
        Background = Color.Parse("#0d0e12"),
        Surface = Color.Parse("#14161c"),
        Surface2 = Color.Parse("#1b1e26"),
        Surface3 = Color.Parse("#232733"),
        Border = Color.Parse("#272b36"),
        Border2 = Color.Parse("#343947"),
        Text = Color.Parse("#e6e8ee"),
        TextDim = Color.Parse("#9aa1b1"),
        TextFaint = Color.Parse("#6a7183"),
        Accent = Color.Parse("#7c6cf5"),
        AccentForeground = Color.Parse("#ffffff"),
        AccentDim = Color.Parse("#2a2547"),
        Danger = Color.Parse("#f0526a"),
        DangerDim = Color.Parse("#3a1922"),
        Warn = Color.Parse("#e8a33d"),
        WarnDim = Color.Parse("#3a2d16"),
        Ok = Color.Parse("#3fbf8f"),
        OkDim = Color.Parse("#13322a"),
        Info = Color.Parse("#4aa3f0"),
        InfoDim = Color.Parse("#12283d"),
    };

    /// <summary>
    /// The headset palette. Only the five values the web UI overrides for VR differ; everything
    /// else is <see cref="Dark"/>, so the two surfaces stay recognisably one product.
    /// </summary>
    public static ModbotPalette VrDark { get; } = Dark with
    {
        Background = Color.Parse("#15171e"),
        Surface = Color.Parse("#1c1f28"),
        Text = Color.Parse("#f2f4f8"),
        TextDim = Color.Parse("#b9c0cf"),
        Border = Color.Parse("#363c4a"),
    };
}

/// <summary>
/// How much room everything gets: one token set, three densities.
/// </summary>
/// <remarks>
/// <para><see cref="Dense"/> is the operational default — the client window is a tool for scanning
/// lists, and a moderator glancing at "what have I sent" wants more of it on screen at once.</para>
/// <para><see cref="Vr"/> is not "comfortable, but more". Every number in it has a reason and the
/// reasons belong to the headset: a laser pointer is far less precise than a mouse, effective
/// pixels-per-degree is low enough that small text turns to mush, and one-pixel borders disappear
/// entirely.</para>
/// </remarks>
/// <param name="TextTiny">
/// Labels, counts and timestamps. The web UI's stylesheet has no token for it; the reference
/// prototype does, as <c>--fs-xs</c>.
/// </param>
public readonly record struct Density(
    double RowHeight,
    double ControlHeight,
    double TextBase,
    double TextSmall,
    double TextTiny,
    double Hairline,
    double Radius)
{
    /// <summary>CSS <c>rem</c> in device-independent pixels, at the browser default.</summary>
    public const double Rem = 16.0;

    /// <summary>Scanning long lists. The client window's default.</summary>
    public static Density Dense { get; } = new(
        RowHeight: 2.125 * Rem,
        ControlHeight: 1.875 * Rem,
        TextBase: 0.8125 * Rem,
        TextSmall: 0.75 * Rem,
        TextTiny: 11,
        Hairline: 1,
        Radius: 0.375 * Rem);

    /// <summary>Lower information density, easier on long sessions.</summary>
    public static Density Comfortable { get; } = Dense with
    {
        RowHeight = 2.75 * Rem,
        ControlHeight = 2.25 * Rem,
        TextBase = 0.875 * Rem,
        TextSmall = 0.8125 * Rem,
        TextTiny = 12,
    };

    /// <summary>Laser-pointer targets, text above the mush floor, borders that do not vanish.</summary>
    public static Density Vr { get; } = new(
        RowHeight: 3.5 * Rem,
        ControlHeight: 3.0 * Rem,
        TextBase: 1.125 * Rem,
        TextSmall: 1.0 * Rem,
        TextTiny: 0.875 * Rem,
        Hairline: 2,
        Radius: 0.5 * Rem);

    public CornerRadius CornerRadius => new(Radius);
}

/// <summary>
/// A palette and a density together, with brushes, ready to draw with.
/// </summary>
/// <remarks>
/// <para><strong>Tokens are shared with the web UI; components are not.</strong> Sharing
/// components would have coupled a native renderer to a DOM, and sharing nothing would have let
/// the two surfaces drift apart. Same palette, same type scale, same density rules, different
/// renderer — and a test that fails when they diverge.</para>
/// <para><strong>Brushes are made on first use, not in a static initialiser.</strong> A brush is an
/// <c>AvaloniaObject</c> and may only be constructed on the thread Avalonia was initialised on, so
/// an eager one would make merely reading a colour from a background thread throw.</para>
/// </remarks>
public sealed class DesignTokens
{
    private static readonly Dictionary<Color, IBrush> BrushCache = [];

    private static readonly Lock Gate = new();

    private DesignTokens(ModbotPalette palette, Density density)
    {
        Palette = palette;
        Density = density;
    }

    /// <summary>The client window: the desktop dark palette at the dense scale.</summary>
    public static DesignTokens Desktop { get; } = new(ModbotPalette.Dark, Density.Dense);

    /// <summary>The in-headset overlay.</summary>
    public static DesignTokens Vr { get; } = new(ModbotPalette.VrDark, Density.Vr);

    public ModbotPalette Palette { get; }

    public Density Density { get; }

    /// <summary>
    /// "IBM Plex Sans", with the same fallback chain the web UI uses.
    /// </summary>
    /// <remarks>
    /// An embedded face backs it up, so nothing depends on what fonts the machine happens to have
    /// and nothing is ever fetched from a font CDN — a renderer reaching out to the network for a
    /// typeface is a call this program has no business making.
    /// </remarks>
    public const string FontFamily = "IBM Plex Sans, Segoe UI, system-ui, sans-serif";

    /// <summary>
    /// A monospaced face for ids and counts.
    /// </summary>
    /// <remarks>
    /// Tabular figures are the single most load-bearing typographic decision in a tool made of
    /// number columns: proportional digits make a column of counts impossible to compare at a
    /// glance, which is the only thing anybody does with a column of counts.
    /// </remarks>
    public const string MonoFontFamily = "IBM Plex Mono, Cascadia Mono, Consolas, monospace";

    public IBrush BackgroundBrush => Brush(Palette.Background);

    public IBrush SurfaceBrush => Brush(Palette.Surface);

    public IBrush Surface2Brush => Brush(Palette.Surface2);

    public IBrush Surface3Brush => Brush(Palette.Surface3);

    public IBrush BorderBrush => Brush(Palette.Border);

    public IBrush Border2Brush => Brush(Palette.Border2);

    public IBrush TextBrush => Brush(Palette.Text);

    public IBrush TextDimBrush => Brush(Palette.TextDim);

    public IBrush TextFaintBrush => Brush(Palette.TextFaint);

    public IBrush AccentBrush => Brush(Palette.Accent);

    public IBrush AccentForegroundBrush => Brush(Palette.AccentForeground);

    public IBrush AccentDimBrush => Brush(Palette.AccentDim);

    public IBrush DangerBrush => Brush(Palette.Danger);

    public IBrush DangerDimBrush => Brush(Palette.DangerDim);

    public IBrush WarnBrush => Brush(Palette.Warn);

    public IBrush WarnDimBrush => Brush(Palette.WarnDim);

    public IBrush OkBrush => Brush(Palette.Ok);

    public IBrush OkDimBrush => Brush(Palette.OkDim);

    public IBrush InfoBrush => Brush(Palette.Info);

    public IBrush InfoDimBrush => Brush(Palette.InfoDim);

    public CornerRadius CornerRadius => Density.CornerRadius;

    /// <summary>One immutable brush per colour, shared. Brushes are never mutated after creation.</summary>
    public static IBrush Brush(Color colour)
    {
        lock (Gate)
        {
            if (!BrushCache.TryGetValue(colour, out var brush))
                BrushCache[colour] = brush = new SolidColorBrush(colour);

            return brush;
        }
    }
}
