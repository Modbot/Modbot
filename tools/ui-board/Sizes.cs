using Modbot.Companion.Overlay;
using Modbot.Overlay;
using Modbot.Overlay.OpenVr;

namespace Modbot.UiBoard;

/// <summary>
/// The one place that says how big a picture is shown on the board, so the pictures of a tab keep their
/// sizes next to each other as they have in use. The size slider scales everything by the same share.
/// <list type="bullet">
/// <item>A headset panel is shown as wide as it is in the headset: its width in metres
/// (<see cref="Metres"/>, read from the product's own constants) times <see cref="PixelsPerMetre"/>.
/// The picture is the whole square panel, so its texture's pixels do not matter.</item>
/// <item>A window (VRChat's desktop windows, the companion's) is shown at its own pixel size times
/// <see cref="WindowScale"/>, which is its size on a 1920x1080 VRChat window.</item>
/// </list>
/// </summary>
internal static class Sizes
{
    /// <summary>How many board pixels one metre in the headset is at a slider of 100 percent: the main panel's 0.45 metres comes out about as wide as the desktop overlay window.</summary>
    public const double PixelsPerMetre = 1000;

    /// <summary>How many board pixels one window pixel is at a slider of 100 percent: 1, the window as it is on a 1920x1080 screen.</summary>
    public const double WindowScale = 1;

    /// <summary>How wide a headset panel is in the headset, in metres.</summary>
    private static double Metres(Panel panel) => panel switch
    {
        // The panel's square is as long on a side as the panel's longer side: the main panel starts tall.
        Panel.Main => OpenVrOverlayRuntime.DefaultWidthInMetres * OverlayPlacement.MainPanelDefault.Longest,

        // The button's width is the card's; the panel is the card with its clear ground around it.
        Panel.Button => OverlayButton.CornerWidth * OverlayButton.CanvasScale,

        // The pop-ups' width is the box's; the panel is the box with the bar's room around it.
        Panel.Notifications => NotifyOverlaySettings.DefaultWidth * NotifyOverlaySettings.PanelPerBox,

        Panel.Chat => ChatHost.DefaultWidth,
        _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, "A window has no width in metres."),
    };

    /// <summary>How wide <paramref name="tile"/> is shown at a slider of 100 percent, in board pixels. <paramref name="pixels"/> is the picture's own width.</summary>
    public static double Shown(Tile tile, Panel panel, int pixels)
        => tile.Side == Side.Vr ? Metres(panel) * PixelsPerMetre : pixels * WindowScale;
}
