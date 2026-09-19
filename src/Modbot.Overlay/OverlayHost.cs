using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Interaction;
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

    /// <summary>
    /// The notification panel's texture. A quarter of the main panel's across, because a pop-up
    /// is three lines and a megabyte of texture to say one name would be a poor trade on a
    /// machine that is also running VRChat.
    /// </summary>
    public const int DefaultNotificationResolution = 256;

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

    // The controllers' side: the rules for holding the panel, the tree that drew the current
    // frame (for finding what a tap landed on), the cursor, and scroll not yet worth a row.
    private readonly OverlayInteraction _interaction;
    private Control? _root;
    private PanelCursor? _cursor;
    private float _scroll;
    private OverlayTracking _lastTracking = OverlayTracking.None;

    /// <summary>Thumbstick travel, in full deflections per poll, that moves the roster one row.</summary>
    public const float ScrollPerRow = 6f;

    /// <summary>The cursor is placed to this fraction, so a trembling hand does not redraw every poll.</summary>
    private const float CursorStep = 1f / 256f;

    public OverlayHost(IOverlayRuntime runtime, IOverlaySurface surface, IFrameRenderer renderer, OverlayPlacement? placement = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(renderer);

        _runtime = runtime;
        _surface = surface;
        _compositor = new OverlayCompositor(new FrameKeeper(renderer, this), surface);
        _interaction = new OverlayInteraction(placement ?? OverlayPlacement.Default);
        _runtime.Place(_interaction.Placement);
    }

    /// <summary>
    /// The ordinary construction: Avalonia into a shared Direct3D texture on Windows, or into a
    /// frame in memory that the runtime is handed as bytes everywhere else; shown through SteamVR
    /// when there is one, and otherwise through WiVRn or Monado (<see cref="FallbackOverlayRuntime"/>).
    /// The placement is where the panel was left, from settings.
    /// </summary>
    public static OverlayHost Create(int resolution = DefaultResolution, IOverlayRuntime? runtime = null, OverlayPlacement? placement = null)
        => new(
            runtime ?? FallbackOverlayRuntime.CreateFor(OverlayKind.Main, resolution),
            OperatingSystem.IsWindows()
                ? D3D11OverlaySurface.Create(resolution, resolution)
                : new MemoryOverlaySurface(resolution, resolution),
            new AvaloniaFrameRenderer(resolution, resolution),
            placement);

    /// <summary>Where the panel is, as last decided by a controller or the settings page.</summary>
    public OverlayPlacement Placement => _interaction.Placement;

    /// <summary>The hand holding the panel, or null.</summary>
    public Hand? Holding { get; private set; }

    /// <summary>Raised whenever the placement changes, by a controller or by <see cref="Place"/>, so it can be saved.</summary>
    public event Action<OverlayPlacement>? PlacementChanged;

    /// <summary>A trigger press on the panel, with what it landed on (null for the empty ground).</summary>
    public event Action<OverlayTarget?>? Tapped;

    /// <summary>Scrolling on the roster, in whole rows; negative is up.</summary>
    public event Action<int>? RosterScrolled;

    /// <summary>Puts the panel somewhere, as the settings page does. UI thread only.</summary>
    public void Place(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        _interaction.Place(placement);
        Holding = null;
        _runtime.Place(_interaction.Placement);
        PlacementChanged?.Invoke(_interaction.Placement);
    }

    /// <summary>
    /// Fixes the panel to something else, from the settings page, and puts it where that
    /// anchor makes sense: in front of the head; just above a hand; or, for the room, exactly
    /// where the panel is right now, so choosing Room pins it rather than sending it to the
    /// room's origin. Size, opacity and curve stay.
    /// </summary>
    public void Anchor(OverlayAnchor anchor)
    {
        var current = _interaction.Placement;
        var offset = anchor switch
        {
            OverlayAnchor.Head => OverlayPlacement.Default.Offset,
            OverlayAnchor.LeftHand or OverlayAnchor.RightHand => OverlayPlacement.HandOffset,
            _ => (PanelGeometry.PanelPose(current, _lastTracking) ?? _lastTracking.Head.Then(Pose.From(OverlayPlacement.Default.Offset))).ToOverlayPose(),
        };

        Place(current with { Anchor = anchor, Offset = offset });
    }

    /// <summary>
    /// One look at the controllers: moves the cursor, holds or lets go of the panel, and raises
    /// taps and scrolls. Cheap when nothing is attached. UI thread only, because a moved cursor
    /// redraws the frame.
    /// </summary>
    public void PollInput(TimeSpan now)
    {
        if (_runtime.Status.State is not OverlayRuntimeState.Running)
        {
            SetCursor(null);
            return;
        }

        _lastTracking = _runtime.ReadTracking();
        var result = _interaction.Update(_lastTracking, now);
        Holding = result.Holding;

        if (result.PlacementChanged)
        {
            _runtime.Place(result.Placement);
            PlacementChanged?.Invoke(result.Placement);
        }

        foreach (var click in result.Clicks)
            Tapped?.Invoke(TargetAt(click.Across, click.Down));

        if (result.Pointer is { } pointer && result.Scroll.Y != 0f
            && TargetAt(pointer.Across, pointer.Down) is OverlayTarget.Roster or OverlayTarget.Person or OverlayTarget.Events)
        {
            // Thumbstick up scrolls the list up, towards the rows above.
            _scroll -= result.Scroll.Y / ScrollPerRow;
            var rows = (int)Math.Truncate(_scroll);
            if (rows != 0)
            {
                _scroll -= rows;
                RosterScrolled?.Invoke(rows);
            }
        }
        else
        {
            _scroll = 0f;
        }

        SetCursor(result.Pointer is { } p
            ? new PanelCursor(MathF.Round(p.Across / CursorStep) * CursorStep, MathF.Round(p.Down / CursorStep) * CursorStep)
            : null);
    }

    /// <summary>What is drawn under a point on the panel, from the tree that drew the current frame.</summary>
    public OverlayTarget? TargetAt(float across, float down)
        => _root is null ? null : OverlayTargets.At(_root, new Point(across * Width, down * Height));

    private void SetCursor(PanelCursor? cursor)
    {
        if (_cursor == cursor)
            return;

        _cursor = cursor;
        Draw();
    }

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

        // Freshly attached: the panel goes where it was left, and whatever was drawn last is
        // handed over again, so it comes back as it was rather than blank until something changes.
        if (status.State is OverlayRuntimeState.Running)
        {
            _runtime.Place(_interaction.Placement);
            if (_compositor.FramesDrawn > 0)
                _runtime.Submit(_surface);
        }

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
        var next = (_pinned ?? _live)?.WithCursor(_cursor);
        if (next is null || (_drawn is not null && _drawn.LooksTheSameAs(next)))
            return false;

        _drawn = next;
        _compositor.Invalidate();

        var root = OverlayView.Build(next);
        if (!_compositor.DrawIfChanged(root))
            return false;

        // Kept laid out, so a tap can be matched against what is actually on the panel.
        _root = root;
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
