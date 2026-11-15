using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Modbot.Companion.App;

/// <summary>
/// The resize corner the desktop windows wear at their bottom right: two thick, see-through slanted
/// strokes on nothing, the same mark the headset panels' corner has (<c>PanelBar.Grip</c>), at a
/// size a mouse can hit. The square around the strokes is clear but still takes a press.
/// </summary>
internal static class ResizeCorner
{
    /// <summary>The corner's side, in the window's own units.</summary>
    internal const double Side = 32;

    /// <summary>A corner with nothing drawn on it yet and not shown; <see cref="Ink"/> draws it.</summary>
    internal static Border Create() => new()
    {
        Width = Side,
        Height = Side,
        Background = Brushes.Transparent,
        Cursor = new Cursor(StandardCursorType.BottomRightCorner),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        IsVisible = false,
    };

    /// <summary>Draws the two strokes on the corner in this ink, which a look change redraws.</summary>
    internal static void Ink(Border corner, IBrush ink)
        => corner.Child = new Viewbox
        {
            Width = Side * 0.85,
            Height = Side * 0.85,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M21,3 L3,21 M21,12 L12,21"),
                Stroke = ink,
                StrokeThickness = 3.2,
                StrokeLineCap = PenLineCap.Round,
                Width = 24,
                Height = 24,
            },
        };
}
