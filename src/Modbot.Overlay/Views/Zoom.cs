using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Modbot.Overlay.Views;

/// <summary>
/// Lays its child out at a smaller size and draws it bigger: the headset's VRChat look is the desktop
/// window's layout, one design size, drawn at the size a texture of the headset's needs.
/// </summary>
/// <remarks>
/// <para>The child keeps its own sizes (a 18-pixel line of text is 18 in its layout) and is measured
/// in the room the scale leaves it, so everything on it, words and pictures and gaps alike, is bigger by
/// the same step and nothing has to be told. This is how the words are made bigger in a headset, where
/// a panel is read from a metre off.</para>
/// <para><strong>The child's bounds are its own.</strong> A tap is matched against what was laid out
/// (<see cref="Interaction.OverlayTargets"/>), which knows to multiply by <see cref="Scale"/> when it
/// walks into one of these.</para>
/// </remarks>
internal sealed class Zoom : Decorator
{
    public Zoom(double scale, Control child)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "A scale is a number above zero.");

        Scale = scale;
        ClipToBounds = true;

        child.RenderTransformOrigin = RelativePoint.TopLeft;
        child.RenderTransform = new ScaleTransform(scale, scale);
        Child = child;
    }

    /// <summary>How many times bigger than its layout the child is drawn.</summary>
    public double Scale { get; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null)
            return default;

        Child.Measure(new Size(availableSize.Width / Scale, availableSize.Height / Scale));
        return new Size(Child.DesiredSize.Width * Scale, Child.DesiredSize.Height * Scale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, finalSize.Width / Scale, finalSize.Height / Scale));
        return finalSize;
    }
}
