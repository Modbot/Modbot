using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Views;

namespace Modbot.UiBoard;

/// <summary>The two looks every surface is drawn in, from the same made-up palette.</summary>
internal static class Looks
{
    /// <summary>The user's Thy Kingdom palette, as VRChat stores it.</summary>
    private static readonly VRChatPalette SamplePalette = new(
        Highlights: Hex("#C53B48"),
        Icons: Hex("#F66229"),
        Buttons: Hex("#934226"),
        Backgrounds: Hex("#67171E"),
        Text: Hex("#FFE07B"),
        Subtext: Hex("#D86049"));

    public static OverlayLook Headset => OverlayLook.Headset;

    /// <summary>VRChat's colours as the desktop windows take them.</summary>
    public static OverlayColours VRChatColours { get; } =
        OverlayColours.From(SamplePalette) ?? throw new InvalidOperationException("The sample palette was refused as unreadable.");

    /// <summary>VRChat's colours, the way the desktop window and the headset's VRChat look take them.</summary>
    public static OverlayLook VRChat { get; } = OverlayLook.FromColours(VRChatColours);

    private static PaletteColour Hex(string text)
        => PaletteColour.TryParse(text, out var colour) ? colour : throw new FormatException(text);
}

/// <summary>Draws a laid-out control tree into a picture, the way the overlay's own tests do.</summary>
internal static class Drawing
{
    /// <summary>The colour behind every picture: a dark, flat stand-in for the world behind a panel.</summary>
    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

    /// <summary>Lays <paramref name="root"/> out at a fixed size over the backdrop and draws it.</summary>
    public static RenderTargetBitmap Draw(Control root, double width, double height)
    {
        var stage = new Border { Background = Backdrop, Child = root };
        var target = new RenderTargetBitmap(new PixelSize((int)width, (int)height), new Vector(96, 96));

        stage.Measure(new Size(width, height));
        stage.Arrange(new Rect(0, 0, width, height));
        target.Render(stage);
        return target;
    }

    /// <summary>
    /// Draws a window that is up as it is on screen, then closes it. Its ground is see-through, so it
    /// goes over the backdrop like the others. <paramref name="topOnly"/> keeps just that many pixels
    /// from the top (the strip along the top of the overlay).
    /// </summary>
    public static RenderTargetBitmap OfWindow(Window window, int? topOnly = null)
        => OfWindowPart(window, _ => null, topOnly);

    /// <summary>
    /// Draws a window that is up, then closes it, keeping only the rectangle <paramref name="part"/>
    /// names (found while the window is still up). A null rectangle keeps the whole window.
    /// </summary>
    public static RenderTargetBitmap OfWindowPart(Window window, Func<Window, Rect?> part, int? topOnly = null)
    {
        try
        {
            Dispatcher.UIThread.RunJobs();
            var keep = part(window);
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window drew no picture.");

            var left = keep is { } k ? (int)Math.Floor(k.X) : 0;
            var top = keep is { } t ? (int)Math.Floor(t.Y) : 0;
            var width = keep is { } w ? (int)Math.Ceiling(w.Width) : frame.PixelSize.Width;
            var height = Math.Min(keep is { } h ? (int)Math.Ceiling(h.Height) : frame.PixelSize.Height, topOnly ?? int.MaxValue);
            var picture = new Image { Source = frame, Width = frame.PixelSize.Width, Height = frame.PixelSize.Height, Stretch = Stretch.None };

            Canvas.SetLeft(picture, -left);
            Canvas.SetTop(picture, -top);

            // A canvas keeps the picture at its own size, and the crop cuts what is outside the rectangle off.
            return Draw(new Canvas { ClipToBounds = true, Children = { picture } }, width, height);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Draws <paramref name="root"/> as wide as asked and as tall as it needs, like a window that sizes to its content.</summary>
    public static RenderTargetBitmap DrawToContent(Control root, double width)
    {
        root.Measure(new Size(width, double.PositiveInfinity));
        return Draw(root, width, Math.Ceiling(root.DesiredSize.Height));
    }
}
