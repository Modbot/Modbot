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
/// <para><strong>And on those machines it costs nothing.</strong> Like the main panel, the
/// renderer and the Direct3D texture are made when a runtime actually attaches rather than when
/// the panel is switched on, and let go when it goes away.</para>
/// </remarks>
public sealed class NotificationHost : IDisposable
{
    private readonly IOverlayRuntime _runtime;

    // The renderer and the texture, made only while a headset is there to show them. Null means
    // pop-ups are kept and not drawn; see OverlayHost for the reasoning in full.
    private readonly Func<OverlayCompositor>? _makeCompositor;
    private OverlayCompositor? _compositor;

    private NotificationScreen _drawn = NotificationScreen.Empty;
    private bool _everDrawn;
    private OverlayPlacement _placement;

    /// <summary>A panel drawing into a surface that already exists, and starts drawing at once.</summary>
    public NotificationHost(
        IOverlayRuntime runtime,
        IOverlaySurface surface,
        IFrameRenderer renderer,
        OverlayPlacement? placement = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(renderer);

        _runtime = runtime;
        _compositor = new OverlayCompositor(renderer, surface);
        _placement = (placement ?? NotifyOverlaySettings.Default.ToPlacement()).Clamped();
        _runtime.Place(_placement);
    }

    /// <summary>
    /// A panel whose renderer and texture wait for a headset: neither is made until a runtime has
    /// attached, and both are let go when it goes away.
    /// </summary>
    public NotificationHost(
        IOverlayRuntime runtime,
        Func<IFrameRenderer> renderer,
        Func<IOverlaySurface> surface,
        OverlayPlacement? placement = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(surface);

        _runtime = runtime;
        _makeCompositor = () =>
        {
            var made = renderer();
            try
            {
                return new OverlayCompositor(made, surface());
            }
            catch
            {
                // The texture is the half that can fail on a machine with no Direct3D. Letting the
                // renderer go here keeps a failed attach from leaking one every ten seconds.
                made.Dispose();
                throw;
            }
        };

        _placement = (placement ?? NotifyOverlaySettings.Default.ToPlacement()).Clamped();
        _runtime.Place(_placement);
    }

    /// <summary>The ordinary construction, at the notification panel's own smaller resolution.</summary>
    /// <remarks>
    /// Nothing here touches a graphics card: the texture and the renderer follow the first answer
    /// that says a headset is running.
    /// </remarks>
    public static NotificationHost Create(
        int resolution = OverlayHost.DefaultNotificationResolution,
        IOverlayRuntime? runtime = null,
        OverlayPlacement? placement = null)
        => new(
            runtime ?? FallbackOverlayRuntime.CreateFor(OverlayKind.Notification, notificationResolution: resolution),
            () => new AvaloniaFrameRenderer(resolution, resolution),
            () => OperatingSystem.IsWindows()
                ? D3D11OverlaySurface.Create(resolution, resolution)
                : new MemoryOverlaySurface(resolution, resolution),
            placement);

    public OverlayRuntimeStatus Status => _runtime.Status;

    public int FramesDrawn => _compositor?.FramesDrawn ?? 0;

    /// <summary>Whether the renderer and the texture exist right now.</summary>
    public bool IsDrawing => _compositor is not null;

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

        if (status.State is not OverlayRuntimeState.Running)
            return status;

        _runtime.Place(_placement);
        var fresh = Open();

        // Freshly attached: whatever was drawn last is handed over again, so the panel comes back
        // as it was rather than blank until something changes. A texture that was only just made
        // holds nothing, so there the pop-ups are drawn again instead.
        if (fresh)
        {
            if (_everDrawn)
                Draw();
        }
        else if (_compositor is { FramesDrawn: > 0 } compositor)
        {
            _runtime.Submit(compositor.Surface);
        }

        return status;
    }

    /// <summary>
    /// Makes the renderer and the texture, if this panel owns their making and they are not made
    /// yet. Answers whether it made them. Throws what the graphics card throws.
    /// </summary>
    private bool Open()
    {
        if (_compositor is not null || _makeCompositor is null)
            return false;

        _compositor = _makeCompositor();
        return true;
    }

    /// <summary>
    /// Gives the renderer and the texture back, with the graphics device behind them. Only for a
    /// panel that can make them again: one handed a surface keeps the surface it was handed.
    /// </summary>
    private void LetGo()
    {
        if (_makeCompositor is null || _compositor is null)
            return;

        _compositor.Dispose();
        _compositor = null;
    }

    /// <summary>
    /// Lets the runtime be heard: a closing SteamVR or WiVRn detaches the overlay, and the texture
    /// and the graphics device go back with it rather than being held until the moderator quits.
    /// </summary>
    public void Poll()
    {
        _runtime.Poll();

        if (_runtime.Status.State is not OverlayRuntimeState.Running)
            LetGo();
    }

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
        return Draw();
    }

    /// <summary>
    /// Draws the pop-ups as they stand and hands them over. Does nothing while there is no
    /// renderer and no texture, which is the ordinary state of a PC with no headset attached: the
    /// pop-ups are kept, and drawn the moment one is.
    /// </summary>
    private bool Draw()
    {
        if (_compositor is null)
            return false;

        _compositor.Invalidate();

        if (!_compositor.DrawIfChanged(NotificationView.Build(_drawn)))
            return false;

        _runtime.Submit(_compositor.Surface);
        return true;
    }

    public void Show() => _runtime.Show();

    public void Hide() => _runtime.Hide();

    public void Dispose()
    {
        _runtime.Dispose();
        _compositor?.Dispose();
        _compositor = null;
    }
}
