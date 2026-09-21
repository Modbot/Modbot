using Avalonia.Controls;

namespace Modbot.Overlay.Rendering;

/// <summary>
/// Draws the overlay when, and only when, what it shows has changed.
/// </summary>
/// <remarks>
/// <para><strong>Nothing here runs at headset frame rate, and that is the whole performance
/// argument.</strong> An OpenVR overlay texture is submitted once and re-projected by the
/// compositor every frame thereafter, at the headset's own rate, without the application being
/// involved. So the cost that matters is per <em>change</em>, not per frame: a roster row
/// appearing, an alert card arriving, a freshness label ticking over. Those happen a few times a
/// minute, not ninety times a second.</para>
/// <para><strong>This is what makes the offscreen readback affordable.</strong> M3 6.0.1 rejected
/// WPF partly because its only offscreen route is a CPU round trip "GPU to CPU to GPU every frame,
/// at 90 Hz". That premise does not hold for an overlay, and measurement puts a full
/// 1024×1024 frame at roughly 1.3 ms end to end. Avalonia is still the right choice for the
/// reasons that survive — one UI stack across the desktop window and the headset, a small resident
/// footprint beside a game using 8–12 GB, and no runtime to troubleshoot on a volunteer's machine
/// — but not because of a frame budget that was never spent.</para>
/// <para><strong>It shares a machine with VRChat.</strong> Redrawing on a timer regardless of
/// change would take cores and cache from a game that wants every one of both, in exchange for
/// nothing visible.</para>
/// </remarks>
public sealed class OverlayCompositor : IDisposable
{
    private readonly IFrameRenderer _renderer;
    private readonly IOverlaySurface _surface;

    private bool _dirty = true;

    public OverlayCompositor(IFrameRenderer renderer, IOverlaySurface surface)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(surface);

        if (renderer.Width != surface.Width || renderer.Height != surface.Height)
        {
            throw new ArgumentException(
                $"The renderer is {renderer.Width}×{renderer.Height} and the surface is "
                + $"{surface.Width}×{surface.Height}; a mismatch would upload a torn frame.",
                nameof(surface));
        }

        _renderer = renderer;
        _surface = surface;
    }

    /// <summary>The texture the frames land in, for the runtime to be handed.</summary>
    public IOverlaySurface Surface => _surface;

    /// <summary>How many frames have actually been drawn. Zero is the healthy idle case.</summary>
    public int FramesDrawn { get; private set; }

    /// <summary>Whether the next <see cref="DrawIfChanged"/> will do any work.</summary>
    public bool IsDirty => _dirty;

    /// <summary>
    /// Marks the content as changed. Called by whatever produced the change — a new alert, a
    /// refreshed roster, a freshness label that has ticked — rather than discovered by polling.
    /// </summary>
    public void Invalidate() => _dirty = true;

    /// <summary>
    /// Renders and uploads if anything changed since the last call. Returns whether it did.
    /// </summary>
    public bool DrawIfChanged(Control root)
    {
        if (!_dirty)
            return false;

        _surface.Upload(_renderer.Render(root));
        _dirty = false;
        FramesDrawn++;
        return true;
    }

    public void Dispose()
    {
        _surface.Dispose();
        _renderer.Dispose();
    }
}
