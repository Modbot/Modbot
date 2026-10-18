using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
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
/// <param name="Hints">
/// The controller hints, or null for none. The main panel draws them beside the bar; the
/// notification panel, which has no room beside its bar, draws them over the foot of its box.
/// </param>
/// <param name="WidthInMetres">
/// How long the panel's square is on a side in the room, so the hints can be drawn the same physical size on every
/// panel. Zero (not known, and always while there are no hints) draws them at the pop-up panel's
/// pixel size. Part of the bar so that stretching the panel draws the hints again.
/// </param>
/// <param name="Corner">
/// The resize corner is drawn at the panel's bottom right: edit mode, and the panel is unlocked,
/// not click-through and not on a wrist.
/// </param>
/// <param name="Wide">How many times wider than ordinary the panel has been made. Part of the bar so that resizing the panel draws it again.</param>
/// <param name="Tall">How many times taller than ordinary the panel has been made.</param>
public readonly record struct PanelBar(
    bool Showing,
    bool Locked,
    bool ClickThrough,
    ControlHints? Hints = null,
    double WidthInMetres = 0,
    bool Corner = false,
    double Wide = 1,
    double Tall = 1)
{
    /// <summary>
    /// The bar for a placement, up or not. A locked panel cannot be moved and one letting rays
    /// through is not pointed at, so neither gets hints, and neither gets the resize corner.
    /// </summary>
    /// <param name="corner">Whether this panel has a resize corner at all while it is edited: edit mode, and not on a wrist.</param>
    public static PanelBar For(OverlayPlacement placement, bool showing, ControlHints? hints = null, bool corner = false)
    {
        var shown = placement.Locked || placement.ClickThrough ? null : hints;
        return new(
            showing,
            placement.Locked,
            placement.ClickThrough,
            shown,
            shown is null ? 0 : placement.Side,
            corner && !placement.Locked && !placement.ClickThrough,
            placement.Wide,
            placement.Tall);
    }
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

    /// <summary>The show and hide button's bar: sized for a 256-pixel texture about 16 cm across.</summary>
    public static readonly BarSize ButtonSize = new(Button: 36, Icon: 22, Gap: 6, Padding: 4, Above: 3);

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

        /// <summary>The bar's own width: two buttons, the gap and the padding.</summary>
        public double Width => (Button * 2) + Gap + (Padding * 2) + (T.Density.Hairline * 2);
    }

    /// <summary>
    /// How much to stretch a hint pill, drawn as the pop-up panel draws it (text <c>TextTiny</c>,
    /// padding 8 by 3), so it comes out the same physical size on a panel of any width and texture.
    /// The pop-up panel at its default width is 1: the box's 256 pixels are
    /// <see cref="NotifyOverlaySettings.DefaultWidth"/> metres. This is the one place that size is
    /// set; every panel that shows hints draws them through it.
    /// </summary>
    /// <remarks>
    /// Held to what a stack of <see cref="MostHintLines"/> lines may take of the panel's side
    /// (<see cref="HintShare"/>): the narrower the panel, the more pixels a centimetre is, and a
    /// pill kept to one physical size would grow until it filled the panel. At the widths the
    /// panels start at, the cap is not reached.
    /// </remarks>
    /// <param name="side">The panel's texture size in pixels.</param>
    public static double HintScale(PanelBar bar, double side)
    {
        if (bar.WidthInMetres <= 0)
            return 1;

        var same = side / bar.WidthInMetres * (NotifyOverlaySettings.DefaultWidth / NotifyOverlaySettings.BoxPixels);

        // A pill at scale 1: its lines (about 1.25 times the text), the padding and the border.
        var pill = (MostHintLines * T.Density.TextTiny * 1.25) + 6 + (T.Density.Hairline * 2);
        return Math.Min(same, HintShare * side / pill);
    }

    /// <summary>The most of a panel's side that a stack of hint lines may take.</summary>
    private const double HintShare = 0.4;

    /// <summary>The most lines a hint wraps onto beside the main panel's bar.</summary>
    private const double MostHintLines = 3;

    /// <summary>
    /// The main panel: its cards from the top, and the bar straight under the last of them.
    /// </summary>
    /// <param name="content">What <see cref="OverlayView.Build"/> made, without a cursor.</param>
    /// <param name="height">The panel's height in pixels: the texture's, or the texture's times how tall the panel has been made.</param>
    /// <param name="width">
    /// The panel's width in pixels, when it has been made wider or taller than the texture's square;
    /// null is as wide as it is tall. The rectangle sits at the top left of a square as long as its longer side.
    /// </param>
    public static Control Main(Control content, PanelBar bar, PanelCursor? cursor, double height, double? width = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        var size = MainSize;
        var across = width ?? height;
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
            Children = { above, BarRow(bar, size, across, Math.Max(across, height), bar.Corner ? MainGrip + size.Padding : 0) },
        };

        // Narrower than the square it sits in: the column is the rectangle, from the left.
        if (across < height)
        {
            column.Width = across;
            column.HorizontalAlignment = HorizontalAlignment.Left;
        }

        if (!bar.Corner)
            return WithCursor(column, cursor);

        var grip = Grip(MainGrip);
        grip.HorizontalAlignment = HorizontalAlignment.Right;
        grip.VerticalAlignment = VerticalAlignment.Bottom;
        grip.Margin = new Thickness(0, 0, size.Padding, size.Padding);

        return WithCursor(
            new Panel
            {
                Width = across,
                Height = height,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Children = { column, grip },
            },
            cursor);
    }

    /// <summary>The resize corner on the main panel, in panel pixels: bigger than a bar button.</summary>
    private const double MainGrip = 128;

    /// <summary>The resize corner on the notification panel, in panel pixels.</summary>
    private const double NotificationGrip = 52;

    /// <summary>
    /// The resize corner: two thick, see-through slanted strokes, the mark a window's corner wears,
    /// on nothing. The square around them is clear but still takes a press (a transparent brush, not
    /// none, so the press finds it). Tagged so a trigger held on it can be told from a press anywhere else.
    /// </summary>
    private static Border Grip(double side) => new()
    {
        Tag = new OverlayTarget.ResizeCorner(),
        Width = side,
        Height = side,
        Background = Brushes.Transparent,
        Child = new Viewbox
        {
            Width = side * 0.85,
            Height = side * 0.85,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Path
            {
                Data = Geometry.Parse("M21,3 L3,21 M21,12 L12,21"),
                Stroke = new SolidColorBrush(T.Palette.Text, 0.55),
                StrokeThickness = 3.2,
                StrokeLineCap = PenLineCap.Round,
                Width = 24,
                Height = 24,
            },
        },
    };

    /// <summary>
    /// The bar, and beside it the controller hints while there are any. The bar stays in the
    /// middle and the hints fill the row's two sides, so the row is as tall as it was and nothing
    /// above it moves.
    /// </summary>
    /// <param name="across">How wide the row is: the bar is in the middle of it.</param>
    /// <param name="side">The square canvas's side, which the hints are sized by.</param>
    /// <param name="corner">How much of the row's right end the resize corner takes.</param>
    private static Control BarRow(PanelBar bar, BarSize size, double across, double side, double corner)
    {
        var strip = Bar(bar, size);
        if (bar.Hints is not { } hints)
            return strip;

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        Grid.SetColumn(strip, 1);
        row.Children.Add(strip);

        var scale = HintScale(bar, side);
        var room = ((across - size.Width) / 2) - (size.Padding * 2);

        var left = HintColumn([hints.Move, hints.Resize], HorizontalAlignment.Right, size, scale, room);
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        var right = HintColumn([hints.Distance], HorizontalAlignment.Left, size, scale, Math.Max(0, room - corner));
        Grid.SetColumn(right, 2);
        row.Children.Add(right);

        return row;
    }

    /// <summary>
    /// Hints stacked beside the bar, against it, level with its middle. At the size the pop-up's
    /// pills are, a long one wraps onto more lines than the row is tall; the stack then grows
    /// upward over the foot of the cards, so nothing moves and the bar's row stays as it was.
    /// </summary>
    /// <param name="room">The widest a pill may be: what the row has on this side of the bar.</param>
    private static Control HintColumn(ControlHint?[] hints, HorizontalAlignment against, BarSize size, double scale, double room)
    {
        var stack = new StackPanel { Spacing = 4 * scale };

        foreach (var hint in hints.OfType<ControlHint>())
            stack.Children.Add(Hint(hint, scale, room));

        // A Canvas, because a Grid cell would cut the stack to the row's height. Measured here so it
        // can be centred on the bar, and kept off the texture's foot when it is taller than the row.
        stack.Measure(new Size(room, double.PositiveInfinity));
        var top = Math.Min(((size.Above + size.Row) / 2) - (stack.DesiredSize.Height / 2), size.Row - stack.DesiredSize.Height);
        Canvas.SetTop(stack, top);

        if (against == HorizontalAlignment.Right)
            Canvas.SetRight(stack, size.Padding * 2);
        else
            Canvas.SetLeft(stack, size.Padding * 2);

        return new Canvas { Children = { stack } };
    }

    /// <summary>
    /// One hint as a pill: the control in the text colour, what it does beside it in the dim one.
    /// Lit, with the accent colours, while the control it names is held down.
    /// </summary>
    /// <param name="scale">How much to stretch it from the pop-up's own size: <see cref="HintScale"/>.</param>
    /// <param name="maxWidth">
    /// The widest the pill may be; its words wrap onto a second line rather than run past it.
    /// Unlimited keeps them on one line.
    /// </param>
    private static Control Hint(ControlHint hint, double scale, double maxWidth = double.PositiveInfinity) => new Border
    {
        Background = hint.Lit ? T.AccentDimBrush : T.SurfaceBrush,
        BorderBrush = hint.Lit ? T.AccentBrush : T.Border2Brush,
        BorderThickness = new Thickness(T.Density.Hairline * scale),
        CornerRadius = new CornerRadius(T.Density.Radius * 2 * scale),
        Padding = new Thickness(8 * scale, 3 * scale),
        MaxWidth = maxWidth,
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock
        {
            FontSize = T.Density.TextTiny * scale,
            FontFamily = new FontFamily(DesignTokens.FontFamily),
            TextWrapping = double.IsFinite(maxWidth) ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Inlines = new InlineCollection
            {
                new Run($"{hint.Control}:") { FontWeight = FontWeight.SemiBold, Foreground = T.TextBrush },
                new Run($" {hint.Action}") { Foreground = T.TextDimBrush },
            },
        },
    };

    /// <summary>
    /// The notification panel: a fixed box the pop-ups stack in, outlined while it can still be
    /// moved, with the bar under it.
    /// </summary>
    /// <remarks>
    /// The box is there even with nothing in it, because an empty panel is still a thing that can
    /// be picked up. Outlined while unlocked, so it can be found and placed; once locked the
    /// outline goes and an empty panel draws nothing again, apart from the bar while a ray is on it.
    /// While unlocked the whole box is one target, so a ray finds the panel anywhere on it, in the
    /// gaps between the cards and when there are none, and not only on a card or the bar.
    /// </remarks>
    /// <param name="cards">What <see cref="NotificationView.Build"/> made.</param>
    /// <param name="side">The panel's design size in pixels before it is made wider or taller; the box is the same share of it at any size.</param>
    /// <param name="wide">How many times wider than ordinary the box is.</param>
    /// <param name="tall">How many times taller than ordinary the box is: room for more pop-ups.</param>
    public static Control Notification(Control cards, PanelBar bar, PanelCursor? cursor, double side, double wide = 1, double tall = 1)
    {
        ArgumentNullException.ThrowIfNull(cards);

        var size = NotificationSize;
        cards.VerticalAlignment = VerticalAlignment.Top;

        var boxSide = side * NotifyOverlaySettings.BoxPixels / NotifyOverlaySettings.PanelPixels;
        var boxWide = boxSide * wide;
        var box = new Panel
        {
            Width = boxWide,
            Height = boxSide * tall,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            Tag = bar.Locked ? null : new OverlayTarget.PopUpBox(),
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

        var canvas = side * Math.Max(wide, tall);
        if (bar.Hints is { } hints)
            box.Children.Add(BoxHints(hints, boxWide, HintScale(bar, canvas)));

        if (bar.Corner)
        {
            var grip = Grip(NotificationGrip);
            grip.HorizontalAlignment = HorizontalAlignment.Right;
            grip.VerticalAlignment = VerticalAlignment.Bottom;
            grip.Margin = new Thickness(0, 0, size.Padding, size.Padding);
            box.Children.Add(grip);
        }

        var column = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top,
            Children = { box, Bar(bar, size) },
        };

        // Narrower than the square it sits in: the box keeps its margin, and the column is the rectangle.
        var across = boxWide + (side - boxSide);
        if (across < canvas)
        {
            column.Width = across;
            column.HorizontalAlignment = HorizontalAlignment.Left;
        }

        return WithCursor(column, cursor);
    }

    /// <summary>
    /// The show and hide button: its card, and the bar in a row of its own under it while the bar is
    /// there at all (edit mode). Without a bar the card is the whole picture, as it always was.
    /// </summary>
    /// <remarks>
    /// The card gives up the bar's row rather than the bar being drawn over it, so the bar never
    /// covers the eye or the words. The button is small, so the bar is the only thing beside the
    /// card, and it has no room for hints.
    /// </remarks>
    /// <param name="card">What <see cref="ButtonView.Build"/> made, without a cursor.</param>
    /// <param name="bar">The bar, or null while edit mode is off.</param>
    public static Control Button(Control card, PanelBar? bar, PanelCursor? cursor)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (bar is not { } shown)
            return WithCursor(card, cursor);

        var frame = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(card, 0);
        frame.Children.Add(card);

        var strip = Bar(shown, ButtonSize);
        Grid.SetRow(strip, 1);
        frame.Children.Add(strip);

        return WithCursor(frame, cursor);
    }

    /// <summary>
    /// The hints over the foot of the notification box, one pill under another. The bar's row has
    /// no room for them: it is about two thirds the width of the box, and the longest hint is not
    /// much narrower than the box. A pill that would run past the box wraps instead. These are the
    /// pills every panel's hints are sized after (<see cref="HintScale"/>).
    /// </summary>
    private static Control BoxHints(ControlHints hints, double boxSide, double scale)
    {
        var stack = new StackPanel
        {
            Spacing = 4 * scale,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 8 * scale),
        };

        foreach (var hint in new[] { hints.Move, hints.Resize, hints.Distance }.OfType<ControlHint>())
        {
            var pill = Hint(hint, scale, boxSide - 16);
            pill.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(pill);
        }

        return stack;
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
