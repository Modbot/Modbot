using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Core.Users;

namespace Modbot.Overlay.Views;

/// <summary>
/// The marks drawn beside a person's name: their trust rank and Modbot's 18+ mark.
/// </summary>
/// <remarks>
/// One drawing for every place that shows them — the headset roster row and the join pop-up, on
/// the headset and on a monitor — so a rank looks the same wherever a moderator meets it. Each
/// takes the tokens of the surface it is drawn on.
/// </remarks>
public static class PersonMarks
{
    /// <summary>
    /// A trust rank as a small mark in VRChat's colour for it, with the rank's name in dim text
    /// beside it. The colour is the second channel and the word carries the meaning, so a rank
    /// whose VRChat colour is dark on a dark panel still reads.
    /// </summary>
    /// <remarks>
    /// The dot is sized to the surface's small text, which keeps it at ten pixels on the headset,
    /// where it was drawn first, and smaller beside a monitor's smaller text.
    /// </remarks>
    public static StackPanel RankLine(TrustRank rank, double size, DesignTokens t)
    {
        ArgumentNullException.ThrowIfNull(t);

        var dot = t.Density.TextSmall * 0.625;
        var mark = new Ellipse
        {
            Width = dot,
            Height = dot,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = DesignTokens.Brush(Color.Parse(TrustRanks.Colour(rank))),
        };

        var name = Text(TrustRanks.Name(rank), size, t.TextDimBrush);
        name.VerticalAlignment = VerticalAlignment.Center;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { mark, name },
        };
    }

    /// <summary>
    /// Modbot's 18+ mark, in the green the website's Members list gives it. Shown only on those who
    /// carry it, so somebody without it gets no mark at all.
    /// </summary>
    public static Control EighteenPlusChip(DesignTokens t)
    {
        ArgumentNullException.ThrowIfNull(t);

        var label = Text("18+", t.Density.TextSmall, t.OkBrush, FontWeight.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Background = new SolidColorBrush(t.Palette.Ok, 0.16),
            BorderBrush = t.OkBrush,
            BorderThickness = new Thickness(t.Density.Hairline),
            CornerRadius = t.CornerRadius,
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
    }

    private static TextBlock Text(string content, double size, IBrush brush, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = content,
        FontSize = size,
        FontFamily = new FontFamily(DesignTokens.FontFamily),
        FontWeight = weight,
        Foreground = brush,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 1,
    };
}
