using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
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
/// <para>The headset frames the screen in VRChat's look only. The desktop window asks for the frame in
/// both looks, with <see cref="WindowFrame"/>.</para>
/// </remarks>
public sealed partial class OverlayView
{
    /// <summary>How big the group's picture is in the title strip.</summary>
    private const double StripIconSize = 28;

    /// <summary>How big the group's picture is in the title strip in the Modbot look.</summary>
    private const double ModbotStripIconSize = 22;

    /// <summary>The name of the title strip, so a window can tell a press on it (to drag by it) from a press on the screen.</summary>
    public const string TitleStripName = "TitleStrip";

    /// <summary>
    /// How the desktop window asks for the frame: in either look, as wide as the window makes it, with the
    /// panel's opacity and whether its corners are rounded.
    /// </summary>
    /// <param name="Alpha">The opacity of the panel and the strip in VRChat's look; everything on them stays solid.</param>
    /// <param name="Rounded">Whether the window can be see-through, so the panel has rounded corners and a soft shadow (VRChat's look).</param>
    public readonly record struct WindowFrame(byte Alpha, bool Rounded);

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

    /// <summary>Whether a press landed on the title strip of a tree from <see cref="BuildFramed"/>, or on something in it.</summary>
    public static bool OnStrip(Visual? source)
    {
        for (var at = source; at is not null; at = at.GetVisualParent())
        {
            if (at is Control { Name: TitleStripName })
                return true;
        }

        return false;
    }

    /// <summary>
    /// The screen in the frame, laid out at <see cref="VRChatHudLayout.OverlayDesignWidth"/> (times <paramref name="wide"/>)
    /// and as tall as the room it is given; the screen alone when <see cref="IsFramed"/> says it is not framed.
    /// </summary>
    /// <remarks>
    /// With <paramref name="window"/> the screen is framed in both looks and as wide as its host makes it. A
    /// screen with no list to scroll by pixels scrolls under the strip instead.
    /// </remarks>
    /// <param name="wide">How many times wider than ordinary the panel has been made: the frame is that much wider, and the words stay their size.</param>
    /// <param name="window">Set by the desktop window; the headset leaves it out.</param>
    public static Control BuildFramed(OverlayScreen screen, Func<string?, IImage?>? icon, OverlayLook look, double wide = 1, WindowFrame? window = null)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(look);

        var view = new OverlayView(look, icon);
        var framed = window is not null || IsFramed(screen, look);

        // The strip holds Dictate, so the screen does not draw a second one.
        view._stripHoldsDictate = framed;

        var content = view.Draw(screen, icon);

        return framed ? view.Frame(screen, content, icon, wide, window) : content;
    }

    private Control Frame(OverlayScreen screen, Control content, Func<string?, IImage?>? icon, double wide, WindowFrame? window)
    {
        var inWindow = window;
        var rounded = inWindow?.Rounded ?? true;
        var alpha = inWindow?.Alpha ?? (byte)255;

        // A dock, so the name takes what the picture and the buttons leave and is cut with an
        // ellipsis, never drawn under a button.
        var title = new DockPanel
        {
            LastChildFill = true,
            HorizontalAlignment = V is null ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = V is null ? ModbotStripIconSize : StripIconSize,
        };

        if (V is null)
            ModbotTitle(title, screen, icon);
        else
            VRChatTitle(title, screen, icon, V);

        // The buttons on the right of the strip, the title in the room that is left: Close at the very
        // edge, Dictate beside it.
        var close = CloseButton(_look);
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

        Border strip;

        if (V is { } v)
        {
            var inner = rounded ? VRChatLook.FrameRadius - VRChatLook.EdgeWidth : 0;
            strip = new Border
            {
                Background = v.Bar(alpha),
                CornerRadius = new CornerRadius(inner, inner, 0, 0),
                Padding = new Thickness(14, 8),
                Child = stripContent,
            };
        }
        else
        {
            strip = new Border
            {
                Background = T.Surface2Brush,
                BorderBrush = T.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, T.Density.Hairline),
                Padding = new Thickness(12, 8),
                Child = stripContent,
            };
        }

        strip.Name = TitleStripName;

        // With the list scrolled by pixels, the screen under the strip is given what the strip leaves; in a
        // window with nothing scrolled by pixels the screen scrolls under the strip, which stays where it is.
        Control body = screen.ScrollOffset is null
            ? inWindow is null
                ? new StackPanel { Children = { strip, content } }
                : new FillStack
                {
                    Children =
                    {
                        strip,
                        new ScrollViewer
                        {
                            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                            Content = content,
                        },
                    },
                }
            : new FillStack { Children = { strip, content } };

        if (inWindow is null)
        {
            var v2 = V!;
            return new Border
            {
                Width = VRChatHudLayout.OverlayDesignWidth * wide,
                // Fills the room the panel has, like the window does, so a long list is cut at a rounded
                // bottom edge rather than running out of the frame.
                VerticalAlignment = VerticalAlignment.Stretch,
                ClipToBounds = true,
                Background = v2.Panel(),
                BorderBrush = v2.Edge,
                BorderThickness = new Thickness(VRChatLook.EdgeWidth),
                CornerRadius = new CornerRadius(VRChatLook.FrameRadius),
                Child = body,
            };
        }

        if (V is { } vw)
        {
            // The window lays the frame out as wide as it is. The frame itself is not clipped, which would
            // take the panel's shadow with it; the body inside it is, so a long list is cut at the rounded
            // corners, as the headset's frame cuts it.
            if (rounded)
            {
                body = new Border
                {
                    ClipToBounds = true,
                    CornerRadius = new CornerRadius(VRChatLook.FrameRadius - VRChatLook.EdgeWidth),
                    Child = body,
                };
            }

            return new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ClipToBounds = false,
                Background = vw.Panel(alpha),
                BorderBrush = vw.Edge,
                BorderThickness = new Thickness(VRChatLook.EdgeWidth),
                CornerRadius = rounded ? new CornerRadius(VRChatLook.FrameRadius) : default,
                BoxShadow = rounded ? vw.PanelShadow : default,
                Child = body,
            };
        }

        // The Modbot look has no panel of its own: the window's ground is what is behind it.
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = body,
        };
    }

    /// <summary>The group's picture and name in VRChat's look, or the small tag that says the lists are not the group's.</summary>
    private void VRChatTitle(DockPanel title, OverlayScreen screen, Func<string?, IImage?>? icon, VRChatLook v)
    {
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
    }

    /// <summary>The group's picture and name in the Modbot look; the words for no group, dim, when there is none.</summary>
    private void ModbotTitle(DockPanel title, OverlayScreen screen, Func<string?, IImage?>? icon)
    {
        if (icon?.Invoke(screen.GroupIconUrl) is { } image)
        {
            var picture = new Border
            {
                Width = ModbotStripIconSize,
                Height = ModbotStripIconSize,
                Margin = new Thickness(0, 0, 8, 0),
                CornerRadius = new CornerRadius(ModbotStripIconSize / 2),
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Image { Source = image, Stretch = Stretch.UniformToFill },
            };
            DockPanel.SetDock(picture, Avalonia.Controls.Dock.Left);
            title.Children.Add(picture);
        }

        var name = Text(
            screen.GroupLabel ?? screen.NoGroupWords,
            T.Density.TextBase,
            screen.GroupLabel is null ? T.TextDimBrush : T.TextBrush,
            FontWeight.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        title.Children.Add(name);
    }

    /// <summary>
    /// The Close button: a round icon button like the side panels' in VRChat's look, one target
    /// (<see cref="OverlayTarget.ClosePanel"/>) so a laser click on it closes the panel as the window's
    /// Close button does. In the Modbot look it is a plain square button in that look's colours.
    /// </summary>
    private static Border CloseButton(OverlayLook look)
    {
        var v = look.VRChat;
        var t = look.Tokens;
        var size = v is not null ? VRChatLook.RoundButton : t.Density.ControlHeight;

        return new Border
        {
            Tag = new OverlayTarget.ClosePanel(),
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = v is not null ? new CornerRadius(VRChatLook.RoundButton / 2) : new CornerRadius(t.Density.Radius),
            Background = v is not null ? v.Button : t.Surface2Brush,
            BorderBrush = v is not null ? v.RaisedEdge : t.Border2Brush,
            BorderThickness = new Thickness(v is not null ? VRChatLook.EdgeWidth : t.Density.Hairline),
            Child = new Viewbox
            {
                Margin = new Thickness(v is not null ? 8 : 6),
                Child = new Canvas
                {
                    Width = 24,
                    Height = 24,
                    Children =
                    {
                        new Path
                        {
                            Data = Geometry.Parse("M6,6 L18,18 M18,6 L6,18"),
                            Stroke = v is not null ? v.Icon : t.TextBrush,
                            StrokeThickness = 2.8,
                            StrokeLineCap = PenLineCap.Round,
                        },
                    },
                },
            },
        };
    }
}
