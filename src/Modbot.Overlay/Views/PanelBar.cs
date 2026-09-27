using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Path = Avalonia.Controls.Shapes.Path;

namespace Modbot.Overlay.Views;

/// <summary>
/// The bar under a headset panel: whether it is up, and the two switches on it.
/// </summary>
/// <param name="Showing">Up while a controller's ray is on the panel; drawn as nothing otherwise.</param>
/// <param name="Locked">The panel cannot be picked up, moved or resized.</param>
/// <param name="ClickThrough">The panel lets rays through to VRChat; only the bar still answers.</param>
public readonly record struct PanelBar(bool Showing, bool Locked, bool ClickThrough)
{
    /// <summary>The bar for a placement, up or not.</summary>
    public static PanelBar For(OverlayPlacement placement, bool showing)
        => new(showing, placement.Locked, placement.ClickThrough);
}

/// <summary>
/// Puts a panel's contents, the bar under them and the cursor over both into one frame, the way
/// XSOverlay puts a bar under each of its windows.
/// </summary>
/// <remarks>
/// <para><strong>Inside the texture.</strong> The panel is one square picture and the room only
/// knows it as a square, so the bar is drawn into the bottom of that square rather than hung under
/// it. The contents are held above the bar's row, so a long roster is cut short before it reaches
/// the bar instead of running under it.</para>
/// <para><strong>Laid out whether it shows or not.</strong> A hidden bar is drawn at no opacity
/// rather than left out, so the contents never jump when it appears, and a ray that lands where the
/// bar is finds it on the very first poll — which is what lets a panel that lets rays through still
/// be switched back.</para>
/// <para>Nothing here reads a controller. What is up, locked or let through is decided by the host
/// and handed in.</para>
/// </remarks>
public static class PanelFrame
{
    private static DesignTokens T => DesignTokens.Vr;

    /// <summary>The main panel's bar: sized for a 1,024-pixel texture about 45 cm across.</summary>
    public static readonly BarSize MainSize = new(Button: 72, Icon: 40, Gap: 12, Padding: 8, Above: 4);

    /// <summary>The notification panel's bar: sized for its 300-pixel texture about 40 cm across.</summary>
    public static readonly BarSize NotificationSize = new(Button: 28, Icon: 18, Gap: 5, Padding: 3, Above: 3);

    /// <summary>How big the bar is drawn, in panel pixels.</summary>
    /// <param name="Button">One button, square.</param>
    /// <param name="Icon">The picture inside a button.</param>
    /// <param name="Gap">Between the two buttons.</param>
    /// <param name="Padding">Between the bar's edge and the buttons.</param>
    /// <param name="Above">Between the contents and the bar.</param>
    public readonly record struct BarSize(double Button, double Icon, double Gap, double Padding, double Above)
    {
        /// <summary>The whole row the bar takes, from the bottom of the contents down.</summary>
        public double Row => Above + Button + (Padding * 2) + (T.Density.Hairline * 2);
    }

    /// <summary>
    /// The main panel: its cards from the top, and the bar straight under the last of them.
    /// </summary>
    /// <param name="content">What <see cref="OverlayView.Build"/> made, without a cursor.</param>
    /// <param name="height">The texture's height in pixels.</param>
    public static Control Main(Control content, PanelBar bar, PanelCursor? cursor, double height)
    {
        ArgumentNullException.ThrowIfNull(content);

        var size = MainSize;
        content.VerticalAlignment = VerticalAlignment.Top;

        var above = new Border
        {
            Background = Brushes.Transparent,
            MaxHeight = Math.Max(0, height - size.Row),
            ClipToBounds = true,
            Child = content,
        };

        var column = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top,
            Children = { above, Bar(bar, size) },
        };

        return WithCursor(column, cursor);
    }

    /// <summary>
    /// The notification panel: a fixed box the pop-ups stack in, outlined while it can still be
    /// moved, with the bar under it.
    /// </summary>
    /// <remarks>
    /// The box is there even with nothing in it, because an empty panel is still a thing that can
    /// be picked up. Outlined while unlocked, so it can be found and placed; once locked the
    /// outline goes and an empty panel draws nothing again, apart from the bar while a ray is on it.
    /// </remarks>
    /// <param name="cards">What <see cref="NotificationView.Build"/> made.</param>
    /// <param name="side">The texture's size in pixels; the box is the same share of it at any size.</param>
    public static Control Notification(Control cards, PanelBar bar, PanelCursor? cursor, double side)
    {
        ArgumentNullException.ThrowIfNull(cards);

        var size = NotificationSize;
        cards.VerticalAlignment = VerticalAlignment.Top;

        var boxSide = side * NotifyOverlaySettings.BoxPixels / NotifyOverlaySettings.PanelPixels;
        var box = new Panel
        {
            Width = boxSide,
            Height = boxSide,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
        };
        if (!bar.Locked)
        {
            box.Children.Add(new Rectangle
            {
                Stroke = new SolidColorBrush(T.Palette.TextDim, 0.7),
                StrokeThickness = 2,
                StrokeDashArray = [4, 3],
                RadiusX = 6,
                RadiusY = 6,
            });
        }

        box.Children.Add(new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(3),
            Child = cards,
        });

        var column = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top,
            Children = { box, Bar(bar, size) },
        };

        return WithCursor(column, cursor);
    }

    private static Control WithCursor(Control frame, PanelCursor? cursor)
        => cursor is { } where
            ? new Panel { Children = { frame, new OverlayView.CursorLayer(where) } }
            : frame;

    /// <summary>The bar itself: a rounded strip with the lock and the hand on it.</summary>
    private static Control Bar(PanelBar bar, BarSize size)
    {
        var strip = new Border
        {
            Tag = new OverlayTarget.Bar(),
            Background = T.SurfaceBrush,
            BorderBrush = T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = new CornerRadius((size.Button / 2) + size.Padding),
            Padding = new Thickness(size.Padding),
            Margin = new Thickness(0, size.Above, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = bar.Showing ? 1 : 0,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = size.Gap,
                Children =
                {
                    Button(new OverlayTarget.Lock(), bar.Locked, LockIcon(bar.Locked, Foreground(bar.Locked)), size),
                    Button(
                        new OverlayTarget.ClickThrough(),
                        bar.ClickThrough,
                        HandIcon(bar.ClickThrough, Foreground(bar.ClickThrough), Background(bar.ClickThrough)),
                        size),
                },
            },
        };

        return strip;
    }

    private static IBrush Foreground(bool on) => on ? T.AccentForegroundBrush : T.TextBrush;

    private static IBrush Background(bool on) => on ? T.AccentBrush : T.Surface2Brush;

    private static Control Button(OverlayTarget target, bool on, Control icon, BarSize size) => new Border
    {
        Tag = target,
        Width = size.Button,
        Height = size.Button,
        CornerRadius = new CornerRadius(size.Button / 2),
        Background = Background(on),
        BorderBrush = on ? T.AccentBrush : T.Border2Brush,
        BorderThickness = new Thickness(T.Density.Hairline),
        Child = new Viewbox
        {
            Width = size.Icon,
            Height = size.Icon,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon,
        },
    };

    /// <summary>
    /// A padlock on a 24-unit square: shut when <paramref name="locked"/>, its shackle lifted
    /// out of the body when not.
    /// </summary>
    public static Control LockIcon(bool locked, IBrush brush)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };

        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse(locked
                ? "M8,11 V8 A4,4 0 0 1 16,8 V11"
                : "M16,11 V6 A4,4 0 0 0 8,6 V7.5"),
            Stroke = brush,
            StrokeThickness = 2.4,
            StrokeLineCap = PenLineCap.Round,
        });

        var body = new Border
        {
            Width = 15,
            Height = 10.5,
            CornerRadius = new CornerRadius(2),
            Background = brush,
        };
        Canvas.SetLeft(body, 4.5);
        Canvas.SetTop(body, 10.5);
        canvas.Children.Add(body);

        return canvas;
    }

    /// <summary>
    /// An open hand on a 24-unit square, struck through when the panel lets rays through.
    /// </summary>
    /// <param name="background">What the stroke is cut out of, so it reads as a gap in the hand.</param>
    public static Control HandIcon(bool through, IBrush brush, IBrush background)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };

        void Piece(double left, double top, double width, double height, double radius)
        {
            var piece = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(radius),
                Background = brush,
            };
            Canvas.SetLeft(piece, left);
            Canvas.SetTop(piece, top);
            canvas.Children.Add(piece);
        }

        // Four fingers, the middle two tallest, over a palm, with the thumb out to the left.
        Piece(7.0, 5.5, 2.8, 9, 1.4);
        Piece(10.1, 3.0, 2.8, 11, 1.4);
        Piece(13.2, 3.5, 2.8, 10.5, 1.4);
        Piece(16.3, 5.5, 2.8, 9, 1.4);
        Piece(7.0, 11.0, 12.1, 10.5, 3.5);
        canvas.Children.Add(new Line
        {
            StartPoint = new Point(8.8, 17.5),
            EndPoint = new Point(4.4, 12.2),
            Stroke = brush,
            StrokeThickness = 2.8,
            StrokeLineCap = PenLineCap.Round,
        });

        if (through)
        {
            canvas.Children.Add(new Line
            {
                StartPoint = new Point(3, 3),
                EndPoint = new Point(21, 21),
                Stroke = background,
                StrokeThickness = 5,
                StrokeLineCap = PenLineCap.Round,
            });
            canvas.Children.Add(new Line
            {
                StartPoint = new Point(3, 3),
                EndPoint = new Point(21, 21),
                Stroke = brush,
                StrokeThickness = 2.2,
                StrokeLineCap = PenLineCap.Round,
            });
        }

        return canvas;
    }
}
