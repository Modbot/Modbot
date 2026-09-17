using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Modbot.Overlay;

namespace Modbot.Companion.App;

/// <summary>
/// The overlay's last drawn frame in a desktop window, opened from the Debug page.
/// </summary>
/// <remarks>
/// It shows the very bytes the overlay handed SteamVR, not a second drawing of the same screen,
/// so what appears here is what a headset would show, including whether anything was drawn at
/// all. Refreshed by the same timer as the main window and only when a new frame has been drawn.
/// It reads nothing and sends nothing.
/// </remarks>
internal sealed class OverlayPreviewWindow : Window
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform, Margin = new Thickness(16) };
    private readonly TextBlock _caption = Ui.Faint("");
    private WriteableBitmap? _bitmap;
    private int _framesShown = -1;

    public OverlayPreviewWindow()
    {
        Title = "Modbot overlay";
        Icon = Brand.Icon();
        Width = 560;
        Height = 620;
        Background = Ui.T.BackgroundBrush;

        DockPanel.SetDock(_caption, Dock.Bottom);
        _caption.Margin = new Thickness(16, 0, 16, 12);

        Content = new DockPanel { Children = { _caption, _image } };
    }

    /// <summary>Shows the host's last frame if it is newer than the one on screen.</summary>
    public void Refresh(OverlayHost host)
    {
        var frame = host.LastFrame;
        if (frame.IsEmpty)
        {
            _caption.Text = "Nothing drawn yet.";
            return;
        }

        if (host.FramesDrawn == _framesShown)
            return;

        if (!MemoryMarshal.TryGetArray(frame, out var bytes) || bytes.Array is null)
            return;

        _framesShown = host.FramesDrawn;
        var rowBytes = host.Width * 4;

        _bitmap ??= new WriteableBitmap(
            new PixelSize(host.Width, host.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var locked = _bitmap.Lock())
        {
            for (var row = 0; row < host.Height; row++)
            {
                Marshal.Copy(
                    bytes.Array, bytes.Offset + row * rowBytes,
                    locked.Address + row * locked.RowBytes, rowBytes);
            }
        }

        // Set again rather than invalidated: the image caches the bitmap it was given.
        _image.Source = null;
        _image.Source = _bitmap;
        _caption.Text = $"Frame {host.FramesDrawn}, {host.Width}×{host.Height}";
    }
}
