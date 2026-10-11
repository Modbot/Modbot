using Avalonia;
using Avalonia.Controls;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay;

/// <summary>
/// The show and hide button, assembled: an Avalonia renderer, a surface to draw into, and the
/// headset runtime.
/// </summary>
/// <remarks>
/// <para><strong>A fixture, pointed at and clicked.</strong> It is always locked
/// (<see cref="OverlayButton.ToPlacement"/>), so a grip cannot pick it up; pointing and the trigger
/// work as on any panel. A click on its face raises <see cref="Pressed"/> and nothing else: what a
/// press means, showing or hiding the headset panel, is the companion's to decide, because the panel
/// belongs to another host.</para>
/// <para><strong>It stays when the panel is hidden.</strong> It is an overlay of its own with a
/// handle of its own, so hiding the headset panel takes down only that panel and Modbot's connection
/// to the VR runtime stays up.</para>
/// <para><strong>Nothing here makes a network request.</strong> It draws one word and a picture.</para>
/// <para><strong>And on a machine with no headset it costs nothing.</strong> Like the other panels,
/// the renderer and the Direct3D texture are made when a runtime actually attaches, and let go when
/// it goes away.</para>
/// </remarks>
public sealed class ButtonHost : IDisposable
{
    private readonly IOverlayRuntime _runtime;

    // The renderer and the texture, made only while a headset is there to show them.
    private readonly Func<OverlayCompositor>? _makeCompositor;
    private OverlayCompositor? _compositor;

    private ButtonScreen _drawn = ButtonScreen.Shown;
    private bool _everDrawn;

    private readonly OverlayInteraction _interaction;
    private Control? _root;
    private PanelCursor? _cursor;
    private PanelCursor? _drawnCursor;

    /// <summary>The cursor is placed to this fraction, so a trembling hand does not redraw every poll.</summary>
    private const float CursorStep = 1f / 128f;

    /// <summary>A button drawing into a surface that already exists, and starts drawing at once.</summary>
    public ButtonHost(
        IOverlayRuntime runtime,
        IOverlaySurface surface,
        IFrameRenderer renderer,
        ButtonPlace place = ButtonPlace.Corner)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(renderer);

        _runtime = runtime;
        _compositor = new OverlayCompositor(renderer, surface);
        Width = surface.Width;
        Height = surface.Height;
        _interaction = Interaction(place);
        _runtime.Place(_interaction.Placement);
    }

    /// <summary>
    /// A button whose renderer and texture wait for a headset: neither is made until a runtime has
    /// attached, and both are let go when it goes away.
    /// </summary>
    public ButtonHost(
        IOverlayRuntime runtime,
        Func<IFrameRenderer> renderer,
        Func<IOverlaySurface> surface,
        ButtonPlace place = ButtonPlace.Corner,
        int resolution = OverlayButton.PanelPixels)
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
        _interaction = Interaction(place);
        _runtime.Place(_interaction.Placement);
    }

    private OverlayInteraction Interaction(ButtonPlace place) => new(OverlayButton.ToPlacement(place), keepOnHead: true)
    {
        // A ray on the clear ground around the card is looking past it.
        IsDrawnAt = (across, down) => _root is { } root && OverlayTargets.Drawn(root, new Point(across * Width, down * Height)),
    };

    /// <summary>The ordinary construction, at the button's own small texture.</summary>
    /// <remarks>
    /// Nothing here touches a graphics card: the texture and the renderer follow the first answer
    /// that says a headset is running.
    /// </remarks>
    public static ButtonHost Create(
        ButtonPlace place = ButtonPlace.Corner,
        int resolution = OverlayButton.PanelPixels,
        IOverlayRuntime? runtime = null)
        => new(
            runtime ?? FallbackOverlayRuntime.CreateFor(OverlayKind.Button, buttonResolution: resolution),
            () => new AvaloniaFrameRenderer(resolution, resolution),
            () => OperatingSystem.IsWindows()
                ? D3D11OverlaySurface.Create(resolution, resolution)
                : new MemoryOverlaySurface(resolution, resolution),
            place,
            resolution);

    public OverlayRuntimeStatus Status => _runtime.Status;

    public int FramesDrawn => _compositor?.FramesDrawn ?? 0;

    /// <summary>Whether the renderer and the texture exist right now.</summary>
    public bool IsDrawing => _compositor is not null;

    public int Width { get; }

    public int Height { get; }

    /// <summary>Where the button is, as the settings last put it.</summary>
    public OverlayPlacement Placement => _interaction.Placement;

    /// <summary>What the button shows right now.</summary>
    public ButtonScreen Showing => _drawn;

    /// <summary>
    /// How the button is drawn: the Modbot look or VRChat's, the same choice the panels make. Drawn
    /// again at once when it changes. UI thread only, like <see cref="Update"/>.
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

    /// <summary>The hand pointing at the button right now, or null.</summary>
    public Hand? Busy { get; private set; }

    /// <summary>Raised when a controller's trigger clicks the button's face. UI thread.</summary>
    public event Action? Pressed;

    /// <summary>Puts the button where the settings say. UI thread only.</summary>
    public void Place(ButtonPlace place)
    {
        _interaction.Place(OverlayButton.ToPlacement(place));
        _runtime.Place(_interaction.Placement);
        Draw();
    }

    /// <summary>
    /// One look at the controllers: moves the cursor and raises <see cref="Pressed"/> on a click on
    /// the button. Cheap when nothing is attached. UI thread only.
    /// </summary>
    /// <param name="elsewhere">A hand another panel over this one is using, left out here.</param>
    public void PollInput(TimeSpan now, Hand? elsewhere = null)
    {
        if (_runtime.Status.State is not OverlayRuntimeState.Running)
        {
            Busy = null;
            if (_cursor is not null)
            {
                _cursor = null;
                Redraw();
            }

            return;
        }

        var result = _interaction.Update(_runtime.ReadTracking(), now, elsewhere);

        Busy = result.Pointer?.Hand;
        _cursor = result.Pointer is { } p
            ? new PanelCursor(MathF.Round(p.Across / CursorStep) * CursorStep, MathF.Round(p.Down / CursorStep) * CursorStep)
            : null;

        // Looked up before the redraw: a press lands on the face that was drawn when it was made.
        var pressed = result.Clicks.Any(click => TargetAt(click.Across, click.Down) is OverlayTarget.PanelButton);

        Redraw();

        if (pressed)
            Pressed?.Invoke();
    }

    /// <summary>What is drawn under a point on the button, from the tree that drew the current frame.</summary>
    public OverlayTarget? TargetAt(float across, float down)
        => _root is null ? null : OverlayTargets.At(_root, new Point(across * Width, down * Height));

    public OverlayRuntimeStatus Start()
    {
        var status = _runtime.Start();

        if (status.State is not OverlayRuntimeState.Running)
            return status;

        _runtime.Place(_interaction.Placement);
        var fresh = Open();

        // Freshly attached: whatever was drawn last is handed over again, so the button comes back
        // as it was. A texture that was only just made holds nothing, so there it is drawn again.
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
    /// Makes the renderer and the texture, if this button owns their making and they are not made
    /// yet. Answers whether it made them. Throws what the graphics card throws.
    /// </summary>
    private bool Open()
    {
        if (_compositor is not null || _makeCompositor is null)
            return false;

        _compositor = _makeCompositor();
        return true;
    }

    /// <summary>Gives the renderer and the texture back, with the graphics device behind them.</summary>
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
    /// and the graphics device go back with it.
    /// </summary>
    public void Poll()
    {
        _runtime.Poll();

        if (_runtime.Status.State is not OverlayRuntimeState.Running)
            LetGo();
    }

    /// <summary>
    /// Replaces what the button shows, drawing only if it would look different from the last one.
    /// <strong>UI thread only:</strong> a changed screen is built out of Avalonia controls.
    /// </summary>
    /// <returns><c>true</c> when a frame was rendered and submitted.</returns>
    public bool Update(ButtonScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        if (_everDrawn && _drawn == screen)
            return false;

        _drawn = screen;
        return Draw();
    }

    /// <summary>Draws again if the cursor would change what is on the button.</summary>
    private void Redraw()
    {
        if (_cursor != _drawnCursor)
            Draw();
    }

    private bool Draw()
    {
        if (_compositor is null)
            return false;

        _compositor.Invalidate();

        var root = ButtonView.Build(_drawn, _look, _cursor);
        _drawnCursor = _cursor;
        _everDrawn = true;

        if (!_compositor.DrawIfChanged(root))
            return false;

        // Kept laid out, so a ray can be matched against where the face actually is.
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
