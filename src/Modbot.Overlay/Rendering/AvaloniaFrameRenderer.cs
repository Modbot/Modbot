using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Modbot.Overlay.Rendering;

/// <summary>Turns an Avalonia visual tree into a frame of pixels.</summary>
public interface IFrameRenderer : IDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>
    /// Lays out and rasterises <paramref name="root"/>, returning premultiplied BGRA, tightly
    /// packed, top row first. The buffer is reused between calls and is only valid until the next
    /// one.
    /// </summary>
    ReadOnlySpan<byte> Render(Control root);
}

/// <summary>
/// Avalonia, rendering offscreen with no window, into a buffer the overlay surface uploads.
/// </summary>
/// <remarks>
/// <para><strong>This is the join that made Avalonia the answer for both surfaces.</strong> The
/// same stack draws the client's ordinary desktop window and this offscreen frame, so the tray UI
/// and the in-headset UI stop being two problems and become one renderer with two hosts. WPF and
/// MAUI have no supported path from rendered UI to a GPU texture at all; Avalonia does because its
/// rendering backend is swappable rather than fixed.</para>
/// <para><strong>The pixel format is chosen to avoid a conversion.</strong> Avalonia's Skia
/// backend produces premultiplied BGRA, which is exactly what
/// <see cref="D3D11OverlaySurface"/> asks Direct3D for, so nothing between here and the headset
/// has to touch the bytes.</para>
/// <para><strong>It draws, and does not look.</strong> This renders a visual tree built from data
/// a paired Modbot server already sent this client. It does not capture the screen, read another
/// window, or sample anything the machine is displaying — and there is no code path here that
/// could, because the only input is a <see cref="Control"/> this program constructed.</para>
/// <para><strong>A detached tree does not re-render on a property change.</strong> Avalonia only
/// re-rasterises what a layout pass has re-measured, so a persistent tree whose bindings change
/// must go through a real top level and a dispatcher turn. That is why the overlay's live path
/// uses <see cref="OverlayCompositor"/>'s explicit re-layout rather than trusting a mutation to
/// take effect on its own — a mistake that is silent, and shows up as a headset card that never
/// updates.</para>
/// </remarks>
public sealed class AvaloniaFrameRenderer : IFrameRenderer
{
    private readonly RenderTargetBitmap _target;
    private readonly byte[] _pixels;

    public AvaloniaFrameRenderer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;

        // 96 DPI, so one design-token pixel is one texture pixel. The overlay's apparent size in
        // the headset is set by its width in metres, not by scaling the raster.
        _target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        _pixels = new byte[width * height * 4];
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlySpan<byte> Render(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);

        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        _target.Render(root);

        var pin = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        try
        {
            _target.CopyPixels(
                new PixelRect(0, 0, Width, Height),
                pin.AddrOfPinnedObject(),
                _pixels.Length,
                Width * 4);
        }
        finally
        {
            pin.Free();
        }

        return _pixels;
    }

    public void Dispose() => _target.Dispose();
}
