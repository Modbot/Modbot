using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Modbot.Companion.Presentation;
using Modbot.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;
using Modbot.Shared.HeadsUps;

namespace Modbot.Companion.App;

/// <summary>
/// The overlay over VRChat on a monitor: the same panel a headset shows, in a window that sits on
/// top of the game and answers a mouse and a keyboard.
/// </summary>
/// <remarks>
/// <para><strong>It is the panel, not a picture of it.</strong> <see cref="OverlayView"/> builds
/// real controls, and the headset path only rasterises them; this window hosts the very same
/// controls, so the roster, the flagged-join card and a person's card are one piece of code with
/// one set of rules. A click is found with <see cref="OverlayTargets"/> — the same lookup a
/// controller's ray uses — and handed to the same drive loop, so there is no second set of
/// interactions to keep in step.</para>
/// <para><strong>It reads nothing and sends nothing.</strong> Screens are pushed into it by the
/// drive loop, which renders from the client's own cache. This window has no client, no address
/// and no socket, and no server can tell it to appear, to hide, or to show anything.</para>
/// <para><strong>Over the game.</strong> Always on top, no border and no taskbar button; it is
/// created hidden, so it never takes VRChat's keyboard until the moderator's shortcut asks for it.
/// A borderless-windowed game — VRChat's own default — is drawn over normally. True exclusive
/// fullscreen is the one case that cannot work, because the game owns the display and nothing
/// short of drawing inside its own graphics device would appear there; that is a thing this client
/// must never do. The desktop overlay design spec (2026-09-18, §3.4) has the detail.</para>
/// <para><strong>Only the panel.</strong> There used to be a feed of the client's own recent events
/// pinned under it, which made the window tall, made the panel small, and told a moderator about
/// things after the moment had passed. Being told as it happens is the notification overlay's job
/// now — its own window, in a corner, which this one neither owns nor summons (§7).</para>
/// <para><strong>The lock and the hand.</strong> In edit mode the bar under the panel carries the
/// same two switches as the bar under a headset panel. Locked, neither the title strip nor the bar
/// drags the window. Click-through, a click on the panel goes to the game underneath instead; the
/// title strip and the bar go on answering, so the switch can be turned back off. Windows decides
/// where a click goes before this program hears of it, so the window watches where the mouse is and
/// lets clicks through everywhere but the strip and the bar.</para>
/// <para><strong>Beside VRChat's menu.</strong> When VRChat's window is there, it sits in the free
/// strip to the right of VRChat's Esc menu, under its top-right column, as wide as that strip allows and
/// the whole way down to VRChat's bottom edge: the panel is laid out at its own width, taller than its own
/// height where the strip is, and scaled, so it follows VRChat's window
/// when that is resized or moved (<see cref="VRChatHudLayout"/>). With no VRChat window, or a
/// minimised one, it is where it always was, down the screen's right-hand side. Looking at
/// VRChat's window is <c>VRChatWindow.cs</c>'s one question, asked four times a second only while
/// this window is up; the window never activates, moves or sends anything to VRChat's.</para>
/// <para><strong>The resize corner.</strong> In edit mode, while the window is neither locked nor
/// click-through, a corner at the bottom right drags the window up to twice its ordinary width and
/// height (<see cref="DesktopPanelSize"/>); the text keeps its size and the lists show more rows. The
/// ordinary size is the fit beside VRChat's menu, so a dragged size is a number of times that and
/// still follows VRChat's window; it replaces the automatic fit, which two quick clicks on the corner
/// bring back. It is saved in <see cref="DesktopOverlaySettings"/>.</para>
/// <para><strong>VRChat's colours.</strong> When the person using this PC has a colour palette
/// selected in VRChat and the client knows who they are from VRChat's log, the window is drawn in
/// the look of VRChat's own menu in that palette; with none it looks as it always did. The palette
/// is one value in the Windows registry, read when the window opens and every two seconds while it
/// is up (<see cref="VRChatPaletteRegistry"/>), so a palette changed in VRChat shows here a moment
/// later. Nothing is written back and nothing is sent. Only this window is drawn so: the headset
/// panel is built from the same screens and never asks for it. While the client does not know who
/// the moderator is yet and VRChat is up, the window stays hidden and looks again, until the palette
/// is found or <see cref="OverlayOpening.LongestWait"/> has passed, so it does not open in the usual
/// look and change; the escape bubble never waits (<see cref="OverlayOpening"/>).</para>
/// </remarks>
internal sealed class DesktopOverlayWindow : Window, IOverlayPresenter
{
    /// <summary>Wide enough for a roster row's name, rank and flags without trimming any of them.</summary>
    private const double PanelWidth = VRChatHudLayout.OverlayDesignWidth;

    private const double PanelHeight = VRChatHudLayout.OverlayDesignHeight;

    /// <summary>How far in from the screen's edge it sits.</summary>
    private const int EdgeMargin = 32;

    private readonly ContentControl _panel = new();

    // What the mouse is on and lights up, by the same rule the headset's laser lights things by.
    private readonly Lit _lit = new();
    private OverlayTarget? _litTarget;
    private Point? _mouse;

    // The bar under the panel in edit mode: the lock and the hand, drawn again whenever either changes. It
    // is in the same window as the panel, so it is on the screen wherever the panel is.
    private readonly Border _barHost = new() { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent };
    private readonly Lit _barLit = new();
    private OverlayTarget? _barLitTarget;

    // The title strip of the tree drawn now, for the click-through region.
    private Control? _strip;

    // What the tree was last drawn with, so a different look, opacity or corner draws it again.
    private (OverlayLook Look, OverlayView.WindowFrame Frame)? _drawnWith;

    private OverlayScreen? _drawn;

    // Which page and person the body drawn now is, so a redraw of the same one keeps where its own scroll
    // (the person card's) stood instead of going back to the top.
    private (OverlayPage Page, string? Person, bool Idle)? _bodyKey;

    // Where the Instance list and the Audit Log are scrolled to, in pixels of the panel's layout, and the
    // list each belongs to: another list goes back to its top. Moved by the wheel and the keys, a glide
    // at a time (_glideWatch).
    private readonly ListScroll _rosterScroll = new();
    private readonly ListScroll _eventScroll = new();
    private int _rosterListId;
    private int _eventListId;

    private readonly DispatcherTimer _glideWatch = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private long _glideStamp;

    private DesktopOverlaySettings _settings = DesktopOverlaySettings.Default;

    // How the panel is drawn now: the headset's look, which is what this window always had, until
    // a palette has been read from VRChat's settings.
    private OverlayLook _look = OverlayLook.Headset;
    private OverlayColours? _colours;
    private readonly Border _frame = new();

    // The resize corner at the panel's bottom right, and the drag that is under way on it: where the pointer
    // was on the screen when it began and how big the window was then.
    private readonly Border _corner = ResizeCorner.Create();
    private (PixelPoint Pointer, double Width, double Height)? _resizing;

    // How big the window is at its ordinary size (the fit beside VRChat's menu, or the panel's own), and how
    // many times that the window was dragged to, in width and in height (DesktopPanelSize).
    private double _ordinaryWidth = PanelWidth;
    private double _ordinaryHeight = PanelHeight;
    private double _wide = 1;
    private double _tall = 1;

    // While the window is up: VRChat's selected palette looked at every two seconds, off the UI
    // thread, since it is a read from Windows. At most one look is out at a time.
    private readonly DispatcherTimer _paletteWatch = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _lookingAtPalette;
    private bool _paletteFailing;

    // Between a shortcut press that found no palette because VRChat's user id is not known yet and
    // the window coming up (OverlayOpening): the window is hidden, and this looks at the palette
    // again every few hundred milliseconds, off the UI thread, for at most OverlayOpening.LongestWait.
    // A pressed-again shortcut, a Dismiss or a settings change ends it; the count is what tells a
    // look that finishes late that its wait is already over.
    private readonly DispatcherTimer _openingWatch = new() { Interval = OverlayOpening.LookEvery };
    private bool _waitingToOpen;
    private bool _lookingWhileWaiting;
    private int _waitCount;
    private long _waitStartedAt;

    // The Dictate button in the title strip: there only while AI Chat is on. What it shows comes from
    // the same card the headset's button is drawn from; the picture moves a step every 150 ms
    // while it has something to show (listening, writing, or getting the speech model).
    private readonly DispatcherTimer _dictateMotion = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private ButtonScreen _dictate = ButtonScreen.Shown;
    private int _dictateFrame;

    private bool _editMode;

    /// <summary>
    /// Whether the bar under the panel shows the lock and the hand. Off by default and set from the window's
    /// Edit mode switch, as for the headset panels; what they were left at still holds.
    /// </summary>
    public bool EditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            _barHost.IsVisible = value;
            PaintCorner();
        }
    }

    // While click-through is on: where the mouse is, looked at often enough that reaching for the
    // strip or the bar finds it answering, and whether clicks are going through right now.
    private readonly DispatcherTimer _throughWatch = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _passingClicks;

    // While the window is up: VRChat's window looked at four times a second, so the panel can sit
    // beside VRChat's own menu and follow it when it moves or is resized.
    private readonly DispatcherTimer _followWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly LayoutTransformControl _scaler = new();
    private nint _vrchat;
    private (bool Usable, int Left, int Top, int Width, int Height)? _lastVRChat;
    private bool _scaled;
    private double _fittedScaling = 1;
    private int _scalingRetries;

    /// <summary>
    /// The moderator's own VRChat user id as the client's log reading last said, or null while it
    /// is not known. The palette is looked for under this id and no other.
    /// </summary>
    public Func<string?>? LocalUserId { get; set; }

    /// <summary>How the window is drawn now, which the chat window beside it is drawn in as well.</summary>
    public OverlayLook Look => _look;

    /// <summary>The window changed to another look (<see cref="Look"/>).</summary>
    public event Action? LookChanged;

    /// <summary>A click on the panel, as the target under it. Null is a click on nothing.</summary>
    public event Action<OverlayTarget?>? PanelTapped;

    /// <summary>
    /// The lock or the hand on the bar under the panel, pressed, or the resize corner let go: the settings as they
    /// should now be.
    /// </summary>
    public event Action<DesktopOverlaySettings>? SwitchPressed;

    /// <summary>The Dictate button on the title strip, pressed.</summary>
    public event Action? DictatePressed;

    /// <summary>The name searched for, as typed here while the Name filter is open, with its list.</summary>
    public event Action<OverlayPage, string>? NameTyped;

    /// <summary>A heads-up's words, as typed here while one is being written.</summary>
    public event Action<string>? HeadsUpTyped;

    /// <summary>
    /// The heads-up's words being typed, kept here for the same reason as <see cref="_typing"/>.
    /// Null while no heads-up is being written.
    /// </summary>
    private string? _typingHeadsUp;

    /// <summary>
    /// The name being typed, kept here rather than read back off the drawn screen: the drive
    /// loop draws four times a second, and two keys pressed between draws would otherwise each
    /// start from the same old text. Null while no Name filter is open.
    /// </summary>
    private string? _typing;

    /// <summary>The list <see cref="_typing"/> belongs to.</summary>
    private OverlayPage? _typingFor;

    public DesktopOverlayWindow()
    {
        Title = "Modbot";
        Icon = Brand.Icon();
        Width = PanelWidth;
        Height = PanelHeight;
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // The ground thins out with the opacity setting while the text stays solid, which is what
        // a panel over a game is for. Where a machine cannot do a transparent window it comes out
        // opaque and everything still works.
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = GroundBrush();

        // The panel is laid out at its own size and then drawn bigger or smaller by the scaler, so
        // layout, scrolling and clicking all work at any size (the transform is undone for a click).
        // The panel holds the shared frame and title strip (OverlayView.BuildFramed); the bar with the lock
        // and the hand sits under it, docked first so the panel's shadow is drawn behind it.
        _corner.Margin = new Thickness(0, 0, 4, 4);
        _corner.PointerPressed += CornerPressed;
        _corner.PointerMoved += CornerMoved;
        _corner.PointerReleased += CornerReleased;

        DockPanel.SetDock(_barHost, Dock.Bottom);
        _barHost.ZIndex = 1;
        _barHost.PointerPressed += OnBarPressed;
        _barHost.PointerMoved += OnBarMoved;
        _barHost.PointerExited += (_, _) => LightBar(null);
        PaintBar();

        _frame.Child = new Grid { Children = { new DockPanel { Children = { _barHost, _panel } }, _corner } };
        _scaler.Child = _frame;
        Content = _scaler;

        _panel.PointerPressed += OnPanelPressed;
        _panel.PointerMoved += OnPanelMoved;
        _panel.PointerExited += (_, _) =>
        {
            _mouse = null;
            Light(null);
        };
        // The wheel glides the roster or the Audit Log, the two lists the panel itself scrolls: a notch
        // is three rows, and a touchpad's small steps are small. On the person card it is left to the
        // window's own scrolling, because taking it there would be a wheel that does nothing over a
        // card that is taller than the window.
        _panel.PointerWheelChanged += (_, e) =>
        {
            if (ShownScroll is null)
                return;

            GlideShown(-e.Delta.Y * WheelRows);
            e.Handled = true;
        };

        _glideWatch.Tick += (_, _) => GlideTick();

        KeyDown += OnKeyDown;
        TextInput += OnTextInput;

        _throughWatch.Tick += (_, _) => WatchTheMouse();
        _dictateMotion.Tick += (_, _) =>
        {
            _dictateFrame = (_dictateFrame + 1) % 8;
            DrawScreen();
        };
        Closed += (_, _) =>
        {
            _dictateMotion.Stop();
            _glideWatch.Stop();
        };
        _followWatch.Tick += (_, _) => FollowVRChatSafely();
        _paletteWatch.Tick += (_, _) => _ = WatchThePaletteAsync();
        _openingWatch.Tick += (_, _) => _ = LookWhileWaitingToOpenAsync();
    }

    /// <summary>
    /// Whether the window can be drawn with rounded corners: only where it can really be see-through.
    /// Where it cannot, the corners would show a square of whatever the window is cleared to.
    /// </summary>
    private bool CanRoundTheCorners => ActualTransparencyLevel == WindowTransparencyLevel.Transparent;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ActualTransparencyLevelProperty)
            PaintGround();
    }

    private bool _followFailing;

    /// <summary>
    /// One tick of the follow watch. A failure from a Windows call or from moving the window is
    /// logged once and the watch carries on: following VRChat is never worth taking the client down.
    /// </summary>
    private void FollowVRChatSafely()
    {
        try
        {
            FollowVRChat();
            _followFailing = false;
        }
        catch (Exception ex)
        {
            if (!_followFailing)
                Serilog.Log.Warning(ex, "The desktop overlay could not follow VRChat's window ({Reason}); the client carries on", ex.Message);

            _followFailing = true;
        }
    }

    /// <summary>The list whose Name filter is open on the screen drawn now, or null.</summary>
    private OverlayPage? TypingFor
        => _drawn is { ShownFilters.Open: FilterPart.Name } drawn ? drawn.Page : null;

    /// <summary>Whether a heads-up is being written on the screen drawn now.</summary>
    private bool WritingHeadsUp => _drawn is { Draft: { Sending: false }, Page: OverlayPage.Instance };

    /// <summary>
    /// Letters go into a heads-up's words while one is being written, into the name while its
    /// filter is open, and are the list's keys otherwise.
    /// </summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (WritingHeadsUp && !string.IsNullOrEmpty(e.Text))
        {
            var words = new string(e.Text.Where(c => !char.IsControl(c)).ToArray());
            if (words.Length == 0)
                return;

            var longer = (_typingHeadsUp ?? string.Empty) + words;
            _typingHeadsUp = longer.Length > HeadsUpRules.MaxTextLength ? longer[..HeadsUpRules.MaxTextLength] : longer;

            HeadsUpTyped?.Invoke(_typingHeadsUp);
            e.Handled = true;
            return;
        }

        if (TypingFor is not { } list || string.IsNullOrEmpty(e.Text))
            return;

        // Control characters are keys, not text; Backspace and Enter are handled as keys.
        var typed = new string(e.Text.Where(c => !char.IsControl(c)).ToArray());
        if (typed.Length == 0)
            return;

        var next = (_typing ?? string.Empty) + typed;
        _typing = next.Length > ListFilters.LongestName ? next[..ListFilters.LongestName] : next;

        NameTyped?.Invoke(list, _typing);
        e.Handled = true;
    }

    /// <summary>
    /// The window's ground, and the panel drawn again when what it is framed with has changed: the look, the
    /// opacity (VRChat's look), or whether the corners can be rounded.
    /// </summary>
    private void PaintGround()
    {
        Background = GroundBrush();

        // One width for the panel on every tab and in every state: the window's own, less the room the
        // shadow falls into. Not left to what happens to be on the screen.
        PaintFrameWidth();
        _frame.HorizontalAlignment = HorizontalAlignment.Left;

        // Where the window can be see-through the panel is drawn a little in from the window's edge (VRChat's
        // look), with a soft shadow falling into the room that leaves; where it cannot, the shadow would be a
        // dark square, so it has neither.
        _frame.Margin = _look.VRChat is not null && CanRoundTheCorners ? new Thickness(VRChatLook.ShadowRoom) : default;

        PaintCorner();
        PaintBar();

        if (_drawnWith != (_look, WindowOptions))
            DrawScreen();
    }

    /// <summary>How the shared frame is asked for: the opacity and the rounded corners of the window as it is now.</summary>
    private OverlayView.WindowFrame WindowOptions => new(_settings.Alpha, CanRoundTheCorners);

    /// <summary>
    /// The panel's width: the design width times how many times wider than ordinary the window was
    /// dragged to, less the room the shadow falls into. The panel's tree is laid out at this width
    /// whatever the scaler does to it, which is why the ordinary width is the design width.
    /// </summary>
    private void PaintFrameWidth()
        => _frame.Width = (PanelWidth * _wide) - (_look.VRChat is not null && CanRoundTheCorners ? 2 * VRChatLook.ShadowRoom : 0);

    /// <summary>The resize corner: there in edit mode while the window is neither locked nor click-through, as on the headset's panels.</summary>
    private void PaintCorner()
    {
        _corner.IsVisible = _editMode && !_settings.Locked && !_settings.ClickThrough;
        ResizeCorner.Ink(_corner, _look.VRChat?.Icon ?? Ui.T.TextDimBrush);
    }

    private void CornerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_corner).Properties.IsLeftButtonPressed)
            return;

        e.Handled = true;

        // Two quick clicks put the automatic fit back, wherever the panel was dragged to.
        if (e.ClickCount >= 2)
        {
            _resizing = null;
            e.Pointer.Capture(null);
            UseTheFit();
            return;
        }

        _resizing = (this.PointToScreen(e.GetPosition(this)), Width, Height);
        e.Pointer.Capture(_corner);
    }

    private void CornerMoved(object? sender, PointerEventArgs e)
    {
        if (_resizing is not { } start)
            return;

        var now = this.PointToScreen(e.GetPosition(this));
        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;

        _wide = DesktopPanelSize.TimesOrdinary(start.Width + ((now.X - start.Pointer.X) / scaling), _ordinaryWidth);
        _tall = DesktopPanelSize.TimesOrdinary(start.Height + ((now.Y - start.Pointer.Y) / scaling), _ordinaryHeight);

        PaintFrameWidth();
        Width = _ordinaryWidth * _wide;
        Height = _ordinaryHeight * _tall;
        e.Handled = true;
    }

    private void CornerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizing is null)
            return;

        _resizing = null;
        e.Pointer.Capture(null);
        SaveTheSize();
    }

    /// <summary>The size the window now has, handed on to be saved with the rest of its settings.</summary>
    private void SaveTheSize()
    {
        var next = _settings with { Wide = _wide, Tall = _tall };

        if (next != _settings)
            SwitchPressed?.Invoke(next);
    }

    /// <summary>The ordinary size again, and the place the fit gives: what two quick clicks on the corner do.</summary>
    private void UseTheFit()
    {
        _wide = 1;
        _tall = 1;
        PaintFrameWidth();
        _lastVRChat = null;
        FollowVRChat();
        SaveTheSize();
    }

    /// <summary>A cross on a 24-unit square, for the Close button's round icon.</summary>
    internal static Control CloseIcon(IBrush brush)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };

        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M6,6 L18,18 M18,6 L6,18"),
            Stroke = brush,
            StrokeThickness = 2.8,
            StrokeLineCap = PenLineCap.Round,
        });

        return canvas;
    }

    /// <summary>
    /// A screen from the drive loop. Redrawn only when it would look different, the same rule the
    /// headset panel uses and for the same reason: this shares a machine with a game.
    /// </summary>
    public bool Update(OverlayScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        if (_drawn is { } last && last.LooksTheSameAs(screen))
            return false;

        // A glide belongs to the list it was started on: another page lets it stop where it is, and
        // another list than the one before starts at its top.
        if (_drawn?.Page != screen.Page)
        {
            _rosterScroll.Touch();
            _eventScroll.Touch();
        }

        if (screen.RosterListId != _rosterListId)
        {
            _rosterListId = screen.RosterListId;
            _rosterScroll.Reset();
        }

        if (screen.EventListId != _eventListId)
        {
            _eventListId = screen.EventListId;
            _eventScroll.Reset();
        }

        _drawn = screen;
        FramesDrawn++;

        // A heads-up's words start from what it holds when it opens, and are let go when it
        // closes. Not read back on every draw, for the same reason as the name.
        _typingHeadsUp = screen.Draft is { } draft && screen.Page is OverlayPage.Instance
            ? _typingHeadsUp ?? draft.Text
            : null;

        // The name being typed starts from what the list already holds whenever its filter opens,
        // and is let go when it closes.
        if (TypingFor != _typingFor)
        {
            _typingFor = TypingFor;
            _typing = _typingFor is null ? null : screen.ShownFilters?.Name ?? string.Empty;
        }

        DrawScreen();

        return true;
    }

    /// <summary>
    /// Builds the panel's controls for the screen drawn now, in the look in use. Called when a
    /// different screen arrives and again when the look changes under the same one.
    /// </summary>
    private void DrawScreen()
    {
        if (_drawn is not { } screen)
            return;

        // The idle screen draws nothing at all in a headset, where the panel hangs in the world.
        // In a window there is a window either way, so it says so rather than going blank.
        // The two lists are drawn whole and moved by pixels, as the headset's are, with the moderator's own
        // row kept at the top of the Instance list; the person card is the window's own to scroll.
        var shown = ShownScroll;

        // The Dictate button is on the strip, from the card the controller last gave; the drive loop's own
        // screen (_drawn) stays as it sent it, so what looks the same is still told apart.
        var withDictate = screen with { Dictate = _dictate.Dictate is DictateState.Off ? null : _dictate with { Frame = _dictateFrame } };
        var options = WindowOptions;

        // A page with no list scrolled by pixels scrolls in the frame's own viewer, which a new tree
        // starts at the top: its place is read from the tree being replaced when it is the same body.
        Vector? kept = null;
        var bodyKey = (screen.Page, screen.Person?.SubjectId, screen.IsIdle);

        if (shown is null && _bodyKey == bodyKey && _panel.Content is Control old)
            kept = old.GetLogicalDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset;

        _bodyKey = bodyKey;

        _drawnWith = (_look, options);
        var built = OverlayView.BuildFramed(
            screen.IsIdle
                ? withDictate with { ShowIdleCard = true }
                : shown is null ? withDictate : withDictate with { ScrollOffset = shown.Offset, PinOwnRow = true },
            GroupIcon,
            _look,
            window: options);

        _strip = built.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => c.Name == OverlayView.TitleStripName);
        _panel.Content = built;

        // The list's room is only known once it is laid out; a list that has got shorter is held inside it.
        if (shown is not null && _panel.Content is Control tree)
        {
            _panel.UpdateLayout();
            shown.Clamp(OverlayView.MaxScroll(tree));
            OverlayView.SetScroll(tree, shown.Offset);
        }
        else if (kept is { } offset && built.GetLogicalDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
        {
            _panel.UpdateLayout();
            scroll.Offset = offset;
        }

        // A new tree is drawn unlit, and what was under the mouse may have moved away from it
        // (a scroll, new events): light what is under the mouse now.
        Relight();
    }

    /// <summary>How many rows one notch of the wheel moves a list by.</summary>
    private const double WheelRows = 3;

    /// <summary>The scroll of the list on the screen: the Instance list's or the Audit Log's; null on the person card and the idle card.</summary>
    private ListScroll? ShownScroll => _drawn is { IsIdle: false } shown
        ? shown.Page switch
        {
            OverlayPage.Instance => _rosterScroll,
            OverlayPage.Events => _eventScroll,
            _ => null,
        }
        : null;

    /// <summary>Sends the list on the screen <paramref name="rows"/> rows further down (up when negative), gliding there.</summary>
    private void GlideShown(double rows)
    {
        if (ShownScroll is not { } scroll || _panel.Content is not Control tree)
            return;

        scroll.GlideBy(rows * OverlayView.RowPitch(_look), OverlayView.MaxScroll(tree));
        StartGliding();
    }

    private void StartGliding()
    {
        if (_glideWatch.IsEnabled)
            return;

        _glideStamp = Stopwatch.GetTimestamp();
        _glideWatch.Start();
    }

    /// <summary>One step of a glide: the list moved on the tree already drawn, with no new tree built.</summary>
    private void GlideTick()
    {
        var now = Stopwatch.GetTimestamp();

        // A pause (the machine was busy) must not become one big jump.
        var seconds = Math.Min(Stopwatch.GetElapsedTime(_glideStamp, now).TotalSeconds, 0.1);
        _glideStamp = now;

        if (ShownScroll is not { Gliding: true } scroll || _panel.Content is not Control tree)
        {
            _glideWatch.Stop();
            return;
        }

        scroll.Glide(seconds);
        OverlayView.SetScroll(tree, scroll.Offset);

        if (!scroll.Gliding)
        {
            _glideWatch.Stop();
            Relight();
        }
    }

    /// <summary>
    /// One look at the palette selected in VRChat's settings, taken off the UI thread and acted on
    /// back on it. A failed look is logged once per run of failures and leaves the look as it is.
    /// </summary>
    private async Task WatchThePaletteAsync()
    {
        if (_lookingAtPalette || !IsVisible)
            return;

        _lookingAtPalette = true;

        try
        {
            var user = LocalUserId?.Invoke();
            var read = await Task.Run(() => ReadPalette(user));
            UsePalette(read);
        }
        catch (Exception ex)
        {
            NoteFailedLook(ex.GetType().Name);
        }
        finally
        {
            _lookingAtPalette = false;
        }
    }

    /// <summary>
    /// The same look taken on the spot, for the moment the shortcut is pressed, so the window never
    /// opens in the wrong colours. Gives back what the look found, for <see cref="OverlayOpening"/>.
    /// </summary>
    private PaletteRead LookAtPaletteNow(string? user)
    {
        try
        {
            var read = ReadPalette(user);
            UsePalette(read);
            return read;
        }
        catch (Exception ex)
        {
            NoteFailedLook(ex.GetType().Name);
            return new PaletteRead(null, ex.GetType().Name);
        }
    }

    /// <summary>Whether the client knows who the moderator is, which is what a look for the palette needs. Not a check of the id's shape.</summary>
    private static bool UserIsKnown(string? user) => !string.IsNullOrEmpty(user);

    /// <summary>Whether VRChat's window is there. With it gone there is no log being written to learn an id from.</summary>
    private bool VRChatIsUp() => VRChatWindow.Look(_vrchat).Window.Found;

    /// <summary>Ends a wait to open, if there is one. Looks that are still out find their wait over.</summary>
    private void StopWaitingToOpen()
    {
        if (!_waitingToOpen)
            return;

        _waitingToOpen = false;
        _waitCount++;
        _openingWatch.Stop();
    }

    /// <summary>
    /// One tick of the wait to open: the palette looked at again off the UI thread, and the window
    /// brought up when it is found or the wait has run out. Does nothing once the wait is over, so a
    /// press that cancelled it, or a late look, cannot bring the window up.
    /// </summary>
    private async Task LookWhileWaitingToOpenAsync()
    {
        if (!_waitingToOpen)
            return;

        var wait = _waitCount;
        var waited = Stopwatch.GetElapsedTime(_waitStartedAt);

        // The limit holds even while a look is still out, so a slow read cannot keep the window hidden.
        if (OverlayOpening.IsOver(waited))
        {
            OpenAfterWaiting();
            return;
        }

        if (_lookingWhileWaiting)
            return;

        _lookingWhileWaiting = true;
        var showNow = false;

        try
        {
            var user = LocalUserId?.Invoke();
            var read = UserIsKnown(user) ? await Task.Run(() => ReadPalette(user)) : new PaletteRead(null);

            if (_waitingToOpen && wait == _waitCount)
            {
                UsePalette(read);

                showNow = OverlayOpening.Next(
                    read.Palette is not null,
                    UserIsKnown(user),
                    VRChatIsUp(),
                    Stopwatch.GetElapsedTime(_waitStartedAt)) == OverlayOpeningStep.ShowNow;
            }
        }
        catch (Exception ex)
        {
            // A look that cannot be made is not worth keeping the window back for.
            NoteFailedLook(ex.GetType().Name);
            showNow = true;
        }
        finally
        {
            _lookingWhileWaiting = false;
        }

        if (showNow && _waitingToOpen && wait == _waitCount)
            OpenAfterWaiting();
    }

    /// <summary>
    /// The wait is over: the window comes up. From a timer tick, so a failure is logged once and
    /// the client carries on rather than losing it as an unobserved task error.
    /// </summary>
    private void OpenAfterWaiting()
    {
        StopWaitingToOpen();

        try
        {
            Open();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "The desktop overlay could not come up after waiting for VRChat's palette ({Reason}); the client carries on", ex.Message);
        }
    }

    private static PaletteRead ReadPalette(string? userId)
        => OperatingSystem.IsWindows() ? VRChatPaletteRegistry.Read(userId) : new PaletteRead(null);

    private void UsePalette(PaletteRead read)
    {
        if (read.Problem is { } problem)
        {
            NoteFailedLook(problem);
            return;
        }

        _paletteFailing = false;
        UseColours(OverlayColours.From(read.Palette));
    }

    private void NoteFailedLook(string reason)
    {
        if (!_paletteFailing)
            Serilog.Log.Warning("The desktop overlay could not read VRChat's selected colour palette ({Reason}); it keeps the look it has", reason);

        _paletteFailing = true;
    }

    /// <summary>Switches the whole window to a look, when it is not the one it is in.</summary>
    internal void UseColours(OverlayColours? colours)
    {
        if (colours == _colours)
            return;

        _colours = colours;
        _look = colours is null ? OverlayLook.Headset : OverlayLook.FromColours(colours);

        // The ground, the bar and the panel are all drawn again for the look.
        PaintGround();
        LookChanged?.Invoke();
    }

    /// <summary>The screen last drawn, or null before the first. For the test remote's <c>state</c>.</summary>
    public OverlayScreen? Showing => _drawn;

    /// <summary>
    /// The picture addresses the screen showing may ask the picture store for, so the store keeps those
    /// pictures while they are on the screen and lets the rest go. Empty while nothing is drawn.
    /// </summary>
    public IReadOnlyCollection<string> PictureAddresses => _drawn?.PictureAddresses() ?? [];

    /// <summary>
    /// Whether the roster's first row is the moderator's own, held at the top in a banner while the rest
    /// scrolls: while this window is up in VRChat's look.
    /// </summary>
    public bool PinsModeratorRow => IsVisible && _look.VRChat is not null;

    private bool _redrawQueued;

    /// <summary>
    /// A picture has arrived in the picture store: the panel is drawn again with it. Arrivals that come
    /// together are one redraw, and nothing is drawn while the window is not up.
    /// </summary>
    public void PictureArrived()
    {
        if (_redrawQueued || !IsVisible || _drawn is null)
            return;

        _redrawQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _redrawQueued = false;

            if (IsVisible)
                DrawScreen();
        });
    }

    /// <summary>How many times a different screen has been drawn.</summary>
    public int FramesDrawn { get; private set; }

    /// <summary>
    /// The group's picture for an address, from the companion's own cache, or null while there is
    /// none.
    /// </summary>
    /// <remarks>
    /// The same cache the window's own server cards draw from. This window fetches nothing and is
    /// told nothing by a server; a picture that has not arrived leaves the group's name standing
    /// on its own.
    /// </remarks>
    public Func<string?, IImage?>? GroupIcon { get; set; }

    /// <summary>
    /// The drive loop does not decide whether this window is up; the moderator's shortcut does.
    /// Both are on the presenter for the headset's sake, where showing the panel and drawing into
    /// it are different things.
    /// </summary>
    void IOverlayPresenter.Show()
    {
    }

    void IOverlayPresenter.Hide()
    {
    }

    /// <summary>The opacity, the lock and the hand, and whether the window may be up at all.</summary>
    public void Apply(DesktopOverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;

        // A size the window did not just make itself (the corner being dragged hands its own on): lay the
        // window out again at the size saved.
        var wide = DesktopPanelSize.Clamp(settings.Wide);
        var tall = DesktopPanelSize.Clamp(settings.Tall);
        var resized = wide != _wide || tall != _tall;
        _wide = wide;
        _tall = tall;

        PaintGround();
        WatchTheMouseWhileNeeded();

        if (resized && IsVisible)
        {
            _lastVRChat = null;
            FollowVRChat();
        }

        if (!settings.On)
            Dismiss();
    }

    /// <summary>
    /// Never takes the keyboard. A button that held it after a click would answer the space bar,
    /// so a space typed into a heads-up or a name would press Dictate or Close instead of landing in
    /// the words.
    /// </summary>
    internal static Button SwitchButton() => new()
    {
        Focusable = false,
        Width = Ui.T.Density.ControlHeight,
        Height = Ui.T.Density.ControlHeight,
        Padding = new Thickness(6),
        CornerRadius = new CornerRadius(Ui.T.Density.Radius),
        BorderThickness = new Thickness(Ui.T.Density.Hairline),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// What the Dictate button on the strip shows, from the card the controller last gave: off hides
    /// it, and the picture moves a step each 150 ms while the card says it has something moving.
    /// </summary>
    public void SetDictate(ButtonScreen card)
    {
        if (card == _dictate)
            return;

        _dictate = card;

        if (_dictate.DictateMoves)
        {
            _dictateMotion.Start();
        }
        else
        {
            _dictateMotion.Stop();
            _dictateFrame = 0;
        }

        DrawScreen();
    }

    /// <summary>The bar under the panel, drawn as the lock and the hand stand (the headset's bar, in the same look in every palette).</summary>
    private void PaintBar()
    {
        _barHost.Child = PanelFrame.EditBar(_settings.Locked, _settings.ClickThrough);
        _barLitTarget = null;
    }

    private void OnBarMoved(object? sender, PointerEventArgs e)
    {
        if (_barHost.Child is not Visual root)
            return;

        var under = OverlayTargets.At(root, e.GetPosition(root));
        LightBar(Lit.Lights(under, editMode: true) ? under : null);
    }

    private void LightBar(OverlayTarget? target)
    {
        if (target == _barLitTarget || _barHost.Child is not Visual root)
            return;

        _barLitTarget = target;
        _barLit.Show(root, target, DesignTokens.Vr);
    }

    /// <summary>A press on the bar: the lock and the hand change the settings; anywhere else on it moves the window, unless it is locked.</summary>
    private void OnBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_barHost.Child is not Visual root || !e.GetCurrentPoint(_barHost).Properties.IsLeftButtonPressed)
            return;

        switch (OverlayTargets.At(root, e.GetPosition(root)))
        {
            case OverlayTarget.Lock:
                SwitchPressed?.Invoke(_settings with { Locked = !_settings.Locked });
                break;
            case OverlayTarget.ClickThrough:
                SwitchPressed?.Invoke(_settings with { ClickThrough = !_settings.ClickThrough });
                break;
            default:
                if (!_settings.Locked)
                    BeginMoveDrag(e);

                break;
        }

        e.Handled = true;
    }

    /// <summary>Starts or stops watching the mouse, and lets clicks through again only when it should.</summary>
    private void WatchTheMouseWhileNeeded()
    {
        if (_settings.ClickThrough && IsVisible && OperatingSystem.IsWindows())
        {
            _throughWatch.Start();
            WatchTheMouse();
            return;
        }

        _throughWatch.Stop();
        PassClicks(false);
    }

    /// <summary>
    /// Clicks go through everywhere but the title strip and the bar under the panel (while it shows): over
    /// those the window takes them, so the hand can be pressed again, and anywhere else the game gets them.
    /// </summary>
    private void WatchTheMouse()
    {
        if (!GetCursorPos(out var mouse))
            return;

        PassClicks(!(Over(_strip, mouse) || Over(_barHost, mouse)));

        static bool Over(Control? part, MousePoint at)
        {
            if (part is not { IsVisible: true } || part.GetVisualRoot() is null)
                return false;

            var topLeft = part.PointToScreen(new Point(0, 0));
            var bottomRight = part.PointToScreen(new Point(part.Bounds.Width, part.Bounds.Height));
            return at.X >= topLeft.X && at.X < bottomRight.X && at.Y >= topLeft.Y && at.Y < bottomRight.Y;
        }
    }

    private void PassClicks(bool pass)
    {
        if (_passingClicks == pass || !OperatingSystem.IsWindows())
            return;

        if (TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero)
            return;

        try
        {
            var style = (long)GetWindowLongPtrW(handle, GwlExStyle);
            style = pass ? style | WsExTransparent : style & ~WsExTransparent;
            SetWindowLongPtrW(handle, GwlExStyle, (IntPtr)style);
            _passingClicks = pass;
        }
        catch (EntryPointNotFoundException)
        {
            // A Windows without the 64-bit entry points: the panel keeps its clicks.
        }
    }

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;

    [StructLayout(LayoutKind.Sequential)]
    private struct MousePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out MousePoint point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);

    /// <summary>
    /// The shortcut: the window goes by the rule on the settings record, which is where it is
    /// tested.
    /// </summary>
    public void Press()
    {
        // Hidden and waiting for the palette is the window on its way up: the shortcut pressed
        // now is the one that takes it back, not a second wait.
        if (_waitingToOpen)
            Dismiss();
        else if (_settings.NextShowing(IsVisible))
            Summon();
        else
            Dismiss();
    }

    /// <summary>
    /// Which visual the window is placed beside, so it lands on the monitor Modbot's own window is
    /// on. Null puts it on the main screen.
    /// </summary>
    public Visual? PlaceNear { get; set; }

    /// <summary>
    /// Brings it up over the game and gives it the keyboard so the moderator can type.
    /// </summary>
    /// <remarks>
    /// Windows normally refuses to let a program that is not in front take the front. It allows it
    /// for a program handling a keyboard shortcut, which is exactly what asked — and when it
    /// refuses anyway the window is still on top and still readable, one click from the keyboard.
    /// </remarks>
    public void Summon()
    {
        // Already on its way up, waiting for the palette: not a second wait.
        if (_waitingToOpen)
            return;

        // The palette first, so the window opens in its colours rather than changing a moment later.
        var user = LocalUserId?.Invoke();
        var read = LookAtPaletteNow(user);

        // Nothing found because the client does not know who the moderator is yet (it learns that
        // from VRChat's log, a moment after it starts or after VRChat does): stay hidden and look
        // again, for no longer than OverlayOpening.LongestWait, rather than opening in the usual
        // look and changing a moment later. A window that is already up has nothing to wait for.
        if (!IsVisible
            && OverlayOpening.Next(read.Palette is not null, UserIsKnown(user), VRChatIsUp(), TimeSpan.Zero) == OverlayOpeningStep.WaitAndLookAgain)
        {
            _waitingToOpen = true;
            _waitCount++;
            _waitStartedAt = Stopwatch.GetTimestamp();
            _openingWatch.Start();
            return;
        }

        Open();
    }

    /// <summary>Puts the window up over the game with the keyboard, the look being settled.</summary>
    private void Open()
    {
        _lastVRChat = null;
        _scalingRetries = 0;
        FollowVRChat();
        Show();
        Activate();
        Focus();

        // Once there is a real window on a real screen, once more: a screen with a different
        // scale than the one the window was made on can change what a size in the panel's own
        // units comes out as.
        _lastVRChat = null;
        FollowVRChat();
        _followWatch.Start();
        _paletteWatch.Start();
        WatchTheMouseWhileNeeded();
    }

    public void Dismiss()
    {
        // Dismissed while waiting for the palette: the window never comes up.
        StopWaitingToOpen();

        _followWatch.Stop();
        _paletteWatch.Stop();

        if (IsVisible)
            Hide();

        WatchTheMouseWhileNeeded();
    }

    /// <summary>
    /// Down the right-hand side, where VRChat's own menus, nameplates and HUD are not. On the
    /// screen the client's window is on, so a moderator with two monitors gets it on the one they
    /// put Modbot on, and on the main screen when that cannot be worked out.
    /// </summary>
    private void Place(Visual? near)
    {
        var screen = (near is not null ? Screens.ScreenFromVisual(near) : null)
            ?? Screens.Primary
            ?? Screens.All.FirstOrDefault();

        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var width = (int)(Width * scaling);
        var height = (int)(Height * scaling);
        var margin = (int)(EdgeMargin * scaling);

        Position = new PixelPoint(
            area.X + Math.Max(0, area.Width - width - margin),
            area.Y + Math.Max(0, (area.Height - height) / 2));
    }

    /// <summary>
    /// Looks at VRChat's window and, when it has moved or been resized since the last look, puts
    /// the panel beside VRChat's own menu at the size that fits (<see cref="VRChatHudLayout"/>).
    /// With no VRChat window, or a minimised one, or one too small to place anything in, it is the
    /// plain placement down the screen's right-hand side, at the panel's own size.
    /// </summary>
    /// <remarks>
    /// It does nothing when VRChat's window is where it was, so a moderator who drags the panel
    /// somewhere keeps it there until VRChat moves. It only moves and sizes this window; it never
    /// activates it, and it asks nothing of VRChat's.
    /// </remarks>
    private void FollowVRChat()
    {
        // A drag on the resize corner is under way: the size is the pointer's until it lets go.
        if (_resizing is not null)
            return;

        var look = VRChatWindow.Look(_vrchat);
        _vrchat = look.Handle;

        var window = look.Window;
        var usable = window.HasPicture;
        var seen = (usable, usable ? look.Left : 0, usable ? look.Top : 0, usable ? window.Width : 0, usable ? window.Height : 0);

        // A screen whose scale is not what the window was sized for is put right again, a few
        // times at most, so a window that never agrees cannot be rewritten four times a second.
        if (_scaled && IsVisible && Math.Abs(RenderScaling - _fittedScaling) > 0.001 && _scalingRetries < 3)
        {
            _scalingRetries++;
            _lastVRChat = null;
        }

        if (_lastVRChat == seen)
            return;

        _lastVRChat = seen;

        if (usable && FitBesideTheMenu(look))
            return;

        if (_scaled)
        {
            _scaler.LayoutTransform = null;
            _scaled = false;
        }

        _ordinaryWidth = PanelWidth;
        _ordinaryHeight = PanelHeight;
        Width = _ordinaryWidth * _wide;
        Height = _ordinaryHeight * _tall;
        PaintFrameWidth();
        Place(PlaceNear);
    }

    /// <summary>
    /// Sizes and places the window for VRChat's picture as it is now. False when nothing fits, and
    /// then the window is left as it was.
    /// </summary>
    private bool FitBesideTheMenu(GameWindowLook look)
    {
        if (VRChatHudLayout.OverlayFor(look.Window) is not { } fit)
            return false;

        // The client rectangle and Position are both in pixels of the desktop (this process is
        // made aware of each screen's scale). The window's own size is in the panel's units, which
        // are a screen's pixels over its scale, so the scale is the one of the screen it goes on.
        var left = look.Left + fit.X;
        var top = look.Top + fit.Y;
        var centre = new PixelPoint(left + (fit.Width / 2), top + (fit.Height / 2));
        var screen = Screens.ScreenFromPoint(centre) ?? Screens.Primary;
        var scaling = screen is { Scaling: > 0 } ? screen.Scaling : 1;

        var factor = fit.Scale / scaling;
        _scaler.LayoutTransform = new ScaleTransform(factor, factor);
        _ordinaryWidth = fit.Width / scaling;
        _ordinaryHeight = fit.Height / scaling;
        Width = _ordinaryWidth * _wide;
        Height = _ordinaryHeight * _tall;
        PaintFrameWidth();

        // The fit is the whole free height, and a panel dragged bigger grows from the fit's top left. Either is
        // moved back onto the screen's work area where it would leave it, and made no taller than that.
        if (screen?.WorkingArea is { } box)
        {
            Height = Math.Min(Height, box.Height / scaling);
            left = Math.Max(box.X, Math.Min(left, box.Right - (int)(Width * scaling)));
            top = Math.Max(box.Y, Math.Min(top, box.Bottom - (int)(Height * scaling)));
        }

        Position = new PixelPoint(left, top);

        _fittedScaling = scaling;
        _scaled = true;
        return true;
    }

    private void OnPanelMoved(object? sender, PointerEventArgs e)
    {
        if (_panel.Content is not Visual root)
            return;

        _mouse = e.GetPosition(_panel);
        var under = OverlayTargets.At(root, e.GetPosition(root));
        Light(Lit.Lights(under, editMode: true) ? under : null);
    }

    /// <summary>
    /// Lights what is under the mouse's last spot on the tree just drawn, after it is laid out, or
    /// nothing when the mouse is off the panel.
    /// </summary>
    private void Relight()
    {
        if (_panel.Content is not Visual root)
            return;

        OverlayTarget? under = null;

        if (_mouse is { } at)
        {
            _panel.UpdateLayout();

            if (_panel.TranslatePoint(at, root) is { } inRoot)
                under = OverlayTargets.At(root, inRoot);
        }

        _litTarget = Lit.Lights(under, editMode: true) ? under : null;
        _lit.Show(root, _litTarget, _look.Tokens);
    }

    /// <summary>Lights the control the mouse is on, or none, and puts the one before it back.</summary>
    private void Light(OverlayTarget? target)
    {
        if (target == _litTarget)
            return;

        _litTarget = target;

        if (_panel.Content is Visual root)
            _lit.Show(root, target, _look.Tokens);
    }

    private void OnPanelPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_panel.Content is not Visual root)
            return;

        // The same lookup a controller's ray uses, on the same tagged controls, with a mouse
        // position instead of a point on a texture.
        var target = OverlayTargets.At(root, e.GetPosition(root));

        // The list's own arrow is this window's business, as it is the headset panel's: the list glides
        // back to its top.
        if (target is OverlayTarget.BackToTop { List: var list })
        {
            var scroll = list == OverlayPage.Events ? _eventScroll : _rosterScroll;

            if (_panel.Content is Control tree)
            {
                scroll.GlideBy(-(scroll.Goal ?? scroll.Offset), OverlayView.MaxScroll(tree));
                StartGliding();
            }

            e.Handled = true;
            return;
        }

        // Close and Dictate are on the title strip, and the window answers them itself.
        if (target is OverlayTarget.ClosePanel)
        {
            Dismiss();
            e.Handled = true;
            return;
        }

        if (target is OverlayTarget.DictateButton)
        {
            DictatePressed?.Invoke();
            e.Handled = true;
            return;
        }

        // Nothing under the press and it is on the strip: that is the way to move the window.
        if (target is null && OverlayView.OnStrip(e.Source as Visual) && !_settings.Locked
            && e.GetCurrentPoint(_panel).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
            e.Handled = true;
            return;
        }

        PanelTapped?.Invoke(target);
        e.Handled = true;
    }

    /// <summary>
    /// The roster's own keys, and deliberately not Escape.
    /// </summary>
    /// <remarks>
    /// <strong>Escape belongs to VRChat.</strong> It is how the game's own menu is opened, and a
    /// moderator pressing it while this window has the keyboard means the menu. The window used to
    /// close on it, which put the two in a fight the game could not win. The shortcut that opened
    /// the window closes it, and so does the Close button.
    /// </remarks>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // While a heads-up is being written, letters are letters too. Backspace takes the last
        // character off and Enter presses Place.
        if (WritingHeadsUp)
        {
            switch (e.Key)
            {
                case Key.Back:
                    if (_typingHeadsUp is { Length: > 0 } words)
                    {
                        var shorter = new System.Globalization.StringInfo(words);
                        _typingHeadsUp = shorter.LengthInTextElements > 1 ? shorter.SubstringByTextElements(0, shorter.LengthInTextElements - 1) : string.Empty;
                        HeadsUpTyped?.Invoke(_typingHeadsUp);
                    }

                    e.Handled = true;
                    return;
                case Key.Enter:
                    PanelTapped?.Invoke(new OverlayTarget.PlaceHeadsUp());
                    e.Handled = true;
                    return;
                case Key.J or Key.K or Key.Up or Key.Down:
                    return;
            }
        }

        // While a name is being typed, letters are letters: j and k go into it rather than
        // scrolling. Backspace takes the last character off, and Enter closes the filter.
        if (TypingFor is { } list)
        {
            switch (e.Key)
            {
                case Key.Back:
                    if (_typing is { Length: > 0 } typing)
                    {
                        var shorter = new System.Globalization.StringInfo(typing);
                        _typing = shorter.LengthInTextElements > 1 ? shorter.SubstringByTextElements(0, shorter.LengthInTextElements - 1) : string.Empty;
                        NameTyped?.Invoke(list, _typing);
                    }

                    e.Handled = true;
                    return;
                case Key.Enter:
                    PanelTapped?.Invoke(new OverlayTarget.Filter(list, FilterPart.Name));
                    e.Handled = true;
                    return;
                case Key.J or Key.K:
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.J or Key.Down:
                GlideShown(1);
                e.Handled = true;
                break;
            case Key.K or Key.Up:
                GlideShown(-1);
                e.Handled = true;
                break;
        }
    }

    private IBrush GroundBrush()
    {
        // In VRChat's look the panel inside paints its own ground, with rounded corners, and the
        // window behind it is clear.
        if (_look.VRChat is not null)
            return Brushes.Transparent;

        var ground = Ui.T.Palette.Background;
        return new SolidColorBrush(Color.FromArgb(_settings.Alpha, ground.R, ground.G, ground.B));
    }
}
