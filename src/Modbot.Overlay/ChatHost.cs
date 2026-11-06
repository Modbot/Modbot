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
/// The chat overlay, assembled: an Avalonia renderer, a surface to draw into, and the headset
/// runtime. It shows your dictated message, a typing animation, then the answer as it arrives.
/// </summary>
/// <remarks>
/// <para><strong>Hidden until it is shown.</strong> Nothing is drawn and nothing can be pointed at
/// until <see cref="Show"/>; the X, or <see cref="Hide"/>, puts it away again.</para>
/// <para><strong>The same hands as every other panel.</strong> It has the bar with the lock and the
/// hand, is grabbed with the grip and carried, pushed and pulled with either stick and sized at its
/// corner in edit mode, and two quick grips send it back to its own spot. Where it is let go it
/// goes back onto the head, as a pop-up does. Its spot is saved through
/// <see cref="PlacementChanged"/>.</para>
/// <para><strong>Scrolling.</strong> A drag on the messages or the right stick scrolls them. While
/// an answer is arriving the list follows the newest words, until a hand scrolls it up.</para>
/// <para><strong>It works without a headset,</strong> and costs nothing there: the renderer and the
/// texture are made when a runtime attaches and the panel is shown, and let go when it goes away.</para>
/// </remarks>
public sealed class ChatHost : IDisposable
{
    /// <summary>The panel's layout size before it is made wider or taller, in pixels, square.</summary>
    public const int DesignPixels = 400;

    /// <summary>The texture's side, in pixels: the design size at the most the panel can be made.</summary>
    public const int TexturePixels = DesignPixels * (int)OverlayPlacement.MaxShape;

    /// <summary>How wide the panel is in the room to begin with, in metres.</summary>
    public const float DefaultWidth = 0.55f;

    /// <summary>The cursor is placed to this fraction, so a trembling hand does not redraw every poll.</summary>
    private const float CursorStep = 1f / 128f;

    /// <summary>The longest a step of scrolling may take, in seconds, so a pause in the polling does not throw a list.</summary>
    private const double MostScrollStep = 0.1;

    /// <summary>Where the panel starts, and where two quick grips send it: in front of the eyes, an arm's length away.</summary>
    public static OverlayPlacement DefaultPlacement { get; } = new(OverlayAnchor.Head, new OverlayPose(0f, 0f, -0.9f), DefaultWidth);

    private readonly IOverlayRuntime _runtime;

    // The renderer and the texture, made only while a headset is there to show them.
    private readonly Func<OverlayCompositor>? _makeCompositor;
    private OverlayCompositor? _compositor;

    private ChatScreen _drawn = ChatScreen.Empty;
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

    // The typing animation's frame now and in the frame on the panel.
    private int _frame;
    private int _drawnFrame;

    // The messages' scrolling: where they are, whether the newest words are being followed, and the
    // state of a drag that started on them.
    private readonly ListScroll _scroll = new();
    private bool _follow = true;
    private bool _dragging;
    private bool _dragOnList;
    private TimeSpan _lastPoll;

    /// <summary>A panel drawing into a surface that already exists, and starts drawing when shown.</summary>
    public ChatHost(
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
    public ChatHost(
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

        // A trigger on the messages waits to see whether it is a tap or a drag.
        IsScrollableAt = (across, down) => TargetAt(across, down) is OverlayTarget.ChatLog,
    };

    /// <summary>The ordinary construction, at the chat panel's own texture.</summary>
    /// <remarks>
    /// Nothing here touches a graphics card: the texture and the renderer follow the first answer
    /// that says a headset is running.
    /// </remarks>
    public static ChatHost Create(
        int resolution = TexturePixels,
        IOverlayRuntime? runtime = null,
        OverlayPlacement? placement = null)
        => new(
            runtime ?? FallbackOverlayRuntime.CreateFor(OverlayKind.Chat, chatResolution: resolution),
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
    public ChatScreen Showing => _drawn;

    /// <summary>The control the last frame drew lit, because the ray was on it, or null.</summary>
    public OverlayTarget? LitUp => _drawnLit;

    /// <summary>How far the messages are scrolled, in layout pixels.</summary>
    public double ScrollOffset => _scroll.Offset;

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

    /// <summary>Raised when the X is tapped. The panel has already put itself away by then.</summary>
    public event Action? Closed;

    /// <summary>Puts the panel where the settings say. UI thread only.</summary>
    public void Place(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        _interaction.Place(placement);
        Holding = null;
        _runtime.Place(_interaction.Placement);
        Redraw();
    }

    /// <summary>Brings the panel up with what it shows. A panel that was put away starts at the top of a new list. UI thread only.</summary>
    public void Show(ChatScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        if (Hidden)
        {
            Hidden = false;
            _scroll.Reset();
            _follow = true;
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
        _dragging = false;
        _scroll.Touch();
        _runtime.Hide();
    }

    /// <summary>
    /// One look at the controllers: moves the cursor, holds or lets go of the panel, scrolls the
    /// messages and presses the bar's switches and the X. Cheap when nothing is attached. UI thread only.
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
            _dragging = false;
            _scroll.Touch();

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

        var scrolled = ScrollLog(result, now);
        _frame = _drawn.IsTyping ? (int)(now.TotalMilliseconds / ChatView.FrameMilliseconds) % ChatView.Frames : 0;

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
                case OverlayTarget.CloseChat:
                    Hide();
                    Closed?.Invoke();
                    return;
            }
        }

        if (scrolled)
            Draw();
        else
            Redraw();
    }

    /// <summary>
    /// Moves the messages by what the hands did to them this poll: a new touch stops them, a drag
    /// on them moves them with the ray, the right stick moves them by how far it is pushed, and a
    /// drag let go leaves them going. Returns whether they moved.
    /// </summary>
    private bool ScrollLog(InteractionResult result, TimeSpan now)
    {
        var seconds = Math.Clamp((now - _lastPoll).TotalSeconds, 0, MostScrollStep);
        _lastPoll = now;

        if (_root is null || ScrollingList.In(_root) is not { } list)
        {
            _scroll.Touch();
            _dragging = false;
            return false;
        }

        var max = list.MaxOffset;
        var before = _scroll.Offset;

        // The content may have got shorter since the list was last moved.
        _scroll.Clamp(max);

        if (result.Touched)
            _scroll.Touch();

        if (result.Drag is { } drag)
        {
            // Settled once, when the drag starts: the list moves under the ray and the press point may
            // no longer be on it, and the drag goes on until the trigger is up.
            if (!_dragging)
            {
                _dragging = true;
                _dragOnList = TargetAt(drag.From.Across, drag.From.Down) is OverlayTarget.ChatLog;
            }

            // The ray's move is a share of the whole square, which is DesignPixels times the longest side in layout pixels.
            if (_dragOnList)
                _scroll.Drag(-drag.Down * DesignPixels * Placement.Longest, max, now);
        }
        else
        {
            if (_dragging && _dragOnList)
                _scroll.Release(now);

            _dragging = false;

            if (result.Pointer is { } pointer && result.Scroll.Y != 0f && TargetAt(pointer.Across, pointer.Down) is OverlayTarget.ChatLog)
                _scroll.Push(-result.Scroll.Y, seconds, max);
            else
                _scroll.Step(seconds, max);
        }

        var moved = _scroll.Offset != before;

        // A hand that takes the list off the end stops it following the newest words; one that
        // brings it back to the end starts it again.
        if (moved)
            _follow = _scroll.Offset >= max - 1;

        return moved;
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
    public bool Update(ChatScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        if (_everDrawn && _drawn.LooksTheSameAs(screen))
            return false;

        _drawn = screen;
        return Draw();
    }

    /// <summary>Draws again if the cursor, the bar, the lock or the typing animation would change what is on the panel.</summary>
    private void Redraw()
    {
        if (_cursor != _drawnCursor || Bar() != _drawnBar || _lit != _drawnLit || _frame != _drawnFrame)
            Draw();
    }

    private PanelBar Bar() => PanelBar.For(_interaction.Placement, _rayOnPanel && EditMode, _hints, corner: EditMode && Holding is null);

    /// <summary>
    /// Draws the panel as it stands and hands it over. Does nothing while it is put away or there is
    /// no renderer and no texture, which is the ordinary state of a PC with no headset attached: the
    /// messages are kept, and drawn the moment there is one.
    /// </summary>
    private bool Draw()
    {
        if (Hidden || _compositor is null)
            return false;

        _compositor.Invalidate();

        var bar = Bar();

        // Laid out at the design size before the panel is made bigger; the frame is then drawn to fit the texture.
        var placement = _interaction.Placement;
        Control root = PanelFrame.Chat(
            ChatView.Build(_drawn, _scroll.Offset, _frame, _look),
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
        _drawnFrame = _frame;
        _everDrawn = true;

        if (!_compositor.DrawIfChanged(root))
            return false;

        // Kept laid out, so a ray can be matched against where the controls actually are.
        _root = root;

        // Following the newest words: now that the list is laid out its end is known, so if the
        // words have grown past the window the list is moved to the end and drawn again.
        if (_follow && ScrollingList.In(root) is { } list && list.MaxOffset > _scroll.Offset)
        {
            _scroll.ToEnd(list.MaxOffset);
            list.Offset = _scroll.Offset;
            _compositor.Invalidate();
            _compositor.DrawIfChanged(root);
        }

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
