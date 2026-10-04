using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay;

/// <summary>How long one redraw of the dashboard tab took.</summary>
/// <param name="Drawing">Building the page, laying it out, drawing it and copying it into the texture.</param>
/// <param name="Handing">Handing the texture to SteamVR.</param>
/// <param name="Flushing">Sending what the hand-over queued to the graphics card.</param>
public readonly record struct DashboardDrawTime(TimeSpan Drawing, TimeSpan Handing, TimeSpan Flushing);

/// <summary>
/// Modbot's tab in the SteamVR dashboard, assembled: the settings page, the renderer and texture
/// it is drawn into, and SteamVR's side of it.
/// </summary>
/// <remarks>
/// <para><strong>It changes nothing itself.</strong> A press is turned into the setting it asks
/// for and raised as an event; the companion saves it the way the window's own controls do and
/// hands the tab its new settings back through <see cref="Update"/>. One way in for every change,
/// so the tab, the window and <c>settings.json</c> say the same thing.</para>
/// <para><strong>SteamVR's laser, not Modbot's.</strong> The presses arrive as SteamVR's mouse
/// events (<see cref="OpenVrDashboardRuntime"/>); nothing here reads a controller.</para>
/// <para><strong>Like the panels, it costs nothing without a headset.</strong> The renderer and the
/// texture are made when SteamVR is found running, and let go when it closes.</para>
/// </remarks>
public sealed class DashboardHost : IDisposable
{
    /// <summary>The page's texture, across. A 2.5-metre page at the panels' sharpness.</summary>
    public const int PageWidth = 1600;

    /// <summary>The page's texture, down.</summary>
    public const int PageHeight = 1100;

    /// <summary>The picture on the tab's button, square.</summary>
    public const int ThumbnailSide = 256;

    private readonly IDashboardRuntime _runtime;
    private readonly Func<OverlayCompositor>? _makeCompositor;
    private OverlayCompositor? _compositor;

    private DashboardScreen _screen = DashboardScreen.Default;
    private DashboardScreen? _drawn;
    private Control? _root;
    private bool _thumbnailSent;

    /// <summary>The slider the trigger went down on, followed until it comes up.</summary>
    private DashboardSlider? _dragging;

    /// <summary>A page drawing into a surface that already exists. For the tests.</summary>
    public DashboardHost(IDashboardRuntime runtime, IOverlaySurface surface, IFrameRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(renderer);

        _runtime = runtime;
        _compositor = new OverlayCompositor(renderer, surface);
        Width = surface.Width;
        Height = surface.Height;
    }

    /// <summary>A page whose renderer and texture wait until SteamVR is found running.</summary>
    public DashboardHost(IDashboardRuntime runtime, int width, int height, Func<IFrameRenderer> renderer, Func<IOverlaySurface> surface)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        _runtime = runtime;
        Width = width;
        Height = height;
        _makeCompositor = () =>
        {
            var made = renderer();
            try
            {
                return new OverlayCompositor(made, surface());
            }
            catch
            {
                made.Dispose();
                throw;
            }
        };
    }

    /// <summary>
    /// The ordinary construction: SteamVR through OpenVR, drawn into a shared Direct3D texture on
    /// Windows and handed over as bytes elsewhere. Nothing touches a graphics card until SteamVR
    /// is found running.
    /// </summary>
    public static DashboardHost Create()
        => new(
            new OpenVrDashboardRuntime(PageWidth, PageHeight),
            PageWidth,
            PageHeight,
            () => new AvaloniaFrameRenderer(PageWidth, PageHeight),
            () => OperatingSystem.IsWindows()
                ? D3D11OverlaySurface.Create(PageWidth, PageHeight)
                : new MemoryOverlaySurface(PageWidth, PageHeight));

    public int Width { get; }

    public int Height { get; }

    /// <summary>The mark on the tab's button. Set before the tab is first made.</summary>
    public IImage? Mark { get; set; }

    public OverlayRuntimeStatus Status => _runtime.Status;

    public int FramesDrawn => _compositor?.FramesDrawn ?? 0;

    /// <summary>What the tab shows now.</summary>
    public DashboardScreen Showing => _screen;

    /// <summary>The <strong>Overlay on</strong> switch was pressed: the main panel, on or off.</summary>
    public event Action<bool>? OverlayOnChanged;

    /// <summary>One of <strong>Fixed to</strong>'s choices was pressed.</summary>
    public event Action<OverlayAnchor>? AnchorChosen;

    /// <summary><strong>Put it back in front of me</strong> was pressed.</summary>
    public event Action? PutBackPressed;

    /// <summary>The notification overlay's switch, spot or a slider changed it.</summary>
    public event Action<NotifyOverlaySettings>? NotifyOverlayChanged;

    /// <summary>A pop-up tick changed the notification filters.</summary>
    public event Action<NotificationFilters>? FiltersChanged;

    /// <summary>
    /// The trigger went down on the page, before whatever it landed on is acted on. For the log:
    /// it carries how long SteamVR held the press before it was read.
    /// </summary>
    public event Action<DashboardPointer>? PressHeard;

    /// <summary>
    /// How long the last redraw took, or null before the first: building, laying out and drawing
    /// the page into its texture, then handing the texture to SteamVR.
    /// </summary>
    public DashboardDrawTime? LastDraw { get; private set; }

    /// <summary>
    /// Attaches to SteamVR if it is running and makes the tab. Never starts SteamVR. The texture
    /// is made here, the first time it is known there is a SteamVR to show it.
    /// </summary>
    public OverlayRuntimeStatus Start()
    {
        var status = _runtime.Start();
        if (status.State is not OverlayRuntimeState.Running)
            return status;

        if (_compositor is null && _makeCompositor is not null)
        {
            _compositor = _makeCompositor();
            _drawn = null;
        }

        // A tab SteamVR has only just made holds no picture of either kind yet.
        _drawn = null;
        _thumbnailSent = false;
        SendThumbnail();
        Draw();
        return status;
    }

    /// <summary>
    /// Hears SteamVR: the laser's moves and presses are acted on, and SteamVR closing lets the
    /// texture go. UI thread only, because a press redraws the page.
    /// </summary>
    public void Poll()
    {
        _runtime.Poll();

        var pointers = _runtime.TakePointer();
        for (var i = 0; i < pointers.Count; i++)
        {
            // Of a run of moves read in one poll, only the last is acted on. A move while a slider
            // is held is a whole change, a save and a redraw, and SteamVR sends several between two
            // polls, so acting on every one put the tab behind the laser by however many there were.
            // A move with nothing held does nothing, so dropping one changes nothing.
            if (pointers[i].Kind is DashboardPointerKind.Move
                && i + 1 < pointers.Count
                && pointers[i + 1].Kind is DashboardPointerKind.Move)
            {
                continue;
            }

            Handle(pointers[i]);
        }

        if (_runtime.Status.State is not OverlayRuntimeState.Running)
            LetGo();
    }

    /// <summary>
    /// The settings to show. Redraws only when they look different. UI thread only.
    /// </summary>
    public bool Update(DashboardScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        _screen = screen;
        return Draw();
    }

    /// <summary>One thing the laser did, in page pixels from the top left.</summary>
    public void Handle(DashboardPointer pointer)
    {
        switch (pointer.Kind)
        {
            case DashboardPointerKind.Down:
                PressHeard?.Invoke(pointer);
                Press(pointer.X, pointer.Y);
                break;
            case DashboardPointerKind.Move when _dragging is { } slider:
                Slide(slider, pointer.X);
                break;
            case DashboardPointerKind.Up or DashboardPointerKind.Gone:
                _dragging = null;
                break;
        }
    }

    /// <summary>What is drawn under a point on the page, from the tree that drew the current frame.</summary>
    public PlacedDashboardTarget? TargetAt(double x, double y)
        => _root is null ? null : DashboardTargets.At(_root, new Point(x, y));

    /// <summary>Every control on the page as last drawn, with where it is.</summary>
    public IReadOnlyList<PlacedDashboardTarget> Targets
        => _root is null ? [] : DashboardTargets.Find(_root);

    private void Press(double x, double y)
    {
        if (TargetAt(x, y) is not { } placed)
            return;

        var notify = _screen.Notify;
        switch (placed.Target)
        {
            case DashboardTarget.Toggle { Which: DashboardSwitch.Overlay }:
                OverlayOnChanged?.Invoke(!_screen.OverlayOn);
                break;
            case DashboardTarget.Toggle { Which: DashboardSwitch.Notifications }:
                NotifyOverlayChanged?.Invoke(notify with { On = !notify.On });
                break;
            case DashboardTarget.FixTo fix:
                AnchorChosen?.Invoke(fix.Anchor);
                break;
            case DashboardTarget.PutBack:
                PutBackPressed?.Invoke();
                break;
            case DashboardTarget.Spot spot:
                NotifyOverlayChanged?.Invoke(notify with { Spot = spot.Where, Placed = null });
                break;
            case DashboardTarget.PopUp tick:
                FiltersChanged?.Invoke(_screen.Filters.With(NotificationWay.PopUp, tick.Kind, !_screen.Filters.PopUpShows(tick.Kind)));
                break;
            case DashboardTarget.Step step:
                Changed(DashboardEdits.Stepped(notify, step.Which, step.By));
                break;
            case DashboardTarget.Track track:
                _dragging = track.Which;
                Slide(track.Which, x);
                break;
        }
    }

    /// <summary>The slider follows the laser: its knob's middle goes where the laser is on the track.</summary>
    private void Slide(DashboardSlider slider, double x)
    {
        var track = Targets.Cast<PlacedDashboardTarget?>()
            .FirstOrDefault(p => p!.Value.Target is DashboardTarget.Track t && t.Which == slider);

        if (track is not { Bounds: var bounds })
            return;

        var travel = bounds.Width - DashboardView.KnobSide;
        var fraction = travel > 0 ? (x - bounds.X - (DashboardView.KnobSide / 2)) / travel : 0;
        Changed(DashboardEdits.With(_screen.Notify, slider, DashboardEdits.RangeOf(slider).At(fraction)));
    }

    /// <summary>Raised only for a real change, so a laser resting on a slider writes nothing.</summary>
    private void Changed(NotifyOverlaySettings next)
    {
        if (next != _screen.Notify)
            NotifyOverlayChanged?.Invoke(next);
    }

    private bool Draw()
    {
        if (_compositor is null)
            return false;

        if (_drawn is not null && _drawn == _screen)
            return false;

        // Timed with the stopwatch's counter, not the clock: only how long it took matters here.
        var started = Stopwatch.GetTimestamp();

        _compositor.Invalidate();
        var root = DashboardView.Build(_screen, Width, Height);
        if (!_compositor.DrawIfChanged(root))
            return false;

        var drawn = Stopwatch.GetTimestamp();

        _drawn = _screen;
        _root = root;
        _runtime.Submit(_compositor.Surface);
        var handed = Stopwatch.GetTimestamp();

        // Handing SteamVR the texture can queue its copy of it on the shared device, where it
        // would otherwise wait for the next flush: another panel's upload, seconds later, which
        // is a press that moves the notification panel at once and shows on the tab late.
        _compositor.Surface.Flush();

        LastDraw = new DashboardDrawTime(
            Stopwatch.GetElapsedTime(started, drawn),
            Stopwatch.GetElapsedTime(drawn, handed),
            Stopwatch.GetElapsedTime(handed));
        return true;
    }

    /// <summary>The tab's button: the mark, drawn once per tab SteamVR makes.</summary>
    private void SendThumbnail()
    {
        if (_thumbnailSent)
            return;

        using var renderer = new AvaloniaFrameRenderer(ThumbnailSide, ThumbnailSide);
        using var surface = new MemoryOverlaySurface(ThumbnailSide, ThumbnailSide);
        surface.Upload(renderer.Render(Thumbnail(Mark)));
        _thumbnailSent = _runtime.SubmitThumbnail(surface);
    }

    /// <summary>The mark on a rounded square of the page's own colour, or the name where there is no mark.</summary>
    public static Control Thumbnail(IImage? mark)
    {
        var t = DesignTokens.Vr;
        Control inside = mark is not null
            ? new Image { Source = mark, Width = ThumbnailSide * 0.75, Height = ThumbnailSide * 0.75 }
            : new TextBlock
            {
                Text = OpenVrDashboardRuntime.DashboardName,
                FontFamily = new FontFamily(DesignTokens.FontFamily),
                FontSize = 48,
                FontWeight = FontWeight.SemiBold,
                Foreground = t.TextBrush,
            };

        inside.HorizontalAlignment = HorizontalAlignment.Center;
        inside.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Width = ThumbnailSide,
            Height = ThumbnailSide,
            CornerRadius = new CornerRadius(32),
            Background = t.BackgroundBrush,
            Child = inside,
        };
    }

    /// <summary>Gives the renderer and texture back once SteamVR has gone.</summary>
    private void LetGo()
    {
        _dragging = null;

        if (_makeCompositor is null || _compositor is null)
            return;

        _compositor.Dispose();
        _compositor = null;
        _drawn = null;
        _root = null;
    }

    public void Dispose()
    {
        _runtime.Dispose();
        _compositor?.Dispose();
        _compositor = null;
    }
}
