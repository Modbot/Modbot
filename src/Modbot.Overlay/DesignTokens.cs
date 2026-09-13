using Avalonia;
using Avalonia.Media;

namespace Modbot.Overlay;

/// <summary>
/// The web UI's design tokens, as constants the overlay draws with.
/// </summary>
/// <remarks>
/// <para><strong>Tokens are shared; components are not.</strong> Sharing components would have
/// coupled a native renderer to a DOM, and sharing nothing would have let the two surfaces drift
/// apart visually. The palette, the type scale and the VR density values are the right level of
/// reuse: same colours, same sizes, same rules, different renderer.</para>
/// <para><strong>These are the <c>vr</c> + <c>dark</c> values</strong> from
/// <c>Modbot.Web/src/index.css</c> and nothing else. A headset is the only place this renderer
/// runs, and a light overlay floating in a dark game is not a theme choice anybody wants.</para>
/// <para><strong>Every number here has a reason rather than a multiplier</strong>, and the reasons
/// belong to the headset: laser pointers are far less precise than a mouse, effective
/// pixels-per-degree is low enough that small text turns to mush, one-pixel borders disappear
/// entirely, and panels bloom at pure black and pure white, so both are pulled inward.</para>
/// <para><c>DesignTokenDriftTests</c> re-reads the CSS and fails if these have drifted from it, so
/// "keep them in sync" is a check rather than a hope.</para>
/// </remarks>
public static class DesignTokens
{
    /// <summary>CSS <c>rem</c>, in device-independent pixels, at the browser default.</summary>
    public const double Rem = 16.0;

    // ── Colour: [data-density="vr"].dark ────────────────────────────────────────────────────
    public static readonly Color Background = Color.Parse("#15171e");

    public static readonly Color Card = Color.Parse("#1c1f28");

    public static readonly Color Foreground = Color.Parse("#f2f4f8");

    public static readonly Color MutedForeground = Color.Parse("#b9c0cf");

    public static readonly Color Border = Color.Parse("#363c4a");

    public static readonly Color Primary = Color.Parse("#7c6cf5");

    public static readonly Color PrimaryForeground = Color.Parse("#ffffff");

    public static readonly Color Destructive = Color.Parse("#f0526a");

    public static readonly Color Muted = Color.Parse("#1b1e26");

    public static readonly Color Accent = Color.Parse("#2a2547");

    public static readonly Color AccentForeground = Color.Parse("#b6acff");

    public static readonly Color Warn = Color.Parse("#e8a33d");

    public static readonly Color Ok = Color.Parse("#3fbf8f");

    public static readonly Color Info = Color.Parse("#4aa3f0");

    // ── Density: [data-density="vr"] ────────────────────────────────────────────────────────

    /// <summary>Laser-pointer targets, not mouse targets.</summary>
    public const double RowHeight = 3.5 * Rem;

    /// <summary>At or above the fat-finger floor of 48 device-independent pixels.</summary>
    public const double ControlHeight = 3.0 * Rem;

    /// <summary>Below roughly 16 px, body text is mush at headset pixels-per-degree.</summary>
    public const double TextBase = 1.125 * Rem;

    public const double TextSmall = 1.0 * Rem;

    /// <summary>One-pixel borders vanish in a headset.</summary>
    public const double Hairline = 2.0;

    public const double Radius = 0.5 * Rem;

    /// <summary>
    /// "IBM Plex Sans", with the same fallback chain the web UI uses. The font is embedded in the
    /// overlay rather than fetched: an overlay that renders in a fallback face the first time it
    /// is shown, in the one moment it matters, is not acceptable, and a renderer that reaches out
    /// to a font CDN is a network call this program has no business making.
    /// </summary>
    public const string FontFamily = "IBM Plex Sans, Segoe UI, system-ui, sans-serif";

    public static IBrush BackgroundBrush { get; } = new SolidColorBrush(Background);

    public static IBrush CardBrush { get; } = new SolidColorBrush(Card);

    public static IBrush ForegroundBrush { get; } = new SolidColorBrush(Foreground);

    public static IBrush MutedForegroundBrush { get; } = new SolidColorBrush(MutedForeground);

    public static IBrush BorderBrush { get; } = new SolidColorBrush(Border);

    public static IBrush DestructiveBrush { get; } = new SolidColorBrush(Destructive);

    public static IBrush WarnBrush { get; } = new SolidColorBrush(Warn);

    public static IBrush OkBrush { get; } = new SolidColorBrush(Ok);

    public static IBrush AccentBrush { get; } = new SolidColorBrush(Accent);

    public static IBrush AccentForegroundBrush { get; } = new SolidColorBrush(AccentForeground);

    public static CornerRadius CornerRadius { get; } = new(Radius);
}
