using Avalonia;
using Avalonia.Headless;
using Modbot.Overlay.Driving;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay;

/// <summary>
/// The overlay, assembled: an Avalonia renderer, a shared Direct3D texture, and SteamVR.
/// </summary>
/// <remarks>
/// <para><strong>Three pieces, one rule.</strong> The view is built from a snapshot of the
/// client's local cache, rendered only when that snapshot would look different, and handed to
/// SteamVR as a texture. Nothing here makes a network request, and nothing here can be told what
/// to do by a server.</para>
/// <para><strong>It works without a headset.</strong> With no SteamVR the runtime reports a state,
/// the compositor still draws into the texture, and nothing fails — which matters because most
/// machines running the Modbot Companion are reporting presence from the desktop.</para>
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

    private OverlayScreen _screen = OverlayScreen.Idle;

    public OverlayHost(IOverlayRuntime runtime, IOverlaySurface surface, IFrameRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        _runtime = runtime;
        _surface = surface;
        _compositor = new OverlayCompositor(renderer, surface);
    }

    /// <summary>
    /// The ordinary construction: Avalonia into a shared Direct3D texture on Windows, or into a
    /// frame in memory that SteamVR is handed as bytes everywhere else; shown through SteamVR when
    /// there is one.
    /// </summary>
    public static OverlayHost Create(int resolution = DefaultResolution, IOverlayRuntime? runtime = null)
        => new(
            runtime ?? new OpenVrOverlayRuntime(),
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

    /// <summary>Lets SteamVR be heard: a closing SteamVR detaches the overlay.</summary>
    public void Poll() => _runtime.Poll();

    /// <summary>
    /// Replaces what the overlay shows. Redraws only if the new state would look different, so a
    /// caller may hand over the same screen as often as it likes. <strong>UI thread only:</strong>
    /// a changed screen is built out of Avalonia controls, which refuse any other thread.
    /// </summary>
    public bool Update(OverlayScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        if (_screen.LooksTheSameAs(screen))
            return false;

        _screen = screen;
        _compositor.Invalidate();

        if (!_compositor.DrawIfChanged(OverlayView.Build(screen)))
            return false;

        _runtime.Submit(_surface);
        return true;
    }

    public void Show() => _runtime.Show();

    public void Hide() => _runtime.Hide();

    public void Dispose()
    {
        _runtime.Dispose();
        _compositor.Dispose();
    }
}
