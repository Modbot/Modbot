using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Overlay.Interaction;
using Path = Avalonia.Controls.Shapes.Path;

namespace Modbot.Overlay.Views;

/// <summary>
/// Builds the Avalonia visual tree for the show and hide button.
/// </summary>
/// <remarks>
/// <para><strong>One card, one thing to point at.</strong> An eye, the label and the shortcut under
/// it. The eye is open on "Show" and struck through on "Hide", so the picture says what the click
/// will do as the word does.</para>
/// <para><strong>The same card in both looks.</strong> The Modbot look draws it as the other
/// headset cards are drawn; the VRChat look uses the button colour, the raised edge and the soft
/// shadow its pop-ups use.</para>
/// <para>The shortcut is text that was handed in, placed as text and kept to one line. A long one is
/// shrunk to fit the card rather than cut off, because a stick and a direction are the words that
/// matter in it. While the stick is held the line counts down ("Hide in 3") and a bar along the foot
/// of the card fills.</para>
/// </remarks>
public static class ButtonView
{
    private const double Margin = 8;

    private const double IconSize = 84;

    private const double BarHeight = 6;

    private const double BarWidth = 150;

    /// <summary>How far above the card's foot the bar sits.</summary>
    private const double BarLift = 10;

    /// <summary>The pictures and bar on the two-row card, which is smaller than the single-face one.</summary>
    private const double RowIconSize = 56;

    private const double RowBarWidth = 190;

    private const double RowBarLift = 3;

    /// <param name="cursor">Where a controller points, or null.</param>
    public static Control Build(ButtonScreen screen, OverlayLook look, PanelCursor? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(look);

        var card = Card(screen, look);

        return cursor is { } where
            ? new Panel { Children = { card, new OverlayView.CursorLayer(where) } }
            : card;
    }

    private static Control Card(ButtonScreen screen, OverlayLook look)
    {
        var t = look.Tokens;
        var v = look.VRChat;

        var fill = v?.Button ?? t.SurfaceBrush;
        var ink = v?.Icon ?? t.TextBrush;
        var words = v?.Text ?? t.TextBrush;
        var dim = v?.Subtext ?? t.TextDimBrush;

        var bright = v?.Bright ?? t.AccentBrush;
        var track = v?.Edge ?? t.Surface3Brush;

        Control face;
        object? tag;

        if (screen.Dictate is DictateState.Off)
        {
            var lines = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    Eye(screen.PanelShown, ink, fill),
                    Text(screen.Label, 36, words, FontWeight.Bold),
                },
            };

            // Nothing at all under the label while the shortcut is off, rather than a gap.
            if (screen.Line.Length > 0)
                lines.Children.Add(Shrunk(Text(screen.Line, 22, screen.Countdown is null ? dim : words, FontWeight.SemiBold)));

            var single = new Grid { Children = { lines } };

            if (screen.Countdown is { } count)
                single.Children.Add(Bar(count.Progress, bright, track));

            face = single;
            tag = new OverlayTarget.PanelButton();
        }
        else
        {
            // Two rows inside the one card, each its own thing to point at, so the card keeps its
            // size and its place on the bar.
            face = Rows(screen, fill, ink, words, dim, bright, track);
            tag = null;
        }

        return new Border
        {
            Tag = tag,
            Margin = new Thickness(Margin),
            Background = fill,
            BorderBrush = v?.RaisedEdge ?? t.Border2Brush,
            BorderThickness = new Thickness(v is null ? t.Density.Hairline : VRChatLook.EdgeWidth),
            CornerRadius = v is null ? t.CornerRadius : new CornerRadius(VRChatLook.CardRadius),
            BoxShadow = v?.CardShadow ?? default,
            Padding = new Thickness(6),
            Child = face,
        };
    }

    /// <summary>The show and hide row above the Dictate row, each with its picture on the left and words on the right.</summary>
    private static Control Rows(ButtonScreen screen, IBrush fill, IBrush ink, IBrush words, IBrush dim, IBrush bright, IBrush track)
    {
        var show = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var eye = Eye(screen.PanelShown, ink, fill, RowIconSize);
        eye.Margin = new Thickness(8, 0, 4, 0);
        eye.VerticalAlignment = VerticalAlignment.Center;
        show.Children.Add(eye);

        var showLines = new StackPanel
        {
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Shrunk(Text(screen.Label, 32, words, FontWeight.Bold)) },
        };

        if (screen.Line.Length > 0)
            showLines.Children.Add(Shrunk(Text(screen.Line, 18, screen.Countdown is null ? dim : words, FontWeight.SemiBold)));

        Grid.SetColumn(showLines, 1);
        show.Children.Add(showLines);

        if (screen.Countdown is { } count)
        {
            var bar = Bar(count.Progress, bright, track, RowBarWidth, RowBarLift);
            Grid.SetColumnSpan(bar, 2);
            show.Children.Add(bar);
        }

        var dictate = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var picture = Dictation(screen.Dictate, screen.Frame, ink, bright);
        picture.Margin = new Thickness(8, 0, 4, 0);
        picture.VerticalAlignment = VerticalAlignment.Center;
        dictate.Children.Add(picture);

        var dictateLines = new StackPanel
        {
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Shrunk(Text(screen.DictateLabel, 32, words, FontWeight.Bold)) },
        };

        if (screen.DictatePercent.Length > 0)
            dictateLines.Children.Add(Shrunk(Text(screen.DictatePercent, 18, dim, FontWeight.SemiBold)));

        Grid.SetColumn(dictateLines, 1);
        dictate.Children.Add(dictateLines);

        var rows = new Grid { RowDefinitions = new RowDefinitions("*,*") };
        var top = Row(new OverlayTarget.PanelButton(), show);
        var bottom = Row(new OverlayTarget.DictateButton(), dictate);
        Grid.SetRow(bottom, 1);
        rows.Children.Add(top);
        rows.Children.Add(bottom);
        return rows;
    }

    private static Border Row(OverlayTarget target, Control content) => new()
    {
        Tag = target,
        Margin = new Thickness(0, 1),
        CornerRadius = new CornerRadius(10),
        Child = content,
    };

    /// <summary>
    /// The Dictate picture on a 24-unit square: a microphone; with sound arcs that come and go while
    /// listening; a ring of dots that turns while writing or getting ready.
    /// </summary>
    public static Control Dictation(DictateState state, int frame, IBrush ink, IBrush bright)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };

        if (state is DictateState.Writing or DictateState.GettingReady)
        {
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Math.PI / 4;
                var age = (((frame - i) % 8) + 8) % 8;
                var dot = new Ellipse { Width = 3.6, Height = 3.6, Fill = ink, Opacity = 1 - (age * 0.11) };
                Canvas.SetLeft(dot, 12 + (8.5 * Math.Sin(angle)) - 1.8);
                Canvas.SetTop(dot, 12 - (8.5 * Math.Cos(angle)) - 1.8);
                canvas.Children.Add(dot);
            }
        }
        else
        {
            var mic = state is DictateState.Listening ? bright : ink;

            canvas.Children.Add(Stroke("M9,5 A3,3 0 0 1 15,5 L15,11 A3,3 0 0 1 9,11 Z", mic));
            canvas.Children.Add(Stroke("M6,11 A6,6 0 0 0 18,11 M12,17 L12,21 M8.5,21 L15.5,21", mic));

            if (state is DictateState.Listening)
            {
                var step = ((frame % 4) + 4) % 4;
                var inner = Stroke("M20,8 A4,4 0 0 1 20,14 M4,8 A4,4 0 0 0 4,14", bright);
                var outer = Stroke("M21.8,7.5 A5,5 0 0 1 21.8,15.5 M2.2,7.5 A5,5 0 0 0 2.2,15.5", bright);
                inner.Opacity = step >= 1 ? 1 : 0.25;
                outer.Opacity = step >= 2 ? 1 : 0.25;
                canvas.Children.Add(inner);
                canvas.Children.Add(outer);
            }
        }

        return new Viewbox
        {
            Width = RowIconSize,
            Height = RowIconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = canvas,
        };
    }

    private static Path Stroke(string data, IBrush brush) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = brush,
        StrokeThickness = 1.8,
        StrokeJoin = PenLineJoin.Round,
        StrokeLineCap = PenLineCap.Round,
    };

    /// <summary>
    /// A bar along the foot of the card, filled from the left as far as <paramref name="progress"/>
    /// of the way.
    /// </summary>
    private static Control Bar(float progress, IBrush fill, IBrush track, double width = BarWidth, double lift = BarLift)
    {
        var radius = new CornerRadius(BarHeight / 2);
        var filled = Math.Clamp(progress, 0f, 1f) * width;

        return new Border
        {
            Width = width,
            Height = BarHeight,
            Margin = new Thickness(0, 0, 0, lift),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = track,
            CornerRadius = radius,
            ClipToBounds = true,
            Child = new Border
            {
                Width = filled,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = fill,
                CornerRadius = radius,
            },
        };
    }

    /// <summary>A line of text that is made smaller, never cut off, when it is wider than the card.</summary>
    private static Control Shrunk(Control text) => new Viewbox
    {
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.DownOnly,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = text,
    };

    /// <summary>
    /// An eye on a 24-unit square: an almond outline with a pupil, struck through when
    /// <paramref name="struckThrough"/> is true.
    /// </summary>
    /// <param name="struckThrough">True while the panel is showing, when a click hides it.</param>
    /// <param name="background">What the strike is cut out of, so it reads as a gap in the eye.</param>
    internal static Control Eye(bool struckThrough, IBrush brush, IBrush background, double size = IconSize)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };

        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M2,12 C5,6.5 8.5,5 12,5 C15.5,5 19,6.5 22,12 C19,17.5 15.5,19 12,19 C8.5,19 5,17.5 2,12 Z"),
            Stroke = brush,
            StrokeThickness = 1.8,
            StrokeJoin = PenLineJoin.Round,
        });

        var pupil = new Ellipse { Width = 6.4, Height = 6.4, Stroke = brush, StrokeThickness = 1.8 };
        Canvas.SetLeft(pupil, 8.8);
        Canvas.SetTop(pupil, 8.8);
        canvas.Children.Add(pupil);

        if (struckThrough)
        {
            // A wider stroke in the card's own colour first, so the slash leaves a gap either side
            // of itself where it crosses the outline.
            canvas.Children.Add(Slash(background, 4.6));
            canvas.Children.Add(Slash(brush, 1.8));
        }

        return new Viewbox
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = canvas,
        };
    }

    private static Path Slash(IBrush brush, double thickness) => new()
    {
        Data = Geometry.Parse("M4.5,4.5 L19.5,19.5"),
        Stroke = brush,
        StrokeThickness = thickness,
        StrokeLineCap = PenLineCap.Round,
    };

    private static TextBlock Text(string content, double size, IBrush brush, FontWeight weight) => new()
    {
        Text = content,
        FontSize = size,
        FontFamily = new FontFamily(DesignTokens.FontFamily),
        FontWeight = weight,
        Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 1,
    };
}
