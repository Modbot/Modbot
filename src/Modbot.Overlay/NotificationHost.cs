using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.OpenXr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay;

/// <summary>
/// The notification overlay, assembled: an Avalonia renderer, a surface to draw into, and the
/// headset runtime.
/// </summary>
/// <remarks>
/// <para><strong>The same three pieces as the main panel, and the same hands.</strong> It is picked
/// up with the grip, carried, pushed and pulled with either stick and sized with two hands, exactly
/// as the main panel is, and it has the same bar under it with the same lock and click-through.
/// The one difference is where it goes when it is let go: back onto the head, where the hand left
/// it as seen from the eyes, because a pop-up belongs in the corner of the view wherever the
/// moderator looks. Nothing on it but the bar can be tapped.</para>
/// <para><strong>A box to take hold of.</strong> It used to draw nothing at all while there were
/// no pop-ups, which left nothing to grab. It now draws a dashed outline of itself while it can be
/// moved; locked, the outline goes and an empty panel draws nothing again until a ray lands on it
/// and the bar comes up. While it can be moved the whole box is the panel as far as a ray is
/// concerned, so it can be taken anywhere on it, between the cards or with none up.</para>
/// <para><strong>Hints for the hands.</strong> A hand pointing at the box, or holding it, shows
/// the same grip hints as the main panel, drawn over the foot of the box because the bar's row has
/// no room for them.</para>
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

    // The controllers' side, as on the main panel: the rules for holding it, the tree that drew
    // the current frame (for finding the bar under a ray), the cursor and whether the bar is up.
    private readonly OverlayInteraction _interaction;
    private Control? _root;
    private PanelCursor? _cursor;
    private bool _rayOnPanel;

    // What the hands are doing to the panel, for the hints. The hints are made again only when
    // what they are made from changes, not on every poll.
    private OverlayTracking _lastTracking = OverlayTracking.None;
    private ControlHints? _hints;
    private (ControllerProfile? Controller, bool Holding, HintLights Lit)? _hintsFrom;

    private bool _editMode;

    /// <summary>
    /// Whether the bar under the pop-ups — the lock and the hand — comes up at all. Off by
    /// default, set from the window's Edit mode switch, as for the main panel.
    /// </summary>
    /// <summary>
    /// How fast the thumbstick pushes and pulls the panel while it is carried, in metres per poll
    /// at full push. Set from the window's Push speed slider.
    /// </summary>
    public float PushStep
    {
        get => _interaction.PushStep;
        set => _interaction.PushStep = value;
    }

    public bool EditMode
    {
        get => _editMode;
        set
        {
            if (_editMode == value)
                return;

            _editMode = value;
            Redraw();
        }
    }

    // What the last frame showed besides the pop-ups, so a moved cursor or a bar coming up redraws
    // and nothing else does.
    private PanelCursor? _drawnCursor;
    private PanelBar _drawnBar;

    /// <summary>The cursor is placed to this fraction, so a trembling hand does not redraw every poll.</summary>
    private const float CursorStep = 1f / 128f;

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
        Width = surface.Width;
        Height = surface.Height;
        _interaction = Interaction(placement);
        _runtime.Place(_interaction.Placement);
    }

    /// <summary>
    /// A panel whose renderer and texture wait for a headset: neither is made until a runtime has
    /// attached, and both are let go when it goes away.
    /// </summary>
    public NotificationHost(
        IOverlayRuntime runtime,
        Func<IFrameRenderer> renderer,
        Func<IOverlaySurface> surface,
        OverlayPlacement? placement = null,
        int resolution = OverlayHost.DefaultNotificationResolution)
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

        Width = resolution;
        Height = resolution;
        _interaction = Interaction(placement);
        _runtime.Place(_interaction.Placement);
    }

    private OverlayInteraction Interaction(OverlayPlacement? placement) => new(
        placement ?? NotifyOverlaySettings.Default.ToPlacement(),
        home: NotifyOverlaySettings.Default.SpotPlacement(),
        keepOnHead: true)
    {
        IsOnBar = (across, down) => EditMode && TargetAt(across, down) is OverlayTarget.Bar or OverlayTarget.Lock or OverlayTarget.ClickThrough,

        // A ray on the clear ground around the box is looking past the panel; the box counts whole
        // while it can be moved, and the bar while it can be used.
        IsDrawnAt = (across, down) =>
            (EditMode && TargetAt(across, down) is OverlayTarget.Bar or OverlayTarget.Lock or OverlayTarget.ClickThrough)
            || TargetAt(across, down) is OverlayTarget.PopUpBox
            || (_root is { } root && OverlayTargets.Drawn(root, new Point(across * Width, down * Height))),
    };

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
            placement,
            resolution);

    public OverlayRuntimeStatus Status => _runtime.Status;

    public int FramesDrawn => _compositor?.FramesDrawn ?? 0;

    /// <summary>Whether the renderer and the texture exist right now.</summary>
    public bool IsDrawing => _compositor is not null;

    public int Width { get; }

    public int Height { get; }

    /// <summary>Where the panel is, as a controller or the settings page last decided.</summary>
    public OverlayPlacement Placement => _interaction.Placement;

    /// <summary>What the panel shows right now.</summary>
    public NotificationScreen Showing => _drawn;

    /// <summary>
    /// How the pop-ups are drawn: the Modbot look they have always had, or VRChat's colours and
    /// cards, the same choice the main panel makes. Drawn again at once when it changes. UI thread
    /// only, like <see cref="Update"/>.
    /// </summary>
    public OverlayLook Look
    {
        get => _look;
        set
        {
            value ??= OverlayLook.Headset;
            if (ReferenceEquals(_look, value))
                return;

            _look = value;
            Draw();
        }
    }

    private OverlayLook _look = OverlayLook.Headset;

    /// <summary>The hand holding the panel, or null.</summary>
    public Hand? Holding { get; private set; }

    /// <summary>The controller hints drawn on the panel right now, or null for none.</summary>
    public ControlHints? Hints => _drawnBar.Hints;

    /// <summary>The hand this panel is using right now, pointing at it or carrying it, or null.</summary>
    public Hand? Busy { get; private set; }

    /// <summary>Where two quick grips on the panel send it: the chosen spot, at the default distance.</summary>
    public OverlayPlacement? Home
    {
        get => _interaction.Home;
        set => _interaction.Home = value;
    }

    /// <summary>
    /// Raised whenever a controller changes the placement or a switch on the bar, so it can be
    /// saved. Not raised by <see cref="Place"/>, whose caller already has what it placed.
    /// </summary>
    public event Action<OverlayPlacement>? PlacementChanged;

    /// <summary>Puts the panel where the settings say. UI thread only.</summary>
    public void Place(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        _interaction.Place(placement);
        Holding = null;
        _runtime.Place(_interaction.Placement);
        Redraw();
    }

    /// <summary>
    /// One look at the controllers: moves the cursor, holds or lets go of the panel, and presses
    /// the bar's switches. Cheap when nothing is attached. UI thread only.
    /// </summary>
    public void PollInput(TimeSpan now)
    {
        if (_runtime.Status.State is not OverlayRuntimeState.Running)
        {
            Busy = null;
            Holding = null;
            _rayOnPanel = false;
            _hints = null;
            _hintsFrom = null;
            _cursor = null;
            Redraw();
            return;
        }

        _lastTracking = _runtime.ReadTracking();
        var result = _interaction.Update(_lastTracking, now);

        Holding = result.Holding;
        Busy = result.Holding ?? result.Pointer?.Hand;
        _rayOnPanel = result.RayOnPanel;
        _hints = HintsFor(result);
        _cursor = result.Pointer is { } p
            ? new PanelCursor(MathF.Round(p.Across / CursorStep) * CursorStep, MathF.Round(p.Down / CursorStep) * CursorStep)
            : null;

        if (result.PlacementChanged)
        {
            _runtime.Place(result.Placement);
            PlacementChanged?.Invoke(result.Placement);
        }

        // The bar's switches are the only things on this panel a click can land on.
        foreach (var click in result.Clicks)
        {
            switch (TargetAt(click.Across, click.Down))
            {
                case OverlayTarget.Lock when EditMode:
                    Switch(Placement with { Locked = !Placement.Locked });
                    break;
                case OverlayTarget.ClickThrough when EditMode:
                    Switch(Placement with { ClickThrough = !Placement.ClickThrough });
                    break;
            }
        }

        Redraw();
    }

    /// <summary>
    /// The hints for what a hand is doing to the panel, or none: a hand pointing at it shows how to
    /// take it, a hand holding it shows how to size and push it, and a pill is lit while the
    /// control it names is down. The same as the main panel's (<c>OverlayHost.HintsFor</c>), minus
    /// the wrist, which this panel never goes on.
    /// </summary>
    private ControlHints? HintsFor(InteractionResult result)
    {
        if ((result.Holding ?? result.Pointer?.Hand) is not { } hand)
        {
            _hintsFrom = null;
            return null;
        }

        var controller = (_runtime as IControllerKind)?.ControllerOf(hand);
        var holding = result.Holding is not null;
        var mine = _lastTracking[hand];
        var other = _lastTracking[hand == Hand.Left ? Hand.Right : Hand.Left];
        var lit = new HintLights(
            mine.Grab,
            other.Tracked && other.Grab,
            OverlayInteraction.Stick(mine.Scroll) != Vector2.Zero
                || (other.Tracked && OverlayInteraction.Stick(other.Scroll) != Vector2.Zero));

        // Whether the panel is locked or lets rays through is decided when it is drawn, in PanelBar.For.
        var from = (controller, holding, lit);
        if (_hintsFrom != from || _hints is null)
        {
            _hintsFrom = from;
            return ControlHints.For(controller, holding, lit);
        }

        return _hints;
    }

    /// <summary>What is drawn under a point on the panel, from the tree that drew the current frame.</summary>
    public OverlayTarget? TargetAt(float across, float down)
        => _root is null ? null : OverlayTargets.At(_root, new Point(across * Width, down * Height));

    private void Switch(OverlayPlacement placement)
    {
        _interaction.Place(placement);
        _runtime.Place(_interaction.Placement);
        PlacementChanged?.Invoke(_interaction.Placement);
    }

    public OverlayRuntimeStatus Start()
    {
        var status = _runtime.Start();

        if (status.State is not OverlayRuntimeState.Running)
            return status;

        _runtime.Place(_interaction.Placement);
        var fresh = Open();

        // Freshly attached: whatever was drawn last is handed over again, so the panel comes back
        // as it was rather than blank until something changes. A texture that was only just made
        // holds nothing, so there the panel is drawn again instead, pop-ups or not, since an
        // unlocked panel shows its outline either way.
        if (fresh)
        {
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
        _root = null;
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
        return Draw();
    }

    /// <summary>Draws again if the cursor, the bar or the lock would change what is on the panel.</summary>
    private void Redraw()
    {
        if (_cursor != _drawnCursor || Bar() != _drawnBar)
            Draw();
    }

    private PanelBar Bar() => PanelBar.For(_interaction.Placement, _rayOnPanel && EditMode, _hints);

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

        var bar = Bar();
        var root = PanelFrame.Notification(NotificationView.Build(_drawn, _look), bar, _cursor, Height);
        _drawnBar = bar;
        _drawnCursor = _cursor;
        _everDrawn = true;

        if (!_compositor.DrawIfChanged(root))
            return false;

        // Kept laid out, so a ray can be matched against where the bar actually is.
        _root = root;
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
