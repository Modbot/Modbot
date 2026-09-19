using Modbot.Companion.Overlay;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay;

/// <summary>
/// The notification overlay, assembled: an Avalonia renderer, a surface to draw into, and the
/// headset runtime.
/// </summary>
/// <remarks>
/// <para><strong>The same three pieces as the main panel, minus the controllers.</strong> This
/// panel is never pointed at, never grabbed and never tapped, so there is no interaction, no
/// cursor and no hit testing here at all. What it has instead is a placement worked out from the
/// moderator's chosen screen position (two overlay modes design §2).</para>
/// <para><strong>Nothing here makes a network request</strong>, and nothing here can be told what
/// to do by a server: it draws pop-ups the client made out of what it already holds.</para>
/// <para><strong>It works without a headset.</strong> With no SteamVR, and no WiVRn or Monado
/// either, the runtime reports a state and nothing fails.</para>
/// </remarks>
public sealed class NotificationHost : IDisposable
{
    private readonly OverlayCompositor _compositor;
    private readonly IOverlayRuntime _runtime;
    private readonly IOverlaySurface _surface;

    private NotificationScreen _drawn = NotificationScreen.Empty;
    private bool _everDrawn;
    private OverlayPlacement _placement;

    public NotificationHost(
        IOverlayRuntime runtime,
        IOverlaySurface surface,
        IFrameRenderer renderer,
        OverlayPlacement? placement = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(renderer);

        _runtime = runtime;
        _surface = surface;
        _compositor = new OverlayCompositor(renderer, surface);
        _placement = (placement ?? NotificationSettings.Default.ToPlacement()).Clamped();
        _runtime.Place(_placement);
    }

    /// <summary>The ordinary construction, at the notification panel's own smaller resolution.</summary>
    public static NotificationHost Create(
        int resolution = OverlayHost.DefaultNotificationResolution,
        IOverlayRuntime? runtime = null,
        OverlayPlacement? placement = null)
        => new(
            runtime ?? FallbackOverlayRuntime.CreateFor(OverlayKind.Notification, notificationResolution: resolution),
            OperatingSystem.IsWindows()
                ? D3D11OverlaySurface.Create(resolution, resolution)
                : new MemoryOverlaySurface(resolution, resolution),
            new AvaloniaFrameRenderer(resolution, resolution),
            placement);

    public OverlayRuntimeStatus Status => _runtime.Status;

    public int FramesDrawn => _compositor.FramesDrawn;

    /// <summary>Where the panel is, as the settings page last decided.</summary>
    public OverlayPlacement Placement => _placement;

    /// <summary>What the panel shows right now.</summary>
    public NotificationScreen Showing => _drawn;

    /// <summary>Puts the panel where the settings say. UI thread only.</summary>
    public void Place(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        _placement = placement.Clamped();
        _runtime.Place(_placement);
    }

    public OverlayRuntimeStatus Start()
    {
        var status = _runtime.Start();

        // Freshly attached: the panel goes where it was left, and whatever was drawn last is
        // handed over again, so it comes back as it was rather than blank until something changes.
        if (status.State is OverlayRuntimeState.Running)
        {
            _runtime.Place(_placement);
            if (_compositor.FramesDrawn > 0)
                _runtime.Submit(_surface);
        }

        return status;
    }

    /// <summary>Lets the runtime be heard: a closing SteamVR or WiVRn detaches the overlay.</summary>
    public void Poll() => _runtime.Poll();

    /// <summary>
    /// Replaces what the panel shows, drawing only if it would look different from the last one.
    /// <strong>UI thread only:</strong> a changed screen is built out of Avalonia controls, which
    /// refuse any other thread.
    /// </summary>
    /// <returns><c>true</c> when a frame was rendered and submitted.</returns>
    public bool Update(NotificationScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        if (_everDrawn && _drawn.LooksTheSameAs(screen))
            return false;

        _drawn = screen;
        _everDrawn = true;
        _compositor.Invalidate();

        if (!_compositor.DrawIfChanged(NotificationView.Build(screen)))
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
