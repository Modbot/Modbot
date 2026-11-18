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
/// The feedback panel, assembled: an Avalonia renderer, a surface to draw into, and the headset
/// runtime. It shows the draft of what was spoken, with the kind to pick, Cancel and Send.
/// </summary>
/// <remarks>
/// <para><strong>Hidden until it is shown.</strong> Nothing is drawn and nothing can be pointed at
/// until <see cref="Show"/>; the X, Cancel or <see cref="Hide"/> puts it away again.</para>
/// <para><strong>The same hands as every other panel.</strong> It has the bar with the lock and the
/// hand, is grabbed with the grip and carried, pushed and pulled with either stick and sized at its
/// corner in edit mode, and two quick grips send it back to its own spot. Where it is let go it
/// goes back onto the head, as a pop-up does. Its spot is saved through
/// <see cref="PlacementChanged"/>, and is its own: it does not share the chat panel's.</para>
/// <para><strong>Nothing is sent from here.</strong> Send raises <see cref="Sent"/> with the kind, the
/// title and the words and the companion does the rest; nothing is saved until then. A Send with no kind
/// chosen or no words is ignored, and while a send is under way Cancel and Send are held.</para>
/// <para><strong>It works without a headset,</strong> and costs nothing there: the renderer and the
/// texture are made when a runtime attaches and the panel is shown, and let go when it goes away.</para>
/// </remarks>
public sealed class FeedbackHost : IDisposable
{
    /// <summary>The panel's layout size before it is made wider or taller, in pixels, square. The chat panel's.</summary>
    public const int DesignPixels = ChatHost.DesignPixels;

    /// <summary>The texture's side, in pixels: the design size at the most the panel can be made.</summary>
    public const int TexturePixels = ChatHost.TexturePixels;

    /// <summary>How wide the panel is in the room to begin with, in metres.</summary>
    public const float DefaultWidth = ChatHost.DefaultWidth;

    /// <summary>The cursor is placed to this fraction, so a trembling hand does not redraw every poll.</summary>
    private const float CursorStep = 1f / 128f;

    /// <summary>Where the panel starts, and where two quick grips send it: in front of the eyes, an arm's length away.</summary>
    public static OverlayPlacement DefaultPlacement { get; } = new(OverlayAnchor.Head, new OverlayPose(0f, 0f, -0.9f), DefaultWidth);

    private readonly IOverlayRuntime _runtime;

    // The renderer and the texture, made only while a headset is there to show them.
    private readonly Func<OverlayCompositor>? _makeCompositor;
    private OverlayCompositor? _compositor;

    private FeedbackScreen _drawn = new(null, string.Empty, string.Empty);
    private bool _everDrawn;

    private readonly OverlayInteraction _interaction;
    private Control? _root;
    private PanelCursor? _cursor;
    private bool _rayOnPanel;

    private OverlayTracking _lastTracking = OverlayTracking.None;
    private ControlHints? _hints;
    private (ControllerProfile? Controller, bool Holding, HintLights Lit)? _hintsFrom;

    private bool _editMode;

    private PanelCursor? _drawnCursor;
    private PanelBar _drawnBar;
    private OverlayTarget? _lit;
    private OverlayTarget? _drawnLit;

    /// <summary>A panel drawing into a surface that already exists, and starts drawing when shown.</summary>
    public FeedbackHost(
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
    /// attached and the panel is shown, and both are let go when it goes away.
    /// </summary>
    public FeedbackHost(
        IOverlayRuntime runtime,
        Func<IFrameRenderer> renderer,
        Func<IOverlaySurface> surface,
        OverlayPlacement? placement = null,
        int resolution = TexturePixels)
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
        placement ?? DefaultPlacement,
        home: DefaultPlacement,
        keepAnchor: OverlayAnchor.Head)
    {
        IsOnBar = (across, down) => EditMode && TargetAt(across, down) is OverlayTarget.Bar or OverlayTarget.Lock or OverlayTarget.ClickThrough,
        IsOnCorner = (across, down) => EditMode && TargetAt(across, down) is OverlayTarget.ResizeCorner,
        ShapeShare = (float)PanelFrame.ChatBoxShare,

        // A ray on the clear ground around the box is looking past the panel; the bar and the clear
        // resize corner count while they can be used.
        IsDrawnAt = (across, down) =>
            (EditMode && TargetAt(across, down) is OverlayTarget.Bar or OverlayTarget.Lock or OverlayTarget.ClickThrough or OverlayTarget.ResizeCorner)
            || (_root is { } root && OverlayTargets.Drawn(root, new Point(across * Width, down * Height))),
    };

    /// <summary>The ordinary construction, at the same texture as the chat panel.</summary>
    /// <remarks>
    /// Nothing here touches a graphics card: the texture and the renderer follow the first answer
    /// that says a headset is running.
    /// </remarks>
    public static FeedbackHost Create(
        int resolution = TexturePixels,
        IOverlayRuntime? runtime = null,
        OverlayPlacement? placement = null)
        => new(
            runtime ?? FallbackOverlayRuntime.CreateFor(OverlayKind.Feedback, chatResolution: resolution),
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

    /// <summary>Whether the panel is put away. It starts that way.</summary>
    public bool Hidden { get; private set; } = true;

    /// <summary>Where the panel is, as a controller or the settings last decided.</summary>
    public OverlayPlacement Placement => _interaction.Placement;

    /// <summary>What the panel shows right now.</summary>
    public FeedbackScreen Showing => _drawn;

    /// <summary>The control the last frame drew lit, because the ray was on it, or null.</summary>
    public OverlayTarget? LitUp => _drawnLit;

    /// <summary>The hand holding the panel, or null.</summary>
    public Hand? Holding { get; private set; }

    /// <summary>The controller hints drawn on the panel right now, or null for none.</summary>
    public ControlHints? Hints => _drawnBar.Hints;

    /// <summary>The hand this panel is using right now, pointing at it or carrying it, or null.</summary>
    public Hand? Busy { get; private set; }

    /// <summary>Where two quick grips on the panel send it.</summary>
    public OverlayPlacement? Home
    {
        get => _interaction.Home;
        set => _interaction.Home = value;
    }

    /// <summary>
    /// How fast the thumbstick pushes and pulls the panel while it is carried, in metres per poll
    /// at full push. Set from the window's Push speed slider.
    /// </summary>
    public float PushStep
    {
        get => _interaction.PushStep;
        set => _interaction.PushStep = value;
    }

    /// <summary>Whether the bar — the lock and the hand — and the resize corner come up at all. Set from the window's Edit mode switch.</summary>
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

    /// <summary>
    /// How the panel is drawn: the Modbot look or VRChat's colours, the same choice the other
    /// panels make. Drawn again at once when it changes. UI thread only.
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

    /// <summary>
    /// Raised whenever a controller changes the placement or a switch on the bar, so it can be
    /// saved. Not raised by <see cref="Place"/>, whose caller already has what it placed.
    /// </summary>
    public event Action<OverlayPlacement>? PlacementChanged;

    /// <summary>Raised when Send is tapped on a draft that can be sent: the kind, the title and the words. The panel stays up until the companion puts it away. UI thread.</summary>
    public event Action<string, string, string>? Sent;

    /// <summary>Raised when Cancel or the X is tapped. The panel has already put itself away by then. UI thread.</summary>
    public event Action? Cancelled;

    /// <summary>Puts the panel where the settings say. UI thread only.</summary>
    public void Place(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        _interaction.Place(placement);
        Holding = null;
        _runtime.Place(_interaction.Placement);
        Redraw();
    }

    /// <summary>Brings the panel up on a new draft. UI thread only.</summary>
    public void Show(FeedbackScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        if (Hidden)
        {
            Hidden = false;
            _everDrawn = false;

            if (_runtime.Status.State is OverlayRuntimeState.Running)
                Open();
        }

        _runtime.Show();
        Update(screen);
    }

    /// <summary>Puts the panel away. UI thread only.</summary>
    public void Hide()
    {
        Hidden = true;
        Busy = null;
        Holding = null;
        _rayOnPanel = false;
        _hints = null;
        _hintsFrom = null;
        _cursor = null;
        _lit = null;
        _runtime.Hide();
    }

    /// <summary>Says why the last send did not go, or clears it. The kind that was picked stays. UI thread only.</summary>
    public void SetProblem(string? problem) => Update(_drawn with { Problem = problem });

    /// <summary>Holds Cancel and Send while a send is under way. UI thread only.</summary>
    public void SetSending(bool sending) => Update(_drawn with { Sending = sending });

    /// <summary>
    /// One look at the controllers: moves the cursor, holds or lets go of the panel and presses the
    /// bar's switches, the kinds, Cancel, Send and the X. Cheap when nothing is attached. UI thread only.
    /// </summary>
    /// <param name="elsewhere">A hand another panel over this one is using, left out here.</param>
    public void PollInput(TimeSpan now, Hand? elsewhere = null)
    {
        // A panel that is put away is not there to be pointed at, so it is read like one with no headset.
        if (Hidden || _runtime.Status.State is not OverlayRuntimeState.Running)
        {
            Busy = null;
            Holding = null;
            _rayOnPanel = false;
            _hints = null;
            _hintsFrom = null;
            _cursor = null;
            _lit = null;

            if (!Hidden)
                Redraw();

            return;
        }

        _lastTracking = _runtime.ReadTracking();
        var result = _interaction.Update(_lastTracking, now, elsewhere);

        Holding = result.Holding;
        Busy = result.Holding ?? result.Pointer?.Hand;
        _rayOnPanel = result.RayOnPanel;
        _hints = HintsFor(result);
        _cursor = result.Pointer is { } p
            ? new PanelCursor(MathF.Round(p.Across / CursorStep) * CursorStep, MathF.Round(p.Down / CursorStep) * CursorStep)
            : null;
        _lit = result.Pointer is { } at && TargetAt(at.Across, at.Down) is { } under && Lit.Lights(under, EditMode) ? under : null;

        if (result.PlacementChanged)
        {
            _runtime.Place(result.Placement);
            PlacementChanged?.Invoke(result.Placement);
        }

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
                case OverlayTarget.FeedbackKind chip:
                    // The kind is held here, so what the companion refreshes (a problem, sending) leaves it alone.
                    Update(_drawn with { Kind = chip.Kind });
                    break;
                case OverlayTarget.FeedbackCancel or OverlayTarget.CloseFeedback when !_drawn.Sending:
                    Hide();
                    Cancelled?.Invoke();
                    return;
                case OverlayTarget.FeedbackSend when _drawn.CanSend:
                    Sent?.Invoke(_drawn.Kind!, _drawn.Title, _drawn.Details);
                    break;
            }
        }

        Redraw();
    }

    /// <summary>
    /// The hints for what a hand is doing to the panel, or none: a hand pointing at it shows how to
    /// take it, a hand holding it shows how to size and push it. The same as the pop-up panel's.
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

        // Attached, but nothing to show: the connection stays and the panel is put away.
        if (Hidden)
        {
            _runtime.Hide();
            return status;
        }

        var fresh = Open();
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
    /// Replaces what the panel shows, drawing only if it would look different from the last one
    /// and the panel is up. <strong>UI thread only:</strong> a changed screen is built out of
    /// Avalonia controls, which refuse any other thread.
    /// </summary>
    /// <returns><c>true</c> when a frame was rendered and submitted.</returns>
    public bool Update(FeedbackScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        if (_everDrawn && _drawn.LooksTheSameAs(screen))
            return false;

        _drawn = screen;
        return Draw();
    }

    /// <summary>Draws again if the cursor, the bar or the lit control would change what is on the panel.</summary>
    private void Redraw()
    {
        if (_cursor != _drawnCursor || Bar() != _drawnBar || _lit != _drawnLit)
            Draw();
    }

    private PanelBar Bar() => PanelBar.For(_interaction.Placement, _rayOnPanel && EditMode, _hints, corner: EditMode && Holding is null);

    /// <summary>
    /// Draws the panel as it stands and hands it over. Does nothing while it is put away or there is
    /// no renderer and no texture, which is the ordinary state of a PC with no headset attached: the
    /// draft is kept, and drawn the moment there is one.
    /// </summary>
    private bool Draw()
    {
        if (Hidden || _compositor is null)
            return false;

        _compositor.Invalidate();

        var bar = Bar();

        // Laid out at the design size before the panel is made bigger; the frame is then drawn to fit the texture.
        var placement = _interaction.Placement;
        Control root = PanelFrame.Feedback(
            FeedbackView.Build(_drawn, _look),
            bar,
            _cursor,
            DesignPixels,
            placement.Wide,
            placement.Tall);
        var fit = Height / (DesignPixels * placement.Longest);
        if (Math.Abs(fit - 1) > 1e-6)
            root = new Zoom(fit, root);

        if (_lit is not null)
            new Lit().Show(root, _lit, _look.Tokens);

        _drawnBar = bar;
        _drawnCursor = _cursor;
        _drawnLit = _lit;
        _everDrawn = true;

        if (!_compositor.DrawIfChanged(root))
            return false;

        // Kept laid out, so a ray can be matched against where the controls actually are.
        _root = root;

        _runtime.Submit(_compositor.Surface);
        return true;
    }

    public void Dispose()
    {
        _runtime.Dispose();
        _compositor?.Dispose();
        _compositor = null;
    }
}
