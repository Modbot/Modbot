namespace Modbot.Companion.Presentation;

/// <summary>
/// The sizes of Modbot's bubble for one HUD size, all in pixels of VRChat's client area.
/// </summary>
/// <remarks>
/// Everything is the row's own geometry at scale 1, measured off VRChat's interface, times the
/// HUD's scale. The bubble is the next slot along VRChat's row, so its icon, its label pill and its
/// height are the other bubbles' (Escape Menu design §3).
/// </remarks>
/// <param name="Scale">How big VRChat's HUD is, where 1 is the smaller of the two known sizes.</param>
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
}
