using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Modbot.Overlay.Driving;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay;

/// <summary>
/// The overlay, assembled: an Avalonia renderer, a surface to draw into, and the headset runtime.
/// </summary>
/// <remarks>
/// <para><strong>Three pieces, one rule.</strong> The view is built from a snapshot of the
/// client's local cache, rendered only when that snapshot would look different, and handed to
/// the runtime as a picture. Nothing here makes a network request, and nothing here can be told
/// what to do by a server.</para>
/// <para><strong>It works without a headset.</strong> With no SteamVR, and no WiVRn or Monado
/// either, the runtime reports a state, the compositor still draws into the surface, and nothing
/// fails — which matters because most machines running the Modbot Companion are reporting
/// presence from the desktop.</para>
/// </remarks>
public sealed class OverlayHost : IOverlayPresenter, IDisposable
{
    /// <summary>
    /// Square, and a power of two. Resolution controls sharpness; apparent size in the headset is
    /// the overlay's width in metres, set separately.
    /// </summary>
    public const int DefaultResolution = 1024;

    private readonly OverlayCompositor _compositor;
    private readonly IOverlayRuntime _runtime;
    private readonly IOverlaySurface _surface;

    // What the drive loop last asked for, what the debug page has pinned over it, and what was
    // actually drawn. Nothing counts as drawn until the first Update: an attached overlay that
    // has never been handed a texture shows nothing at all, so even the idle screen is drawn
    // once rather than assumed to be there.
    private OverlayScreen? _live;
    private OverlayScreen? _pinned;
    private OverlayScreen? _drawn;
    private byte[]? _lastFrame;

    public OverlayHost(IOverlayRuntime runtime, IOverlaySurface surface, IFrameRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(renderer);

        _runtime = runtime;
        _surface = surface;
        _compositor = new OverlayCompositor(new FrameKeeper(renderer, this), surface);
    }

    /// <summary>
    /// The ordinary construction: Avalonia into a shared Direct3D texture on Windows, or into a
    /// frame in memory that the runtime is handed as bytes everywhere else; shown through SteamVR
    /// when there is one, and otherwise through WiVRn or Monado (<see cref="FallbackOverlayRuntime"/>).
    /// </summary>
    public static OverlayHost Create(int resolution = DefaultResolution, IOverlayRuntime? runtime = null)
        => new(
            runtime ?? FallbackOverlayRuntime.Create(resolution),
            OperatingSystem.IsWindows()
                ? D3D11OverlaySurface.Create(resolution, resolution)
                : new MemoryOverlaySurface(resolution, resolution),
            new AvaloniaFrameRenderer(resolution, resolution));

    /// <summary>
    /// Configures Avalonia the way both the overlay and the client window need it.
    /// </summary>
    /// <remarks>
    /// The embedded font is not a style choice. An overlay that renders in whatever the machine
    /// happens to have installed — or fails because it has nothing — is unacceptable in the one
    /// moment it matters, and a renderer that fetches a font from a CDN is a network call this
    /// program has no business making. <see cref="DesignTokens.FontFamily"/> still names IBM Plex
    /// Sans first, so a machine that has it matches the web UI exactly; the embedded face is what
    /// every other machine gets.
    /// </remarks>
    public static AppBuilder ConfigureAvalonia<TApp>() where TApp : Application, new()
        => AppBuilder.Configure<TApp>()
            .UseSkia()
            .WithInterFont();

    /// <summary>Brings Avalonia up offscreen, with no window and no message loop.</summary>
    public static AppBuilder ConfigureOffscreen<TApp>() where TApp : Application, new()
        => ConfigureAvalonia<TApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public OverlayRuntimeStatus Status => _runtime.Status;

    public int FramesDrawn => _compositor.FramesDrawn;

    public OverlayRuntimeStatus Start()
    {
        var status = _runtime.Start();

        // Freshly attached: whatever was drawn last is handed over again, so the panel comes back
        // as it was rather than blank until something changes.
        if (status.State is OverlayRuntimeState.Running && _compositor.FramesDrawn > 0)
            _runtime.Submit(_surface);

        return status;
    }

    /// <summary>Lets the runtime be heard: a closing SteamVR or WiVRn detaches the overlay.</summary>
    public void Poll() => _runtime.Poll();

    /// <summary>
    /// Replaces what the overlay shows. Redraws only if the new state would look different, so a
    /// caller may hand over the same screen as often as it likes. <strong>UI thread only:</strong>
    /// a changed screen is built out of Avalonia controls, which refuse any other thread.
    /// </summary>
    public bool Update(OverlayScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        _live = screen;
        return Draw();
    }

    /// <summary>
    /// A screen shown in place of the live one, for the companion's debug page. Null shows the
    /// live screen again. UI thread only, like <see cref="Update"/>.
    /// </summary>
    public OverlayScreen? Pinned
    {
        get => _pinned;
        set
        {
            _pinned = value;
            Draw();
        }
    }

    /// <summary>What the overlay shows right now: the pinned screen, else the live one, else idle.</summary>
    public OverlayScreen Showing => _drawn ?? OverlayScreen.Idle;

    /// <summary>
    /// Whether a copy of each drawn frame is kept for <see cref="LastFrame"/>. Off unless a
    /// window is showing the frame, because the copy is four megabytes a draw.
    /// </summary>
    public bool KeepLastFrame { get; set; }

    /// <summary>
    /// The last frame drawn, premultiplied BGRA and tightly packed, while
    /// <see cref="KeepLastFrame"/> is on. Empty otherwise, and before the first draw.
    /// </summary>
    public ReadOnlyMemory<byte> LastFrame => _lastFrame ?? ReadOnlyMemory<byte>.Empty;

    public int Width => _surface.Width;

    public int Height => _surface.Height;

    private bool Draw()
    {
        var next = _pinned ?? _live;
        if (next is null || (_drawn is not null && _drawn.LooksTheSameAs(next)))
            return false;

        _drawn = next;
        _compositor.Invalidate();

        if (!_compositor.DrawIfChanged(OverlayView.Build(next)))
            return false;

        _runtime.Submit(_surface);
        return true;
    }

    /// <summary>Passes frames through, keeping a copy of the last one when the host asks.</summary>
    private sealed class FrameKeeper(IFrameRenderer inner, OverlayHost host) : IFrameRenderer
    {
        public int Width => inner.Width;

        public int Height => inner.Height;

        public ReadOnlySpan<byte> Render(Control root)
        {
            var frame = inner.Render(root);

            if (host.KeepLastFrame)
            {
                if (host._lastFrame is null || host._lastFrame.Length != frame.Length)
                    host._lastFrame = new byte[frame.Length];

                frame.CopyTo(host._lastFrame);
            }

            return frame;
        }

        public void Dispose() => inner.Dispose();
    }

    public void Show() => _runtime.Show();

    public void Hide() => _runtime.Hide();

    public void Dispose()
    {
        _runtime.Dispose();
        _compositor.Dispose();
    }
}
