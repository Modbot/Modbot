using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Modbot.Overlay.Views;

/// <summary>
/// The headset panel in VRChat's look: the desktop window's screen, in a frame with the title strip the
/// window gives it, drawn bigger so it reads from a metre off.
/// </summary>
/// <remarks>
/// <para>The screen itself is the one the desktop window draws (<see cref="Build(OverlayScreen, Func{string?, IImage?}?, OverlayLook)"/>),
/// so a headset and a window cannot drift apart. What the window has outside it, its own frame and
/// title strip, is drawn here for the headset, and the grab bar and the controller hints stay with
/// the panel's frame (<see cref="PanelFrame"/>).</para>
/// <para>Laid out at <see cref="HeadsetDesignWidth"/> and drawn at the texture's width
/// (<see cref="Zoom"/>), which is what makes the words bigger than they were: 18 points of text is
/// about 1.7 times that on the texture.</para>
/// </remarks>
public sealed partial class OverlayView
{
    /// <summary>How wide the VRChat look is laid out for a headset, before it is drawn at the texture's width.</summary>
    public const double HeadsetDesignWidth = 600;

    /// <summary>How big the group's picture is in the title strip.</summary>
    private const double StripIconSize = 28;

    /// <summary>
    /// The screen as the headset panel draws it. In the Modbot look that is the screen as it has always
    /// been; in VRChat's look it is the window's screen in a frame, drawn bigger.
    /// </summary>
    /// <param name="textureWidth">How wide the panel's picture is, in pixels.</param>
    public static Control BuildForHeadset(OverlayScreen screen, Func<string?, IImage?>? icon, OverlayLook look, double textureWidth)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(look);

        var view = new OverlayView(look, icon);
        var content = view.Draw(screen, icon);

        // Nothing is framed that is not a list: the idle screen draws nothing, and the wrist card is
        // four large lines that are already drawn for a panel a sixth of the size.
        if (look.VRChat is null || screen.Page is OverlayPage.Wrist || (screen.IsIdle && !screen.ShowIdleCard))
            return content;

        return new Zoom(textureWidth / HeadsetDesignWidth, view.HeadsetFrame(screen, content, icon));
    }

    private Control HeadsetFrame(OverlayScreen screen, Control content, Func<string?, IImage?>? icon)
    {
        var v = V!;
        var inner = VRChatLook.FrameRadius - VRChatLook.EdgeWidth;

        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = StripIconSize,
        };

        if (screen.GroupLabel is { } group)
        {
            if (icon?.Invoke(screen.GroupIconUrl) is { } image)
            {
                title.Children.Add(new Border
                {
                    Width = StripIconSize,
                    Height = StripIconSize,
                    CornerRadius = new CornerRadius(VRChatLook.PillRadius),
                    ClipToBounds = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new Image { Source = image, Stretch = Stretch.UniformToFill },
                });
            }

            var name = Text(group, 20, v.Heading, FontWeight.ExtraBold);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.MaxWidth = HeadsetDesignWidth - 120;
            title.Children.Add(name);
        }
        else if (screen.NotSynced)
        {
            // Outside a group's instance the body's own heading says "Not in a group instance", so the
            // strip carries only the small tag that says the lists are not from the group.
            title.Children.Add(new Border
            {
                Background = VRChatLook.PillGround,
                BorderBrush = v.Edge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(VRChatLook.PillRadius),
                Padding = new Thickness(10, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Text(NotSyncedWords, 13, v.Text, FontWeight.Bold),
            });
        }

        var strip = new Border
        {
            Background = v.Bar(),
            CornerRadius = new CornerRadius(inner, inner, 0, 0),
            Padding = new Thickness(14, 8),
            Child = title,
        };

        return new Border
        {
            Width = HeadsetDesignWidth,
            // Fills the room the panel has, like the window does, so a long list is cut at a rounded
            // bottom edge rather than running out of the frame.
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
            Background = v.Panel(),
            BorderBrush = v.Edge,
            BorderThickness = new Thickness(VRChatLook.EdgeWidth),
            CornerRadius = new CornerRadius(VRChatLook.FrameRadius),
            Child = new StackPanel { Children = { strip, content } },
        };
    }
}
