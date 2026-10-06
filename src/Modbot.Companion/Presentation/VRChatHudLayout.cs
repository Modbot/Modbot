using Modbot.Companion.Clips;

namespace Modbot.Companion.Presentation;

/// <summary>
/// A box in the pixels of VRChat's client area (its window without the title bar), measured from
/// that area's top-left corner. Right and Bottom are the first pixels outside it.
/// </summary>
public readonly record struct HudBox(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    /// <summary>Whether the two boxes share any pixel. Boxes that only touch do not overlap.</summary>
    public bool Overlaps(HudBox other)
        => Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;
}

/// <summary>
/// Where the desktop overlay goes inside VRChat's window: its size, and its top-left corner,
/// in the pixels of VRChat's client area.
/// </summary>
/// <param name="X">How far in from the client area's left edge.</param>
/// <param name="Y">How far down from the client area's top edge.</param>
/// <param name="Width">How wide the overlay is drawn, in pixels.</param>
/// <param name="Height">How tall the overlay is drawn, in pixels.</param>
/// <param name="Scale">
/// How much the overlay's own layout is scaled: <see cref="VRChatHudLayout.OverlayDesignWidth"/>
/// laid out as usual, then drawn <c>Scale</c> times as big.
/// </param>
public readonly record struct OverlayFit(int X, int Y, int Width, int Height, double Scale)
{
    public HudBox Box => new(X, Y, X + Width, Y + Height);
}

/// <summary>
/// Where the parts of VRChat's desktop screen are for one window size, and so where the desktop
/// overlay fits beside them (<c>2026-10-05-desktop-overlay-escape-menu-design.md</c> §3.3).
/// </summary>
/// <remarks>
/// <para><strong>One rule for every size.</strong> VRChat draws its HUD at a scale that follows the
/// window's <em>height</em>: <c>S = client height / 1009</c>. That was measured on one real window
/// at two sizes (1920 × 1009 and 2560 × 1440), and it holds for the width too, because the Esc
/// menu is centred on the width and grows with S. There is no list of known sizes and no minimum
/// size: a smaller window gets a smaller overlay, the way VRChat's own menu gets smaller.</para>
/// <para><strong>What the numbers are.</strong> Plain code, no window and no Windows calls, so the
/// placing can be tested without a screen. Every number is a constant below, at scale 1, so the
/// first look on a live window can correct it. The Esc menu's width was measured on the Launch
/// Pad page only; other pages may be wider. The corner column and the notification band are
/// read off pictures and good to a few pixels. Nobody has seen the result on a live VRChat
/// window yet.</para>
/// </remarks>
public sealed record VRChatHudLayout
{
    /// <summary>The client height at which VRChat's HUD is drawn at scale 1.</summary>
    public const double ReferenceHeight = 1009;

    /// <summary>How far the Esc menu reaches either side of the client area's middle, at scale 1.</summary>
    public const double MenuHalfWidth = 492;

    /// <summary>The space left between the overlay and what is beside it, at scale 1, on each side.</summary>
    public const double Gap = 12;

    /// <summary>The top-right column (mic, F4, F5): its right edge's distance from the client's right edge, at scale 1.</summary>
    public const double CornerColumnRightInset = 23;

    /// <summary>The same column's left edge's distance from the client's right edge, at scale 1.</summary>
    public const double CornerColumnLeftInset = 118;

    /// <summary>The same column's bottom, down from the client's top, at scale 1.</summary>
    public const double CornerColumnBottom = 198;

    /// <summary>VRChat's notification tiles along the top: where they start, from the client area's middle, at scale 1 (to the left).</summary>
    public const double NotificationLeftOfMiddle = 300;

    /// <summary>Where they end, from the client area's middle, at scale 1 (to the right).</summary>
    public const double NotificationRightOfMiddle = 200;

    /// <summary>Where they end, down from the client's top, at scale 1.</summary>
    public const double NotificationBottom = 175;

    /// <summary>How wide the overlay's panel is laid out, before it is scaled.</summary>
    public const double OverlayDesignWidth = 520;

    /// <summary>How tall the overlay's panel is laid out, before it is scaled.</summary>
    public const double OverlayDesignHeight = 720;

    private VRChatHudLayout(int clientWidth, int clientHeight)
    {
        ClientWidth = clientWidth;
        ClientHeight = clientHeight;
        Scale = ScaleFor(clientHeight);
    }

    public int ClientWidth { get; }

    public int ClientHeight { get; }

    /// <summary>How big VRChat's HUD is in this window, where 1 is a client area 1009 pixels tall.</summary>
    public double Scale { get; }

    /// <summary>How big VRChat's HUD is for a client area <paramref name="clientHeight"/> pixels tall.</summary>
    public static double ScaleFor(int clientHeight) => clientHeight / ReferenceHeight;

    /// <summary>
    /// The layout for a client area, or null when the size is empty or makes no sense (nothing
    /// wide or tall to place anything in).
    /// </summary>
    public static VRChatHudLayout? For(int clientWidth, int clientHeight)
        => clientWidth > 0 && clientHeight > 0 ? new VRChatHudLayout(clientWidth, clientHeight) : null;

    private int Px(double atOne) => (int)Math.Round(atOne * Scale);

    private double MenuRightExact => (ClientWidth / 2.0) + (MenuHalfWidth * Scale);

    private double MenuLeftExact => (ClientWidth / 2.0) - (MenuHalfWidth * Scale);

    /// <summary>The Esc menu's right edge, from the client area's left edge. The left side is the same distance from the middle.</summary>
    public int EscMenuRight => (int)Math.Round(MenuRightExact);

    /// <summary>The Esc menu's left edge, from the client area's left edge.</summary>
    public int EscMenuLeft => (int)Math.Round(MenuLeftExact);

    /// <summary>The Esc menu, the whole way down: what VRChat's menu covers.</summary>
    public HudBox EscMenu => new(EscMenuLeft, 0, EscMenuRight, ClientHeight);

    /// <summary>
    /// The free strip on the right: from the Esc menu's right edge to the client's. Empty (no
    /// width) when the window is so narrow that the menu fills it.
    /// </summary>
    public HudBox RightStrip
    {
        get
        {
            var left = Math.Min(EscMenuRight, ClientWidth);
            return new HudBox(left, 0, ClientWidth, ClientHeight);
        }
    }

    /// <summary>The mic, F4 and F5 column in the top-right corner.</summary>
    public HudBox TopRightColumn
        => new(ClientWidth - Px(CornerColumnLeftInset), 0, ClientWidth - Px(CornerColumnRightInset), Px(CornerColumnBottom));

    /// <summary>
    /// VRChat's notification tiles along the top, a rough box to keep out of. Not used to place
    /// the overlay (it is beside the Esc menu, clear of this band); it is here so the desktop
    /// notification can keep out of it later.
    /// </summary>
    public HudBox NotificationBand
    {
        get
        {
            var middle = ClientWidth / 2.0;
            return new HudBox(
                (int)Math.Round(middle - (NotificationLeftOfMiddle * Scale)),
                0,
                (int)Math.Round(middle + (NotificationRightOfMiddle * Scale)),
                Px(NotificationBottom));
        }
    }

    /// <summary>
    /// Where the overlay goes for VRChat's window as Windows last described it, or null when the
    /// overlay should stay where it always was: there is no VRChat window, it is minimised, it has
    /// no picture to speak of, or nothing fits in it.
    /// </summary>
    public static OverlayFit? OverlayFor(GameWindow window)
        => window.HasPicture ? For(window.Width, window.Height)?.Overlay() : null;

    /// <summary>
    /// Where the overlay goes: in the free strip beside the Esc menu, a gap away from the menu and
    /// from the client's edge, under the top-right column, as big as fits at the panel's own shape.
    /// Null when nothing fits (the strip or the space under the column is not even a pixel).
    /// </summary>
    /// <remarks>
    /// The width is the strip's less a gap either side, and the height follows from the panel's
    /// shape. When that is taller than the space from just under the corner column to the client's
    /// bottom less a gap, the scale shrinks until it fits. It sits right-aligned and starts just
    /// under the column.
    /// </remarks>
    public OverlayFit? Overlay()
    {
        var gap = Gap * Scale;
        var width = ClientWidth - MenuRightExact - (2 * gap);
        var top = (CornerColumnBottom * Scale) + gap;
        var room = ClientHeight - gap - top;

        if (width <= 0 || room <= 0)
            return null;

        var scale = Math.Min(width / OverlayDesignWidth, room / OverlayDesignHeight);

        // Whole pixels, rounded down so it never grows past the room it was given. The scale is
        // then the one the width came out at, so the content fills what is drawn.
        var drawnWidth = (int)Math.Floor(OverlayDesignWidth * scale);
        if (drawnWidth < 1)
            return null;

        var drawnScale = drawnWidth / OverlayDesignWidth;
        var drawnHeight = (int)Math.Floor(OverlayDesignHeight * drawnScale);
        if (drawnHeight < 1)
            return null;

        // The margin to the client's edge is rounded down and the top up, so the gaps are at
        // least what was asked for.
        var x = ClientWidth - (int)Math.Floor(gap) - drawnWidth;
        var y = (int)Math.Ceiling(top);

        return new OverlayFit(x, y, drawnWidth, drawnHeight, drawnScale);
    }
}
