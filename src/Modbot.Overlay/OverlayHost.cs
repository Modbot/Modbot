using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
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
/// either, the runtime reports a state and nothing fails — which matters because most machines
/// running the Modbot Companion are reporting presence from the desktop.</para>
/// <para><strong>And on those machines it costs nothing.</strong> The renderer and the Direct3D
/// texture are made when a runtime actually attaches, not when the panel is switched on, because
/// a graphics device and a four-megabyte texture on a PC with no headset plugged in are about
/// forty megabytes and forty threads spent on a picture nobody can see. A headset started in the
/// middle of a session is picked up by the same ten-second look that has always attached the
/// panel, and one that goes away hands the texture and the device straight back.</para>
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

    private readonly IOverlayRuntime _runtime;

    // The renderer and the texture. Null on a machine with no headset attached, and made by
    // Open() the moment one is — which is the whole of the saving described above. Null also
    // means Draw does nothing, so the drive loop can go on pushing screens at a panel that has
    // nowhere to put them.
    private readonly Func<OverlayCompositor>? _makeCompositor;
    private OverlayCompositor? _compositor;

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

    /// <summary>A panel drawing into a surface that already exists, and starts drawing at once.</summary>
    public OverlayHost(IOverlayRuntime runtime, IOverlaySurface surface, IFrameRenderer renderer, OverlayPlacement? placement = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(renderer);

        _runtime = runtime;
        Width = surface.Width;
        Height = surface.Height;
        _compositor = new OverlayCompositor(new FrameKeeper(renderer, this), surface);
        _interaction = new OverlayInteraction(placement ?? OverlayPlacement.Default);
        _runtime.Place(_interaction.Placement);
    }

    /// <summary>
    /// A panel whose renderer and texture wait for a headset: neither is made until a runtime has
    /// attached, and both are let go when it goes away.
    /// </summary>
    /// <param name="runtime">The headset side.</param>
    /// <param name="resolution">The texture's size, square, known before there is a texture.</param>
    /// <param name="renderer">Makes the renderer, on the UI thread, when a runtime attaches.</param>
    /// <param name="surface">Makes the texture, at the same moment and on the same thread.</param>
    /// <param name="placement">Where the panel was left, from settings.</param>
    public OverlayHost(
        IOverlayRuntime runtime,
        int resolution,
        Func<IFrameRenderer> renderer,
        Func<IOverlaySurface> surface,
        OverlayPlacement? placement = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);

        _runtime = runtime;
        Width = resolution;
        Height = resolution;
        _makeCompositor = () =>
        {
            var made = renderer();
            try
            {
                return new OverlayCompositor(new FrameKeeper(made, this), surface());
            }
            catch
            {
                // The texture is the half that can fail on a machine with no Direct3D. Letting the
                // renderer go here keeps a failed attach from leaking one every ten seconds.
                made.Dispose();
                throw;
            }
        };

        _interaction = new OverlayInteraction(placement ?? OverlayPlacement.Default);
        _runtime.Place(_interaction.Placement);
    }

    /// <summary>
    /// The ordinary construction: Avalonia into a shared Direct3D texture on Windows, or into a
    /// frame in memory that the runtime is handed as bytes everywhere else; shown through SteamVR
    /// when there is one, and otherwise through WiVRn or Monado (<see cref="FallbackOverlayRuntime"/>).
    /// The placement is where the panel was left, from settings.
    /// </summary>
    /// <remarks>
    /// Nothing here touches a graphics card. What is built is the runtime, which answers "is a
    /// headset running" without a texture; the texture and the renderer follow the first answer
    /// that is yes.
    /// </remarks>
    public static OverlayHost Create(int resolution = DefaultResolution, IOverlayRuntime? runtime = null, OverlayPlacement? placement = null)
        => new(
            runtime ?? FallbackOverlayRuntime.CreateFor(OverlayKind.Main, resolution),
            resolution,
            () => new AvaloniaFrameRenderer(resolution, resolution),
            () => OperatingSystem.IsWindows()
                ? D3D11OverlaySurface.Create(resolution, resolution)
                : new MemoryOverlaySurface(resolution, resolution),
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

    /// <summary>
    /// The group's picture for an address, from the companion's own cache, or null while there is
    /// none. Nothing here fetches anything: the panel asks for what the client is already holding,
    /// and a picture that has not arrived leaves the group's name standing on its own.
    /// </summary>
    public Func<string?, IImage?>? GroupIcon { get; set; }

    /// <summary>Puts the panel somewhere, as the settings page does. UI thread only.</summary>
    public void Place(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        _interaction.Place(placement);
        Holding = null;
        _runtime.Place(_interaction.Placement);
        PlacementChanged?.Invoke(_interaction.Placement);

        // A panel that has just moved on or off a wrist shows a different screen, and the screen
        // itself has not changed, so nothing else would redraw it.
        Draw();
    }

    /// <summary>
    /// Fixes the panel to something else, from the settings page, and puts it where that
    /// anchor makes sense: in front of the head; on the wrist, at the wrist size; or, for the
    /// room, exactly where the panel is right now, so choosing Room pins it rather than sending
    /// it to the room's origin. Opacity and curve stay.
    /// </summary>
    public void Anchor(OverlayAnchor anchor)
    {
        var current = _interaction.Placement;
        var offset = anchor switch
        {
            OverlayAnchor.Head => OverlayPlacement.Default.Offset,
            OverlayAnchor.LeftHand or OverlayAnchor.RightHand => OverlayPlacement.WristOffset,
            _ => (PanelGeometry.PanelPose(current, _lastTracking) ?? _lastTracking.Head.Then(Pose.From(OverlayPlacement.Default.Offset))).ToOverlayPose(),
        };

        Place(current with
        {
            Anchor = anchor,
            Offset = offset,
            Width = OverlayPlacement.WidthFor(anchor, current.Width),
        });
    }

    /// <summary>
    /// Whether the panel is worn on a hand. A wrist is read at a glance, so the panel shows the
    /// wrist screen there instead of the roster (two overlay modes design §3.2).
    /// </summary>
    /// <remarks>
    /// Not while it is being carried. A panel in mid-air on its way to somewhere is anchored to
    /// the hand that is carrying it, and swapping its screen for the wrist one halfway would make
    /// picking the panel up look like breaking it.
    /// </remarks>
    public bool OnWrist =>
        Holding is null && _interaction.Placement.Anchor is OverlayAnchor.LeftHand or OverlayAnchor.RightHand;

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

        var wasHolding = Holding;
        Holding = result.Holding;

        if (result.PlacementChanged)
        {
            _runtime.Place(result.Placement);
            PlacementChanged?.Invoke(result.Placement);
        }

        // Picking the panel up and putting it down change which screen is drawn without changing
        // the screen itself, so nothing else would redraw it.
        if (result.PlacementChanged || wasHolding != Holding)
            Draw();

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

    public int FramesDrawn => _compositor?.FramesDrawn ?? 0;

    /// <summary>Whether the renderer and the texture exist right now.</summary>
    public bool IsDrawing => _compositor is not null;

    public OverlayRuntimeStatus Start()
    {
        var status = _runtime.Start();

        // Freshly attached: the panel goes where it was left, and whatever was drawn last is
        // handed over again, so it comes back as it was rather than blank until something changes.
        // A texture that was only just made holds nothing, so there the screen is drawn again
        // instead, which is what Open arranges by forgetting what was drawn.
        if (status.State is OverlayRuntimeState.Running)
        {
            _runtime.Place(_interaction.Placement);
            Open();

            if (!Draw() && _compositor is { FramesDrawn: > 0 } compositor)
                _runtime.Submit(compositor.Surface);
        }

        return status;
    }

    /// <summary>
    /// Makes the renderer and the texture, if this panel owns their making and they are not made
    /// yet. Throws what the graphics card throws; the caller decides what that means.
    /// </summary>
    private void Open()
    {
        if (_compositor is not null || _makeCompositor is null)
            return;

        _compositor = _makeCompositor();

        // A texture just made is empty, so nothing that was on the panel before is still there.
        _drawn = null;
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

        // Nothing is drawn any more, so nothing is laid out to match a tap against, and the frame
        // the debug window was shown is gone with the texture it came from.
        _drawn = null;
        _root = null;
        _lastFrame = null;
    }

    /// <summary>
    /// Lets the runtime be heard: a closing SteamVR or WiVRn detaches the overlay, and the texture
    /// and the graphics device go back with it rather than being held until the moderator quits.
    /// </summary>
    public void Poll()
    {
        _runtime.Poll();

        if (_runtime.Status.State is not OverlayRuntimeState.Running && !KeepLastFrame)
            LetGo();
    }

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
    /// <remarks>
    /// Switching it on also starts the drawing, headset or no headset: the Debug page's overlay
    /// window shows the very bytes the panel would hand a compositor, and there are no such bytes
    /// while there is no renderer. That is the one thing that draws without a runtime attached,
    /// and it is only ever on in debug mode.
    /// </remarks>
    public bool KeepLastFrame
    {
        get => _keepLastFrame;
        set
        {
            _keepLastFrame = value;

            if (!value)
                return;

            Open();
            Draw();
        }
    }

    private bool _keepLastFrame;

    /// <summary>
    /// The last frame drawn, premultiplied BGRA and tightly packed, while
    /// <see cref="KeepLastFrame"/> is on. Empty otherwise, and before the first draw.
    /// </summary>
    public ReadOnlyMemory<byte> LastFrame => _lastFrame ?? ReadOnlyMemory<byte>.Empty;

    public int Width { get; }

    public int Height { get; }

    private bool Draw()
    {
        // No renderer and no texture: no headset has attached yet, so there is nowhere to put a
        // frame. The screen is kept, and drawn the moment one does.
        if (_compositor is null)
            return false;

        var next = (_pinned ?? _live)?.WithCursor(_cursor);

        // Worn on a wrist, the panel is a sixth of the width it is in front of the head, and the
        // roster at that size is a grey smear. The wrist screen is what it shows there instead.
        if (next is not null && OnWrist)
            next = next with { Page = OverlayPage.Wrist };

        if (next is null || (_drawn is not null && _drawn.LooksTheSameAs(next)))
            return false;

        _drawn = next;
        _compositor.Invalidate();

        var root = OverlayView.Build(next, GroupIcon);
        if (!_compositor.DrawIfChanged(root))
            return false;

        // Kept laid out, so a tap can be matched against what is actually on the panel.
        _root = root;
        _runtime.Submit(_compositor.Surface);
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
        _compositor?.Dispose();
        _compositor = null;
    }
}
