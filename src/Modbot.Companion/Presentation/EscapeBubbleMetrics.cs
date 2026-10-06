namespace Modbot.Companion.Presentation;

/// <summary>
/// The sizes of Modbot's bubble for one HUD size, all in pixels of VRChat's client area.
/// </summary>
/// <remarks>
/// Everything is the row's own geometry at scale 1, measured on a real VRChat window at 1920 by
/// 1009 and again at 2560 by 1440, times the HUD's scale. The bubble is the next slot along
/// VRChat's row, so its icon, its label pill and its height are the other bubbles' (Escape Menu
/// design §3).
/// </remarks>
/// <param name="Scale">How big VRChat's HUD is: the client area's height over 1009, so 1 is a client area 1009 pixels tall (<see cref="VRChatHudLayout.ScaleFor"/>).</param>
public sealed record EscapeBubbleMetrics(double Scale)
{
    /// <summary>The Esc bubble's centre at scale 1, from the client area's left edge.</summary>
    private const double EscCentre = 55.5;

    /// <summary>How far apart the bubbles in the row are, at scale 1.</summary>
    private const double Pitch = 64.5;

    /// <summary>Which slot Modbot takes: Esc, R, Tab and Y are 0 to 3.</summary>
    private const int Slot = 4;

    /// <summary>At scale 1: the top of the icons and the top of the label pills, from the client area's top edge.</summary>
    private const double IconTopAtOne = 40;

    private const double PillTopAtOne = 78;

    private int Px(double atOne) => (int)Math.Round(atOne * Scale);

    /// <summary>The middle of the bubble, from the client area's left edge.</summary>
    public int CentreX => Px(EscCentre + Slot * Pitch);

    /// <summary>Where the bubble starts, from the client area's top edge.</summary>
    public int Top => Px(IconTopAtOne);

    public int IconHeight => Px(30);

    /// <summary>The label pill's top, measured from <see cref="Top"/>.</summary>
    public int PillTop => Px(PillTopAtOne) - Top;

    public int PillHeight => Px(20);

    /// <summary>The narrowest the pill goes, which is what VRChat's own labels are.</summary>
    public int PillMinWidth => Px(41);

    /// <summary>Between the pill's edge and its text, either side.</summary>
    public int PillPad => Px(8);

    /// <summary>
    /// The label's size, which gives capitals the height of VRChat's own: 11 pixels at scale 1,
    /// measured on the reference window.
    /// </summary>
    public double FontSize => 15 * Scale;

    /// <summary>How tall the bubble is: the icon, the gap, and the pill.</summary>
    public int Height => PillTop + PillHeight;

    /// <summary>
    /// How wide the pill (and so the bubble) is for a label <paramref name="textWidth"/> pixels
    /// wide: never narrower than a normal bubble, wider when the text needs it, so the text is
    /// always the size of the other labels'.
    /// </summary>
    public int Width(double textWidth) => Math.Max(PillMinWidth, (int)Math.Ceiling(textWidth) + 2 * PillPad);

    /// <summary>
    /// Where the top-left of the bubble goes, as an offset from VRChat's client area. The left edge
    /// is where a normal bubble's would be in the slot, and a long label grows to the right, so it
    /// never reaches back into the Y bubble beside it.
    /// </summary>
    /// <param name="nudgeX">The moderator's own adjustment, in pixels.</param>
    /// <param name="nudgeY">The moderator's own adjustment, in pixels.</param>
    public (int X, int Y) Place(int nudgeX = 0, int nudgeY = 0)
        => (CentreX - PillMinWidth / 2 + nudgeX, Top + nudgeY);

    // The backing panel: VRChat draws a faint rounded dark panel behind its four bubbles, and
    // Modbot's bubble gets one of its own, in the same window as the bubble. Measured on a real
    // 1920 by 1080 client (scale 1.0704), where VRChat's own is x 26 to 301 and y 26 to 123.

    /// <summary>How far past VRChat's own panel's right edge (281.2 at scale 1) ours starts, so the two never overlap.</summary>
    private const double PanelGapAtOne = 3;

    /// <summary>The right edge of VRChat's own panel at scale 1, from the client area's left edge.</summary>
    private const double VRChatPanelRightAtOne = 281.2;

    /// <summary>The panel's top and bottom at scale 1, which are VRChat's own panel's.</summary>
    private const double PanelTopAtOne = 24.3;

    private const double PanelBottomAtOne = 114.9;

    /// <summary>Between the pill's right edge and the panel's, at scale 1.</summary>
    private const double PanelPadRightAtOne = 11;

    /// <summary>The panel's corner radius at scale 1. Looks like about 10 at scale 1.07; not measured closer than that.</summary>
    private const double PanelRadiusAtOne = 9.5;

    /// <summary>How much of what is behind the panel it covers: black at 22%, which took (57,31,15) to (45,24,10).</summary>
    public const double PanelAlpha = 0.22;

    /// <summary>
    /// The panel's left edge, from the client area's left edge. Rounded up, so it is never less
    /// than the gap past VRChat's own panel, which it must not overlap.
    /// </summary>
    public int PanelLeft => (int)Math.Ceiling((VRChatPanelRightAtOne + PanelGapAtOne) * Scale);

    /// <summary>The panel's top, from the client area's top edge.</summary>
    public int PanelTop => Px(PanelTopAtOne);

    public int PanelHeight => Px(PanelBottomAtOne) - PanelTop;

    public int PanelRadius => Px(PanelRadiusAtOne);

    /// <summary>
    /// Where the bubble's left edge is inside the panel (the room left of it, between the two
    /// panels). The bubble keeps its place on the row; the panel is drawn around it.
    /// </summary>
    public int PillOffsetX => Place().X - PanelLeft;

    /// <summary>Where the bubble's top is inside the panel.</summary>
    public int PillOffsetY => Top - PanelTop;

    /// <summary>
    /// How wide the panel is for a label <paramref name="textWidth"/> pixels wide: the room left of
    /// the bubble, the bubble, and the room right of it. A long label grows the panel to the right.
    /// </summary>
    public int PanelWidth(double textWidth) => PillOffsetX + Width(textWidth) + Px(PanelPadRightAtOne);

    /// <summary>
    /// Where the top-left of the panel goes, as an offset from VRChat's client area. This is where
    /// the bubble's window goes: the window is the panel.
    /// </summary>
    public (int X, int Y) PanelOrigin() => (PanelLeft, PanelTop);
}
