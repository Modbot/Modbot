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
/// <para>The shortcut is text that was handed in, placed as text and kept to one line.</para>
/// </remarks>
public static class ButtonView
{
    private const double Margin = 8;

    private const double IconSize = 84;

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

        var lines = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Eye(screen.PanelShown, ink, fill),
                Text(screen.Label, 36, words, FontWeight.Bold),
                Text(screen.Shortcut, 22, dim, FontWeight.SemiBold),
            },
        };

        return new Border
        {
            Tag = new OverlayTarget.PanelButton(),
            Margin = new Thickness(Margin),
            Background = fill,
            BorderBrush = v?.RaisedEdge ?? t.Border2Brush,
            BorderThickness = new Thickness(v is null ? t.Density.Hairline : VRChatLook.EdgeWidth),
            CornerRadius = v is null ? t.CornerRadius : new CornerRadius(VRChatLook.CardRadius),
            BoxShadow = v?.CardShadow ?? default,
            Padding = new Thickness(6),
            Child = lines,
        };
    }

    /// <summary>
    /// An eye on a 24-unit square: an almond outline with a pupil, struck through when
    /// <paramref name="struckThrough"/> is true.
    /// </summary>
    /// <param name="struckThrough">True while the panel is showing, when a click hides it.</param>
    /// <param name="background">What the strike is cut out of, so it reads as a gap in the eye.</param>
    internal static Control Eye(bool struckThrough, IBrush brush, IBrush background)
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
            Width = IconSize,
            Height = IconSize,
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
