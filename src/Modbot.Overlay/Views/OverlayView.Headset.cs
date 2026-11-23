using Avalonia.Controls;
using Avalonia.Media;
using Modbot.Companion.Presentation;

namespace Modbot.Overlay.Views;

/// <summary>
/// The headset panel in VRChat's look: the main panel's frame and title strip (<see cref="BuildFramed"/>),
/// the same the desktop window hosts, drawn bigger so it reads from a metre off.
/// </summary>
/// <remarks>
/// <para>The frame is laid out at the desktop window's design size
/// (<see cref="VRChatHudLayout.OverlayDesignWidth"/>) and drawn at <see cref="HeadsetScale"/> times that
/// (<see cref="Zoom"/>), which is what makes the words bigger than they were: 18 points of text is
/// about 1.7 times that on the texture.</para>
/// </remarks>
public sealed partial class OverlayView
{
    /// <summary>
    /// How many layout pixels across the texture the headset's words are sized against. The frame is laid out
    /// narrower than that (the window's width), so it takes about 87% of the texture's width and its words
    /// are the size in the room they have always been.
    /// </summary>
    private const double HeadsetWordsBasis = 600;

    /// <summary>
    /// How many times bigger than its layout the headset panel is drawn: the VRChat look is laid out at the
    /// window's design size and drawn this much bigger, and the Modbot look is not scaled.
    /// A list's scroll is in layout pixels, so a distance on the texture is divided by this to be one.
    /// </summary>
    public static double HeadsetScale(OverlayLook look, double textureWidth)
    {
        ArgumentNullException.ThrowIfNull(look);
        return look.VRChat is null ? 1 : textureWidth / HeadsetWordsBasis;
    }

    /// <summary>
    /// How wide the panel's own rectangle is on the texture, in pixels: the frame as drawn when the screen is
    /// framed, and as wide as the texture (times <paramref name="wide"/>) when it is not.
    /// </summary>
    public static double HeadsetPanelWidth(OverlayScreen screen, OverlayLook look, double textureWidth, double wide = 1)
        => IsFramed(screen, look)
            ? VRChatHudLayout.OverlayDesignWidth * wide * HeadsetScale(look, textureWidth)
            : textureWidth * wide;

    /// <summary>
    /// The screen as the headset panel draws it. In the Modbot look that is the screen as it has always
    /// been; in VRChat's look it is the window's screen in the frame, drawn bigger.
    /// </summary>
    /// <param name="textureWidth">How wide the panel's picture is, in pixels.</param>
    /// <param name="wide">How many times wider than ordinary the panel has been made: the frame is that much wider, and the words stay their size.</param>
    public static Control BuildForHeadset(OverlayScreen screen, Func<string?, IImage?>? icon, OverlayLook look, double textureWidth, double wide = 1)
    {
        var framed = BuildFramed(screen, icon, look, wide);

        return IsFramed(screen, look) ? new Zoom(HeadsetScale(look, textureWidth), framed) : framed;
    }
}
