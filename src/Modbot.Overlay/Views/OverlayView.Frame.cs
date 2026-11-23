using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Interaction;
using Path = Avalonia.Controls.Shapes.Path;

namespace Modbot.Overlay.Views;

/// <summary>
/// The main panel's frame and title strip: the one the headset draws and the desktop window hosts, so the
/// two cannot drift apart.
/// </summary>
/// <remarks>
/// <para>The strip is the group's picture and name (or the small tag that says the lists are not the
/// group's), the Dictate button when it is offered, and the Close button; under it is the screen
/// itself (<see cref="Build(OverlayScreen, Func{string?, IImage?}?, OverlayLook)"/>).</para>
/// <para>It is laid out at the desktop window's design size (<see cref="VRChatHudLayout.OverlayDesignWidth"/>)
/// and drawn bigger where it needs to be (<see cref="Zoom"/>, for the headset).</para>
/// </remarks>
public sealed partial class OverlayView
{
    /// <summary>How big the group's picture is in the title strip.</summary>
    private const double StripIconSize = 28;

    /// <summary>
    /// Whether the screen is drawn in the frame. In the Modbot look nothing is framed, and nothing is framed
    /// that is not a list: the idle screen draws nothing, and the wrist card is four large lines that are
    /// already drawn for a panel a sixth of the size.
    /// </summary>
    public static bool IsFramed(OverlayScreen screen, OverlayLook look)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(look);

        return look.VRChat is not null && screen.Page is not OverlayPage.Wrist && !(screen.IsIdle && !screen.ShowIdleCard);
    }

    /// <summary>
    /// The screen in the frame, laid out at <see cref="VRChatHudLayout.OverlayDesignWidth"/> (times <paramref name="wide"/>)
    /// and as tall as the room it is given; the screen alone when <see cref="IsFramed"/> says it is not framed.
    /// </summary>
    /// <remarks>The frame is VRChat's look; in the Modbot look this returns the bare screen, which the desktop window's own strip still surrounds.</remarks>
    /// <param name="wide">How many times wider than ordinary the panel has been made: the frame is that much wider, and the words stay their size.</param>
    public static Control BuildFramed(OverlayScreen screen, Func<string?, IImage?>? icon, OverlayLook look, double wide = 1)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(look);

        var view = new OverlayView(look, icon);
        var content = view.Draw(screen, icon);

        return IsFramed(screen, look) ? view.Frame(screen, content, icon, wide) : content;
    }

    private Control Frame(OverlayScreen screen, Control content, Func<string?, IImage?>? icon, double wide)
    {
        var v = V!;
        var inner = VRChatLook.FrameRadius - VRChatLook.EdgeWidth;

        // A dock, so the name takes what the picture and the buttons leave and is cut with an
        // ellipsis, never drawn under a button.
        var title = new DockPanel
        {
            LastChildFill = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = StripIconSize,
        };

        if (screen.GroupLabel is { } group)
        {
            if (icon?.Invoke(screen.GroupIconUrl) is { } image)
            {
                var picture = new Border
                {
                    Width = StripIconSize,
                    Height = StripIconSize,
                    Margin = new Thickness(0, 0, 8, 0),
                    CornerRadius = new CornerRadius(VRChatLook.PillRadius),
                    ClipToBounds = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new Image { Source = image, Stretch = Stretch.UniformToFill },
                };
                DockPanel.SetDock(picture, Avalonia.Controls.Dock.Left);
                title.Children.Add(picture);
            }

            var name = Text(group, 20, v.Heading, FontWeight.ExtraBold);
            name.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(name);
        }
        else if (screen.NotSynced)
        {
            // Outside a managed group's instance the body's own heading says "Not in a group instance"
            // (or "Another group's instance" for a group no paired server manages), so the
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

        // The buttons on the right of the strip, the title in the room that is left: Close at the very
        // edge, Dictate beside it.
        var close = CloseButton(v);
        DockPanel.SetDock(close, Avalonia.Controls.Dock.Right);

        var stripContent = new DockPanel { LastChildFill = true, Children = { close } };

        if (screen.Dictate is { } card)
        {
            var dictate = DictateButton(card);
            dictate.VerticalAlignment = VerticalAlignment.Center;
            dictate.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(dictate, Avalonia.Controls.Dock.Right);
            stripContent.Children.Add(dictate);
        }

        stripContent.Children.Add(title);

        var strip = new Border
        {
            Background = v.Bar(),
            CornerRadius = new CornerRadius(inner, inner, 0, 0),
            Padding = new Thickness(14, 8),
            Child = stripContent,
        };

        return new Border
        {
            Width = VRChatHudLayout.OverlayDesignWidth * wide,
            // Fills the room the panel has, like the window does, so a long list is cut at a rounded
            // bottom edge rather than running out of the frame.
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
            Background = v.Panel(),
            BorderBrush = v.Edge,
            BorderThickness = new Thickness(VRChatLook.EdgeWidth),
            CornerRadius = new CornerRadius(VRChatLook.FrameRadius),
            // With the list scrolled by pixels, the screen under the strip is given what the strip leaves.
            Child = screen.ScrollOffset is null
                ? new StackPanel { Children = { strip, content } }
                : new FillStack { Children = { strip, content } },
        };
    }

    /// <summary>
    /// The Close button: a round icon button like the side panels' in VRChat's look, one target
    /// (<see cref="OverlayTarget.ClosePanel"/>) so a laser click on it closes the panel as the window's
    /// Close button does.
    /// </summary>
    private static Border CloseButton(VRChatLook v) => new()
    {
        Tag = new OverlayTarget.ClosePanel(),
        Width = VRChatLook.RoundButton,
        Height = VRChatLook.RoundButton,
        VerticalAlignment = VerticalAlignment.Center,
        CornerRadius = new CornerRadius(VRChatLook.RoundButton / 2),
        Background = v.Button,
        BorderBrush = v.RaisedEdge,
        BorderThickness = new Thickness(VRChatLook.EdgeWidth),
        Child = new Viewbox
        {
            Margin = new Thickness(8),
            Child = new Canvas
            {
                Width = 24,
                Height = 24,
                Children =
                {
                    new Path
                    {
                        Data = Geometry.Parse("M6,6 L18,18 M18,6 L6,18"),
                        Stroke = v.Icon,
                        StrokeThickness = 2.8,
                        StrokeLineCap = PenLineCap.Round,
                    },
                },
            },
        },
    };
}
