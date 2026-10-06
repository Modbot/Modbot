using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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
/// <para><strong>The lock and the hand.</strong> The strip carries the same two switches as the bar
/// under a headset panel. Locked, the strip no longer drags the window. Click-through, a click on
/// the panel goes to the game underneath instead; the strip itself goes on answering, so the
/// switch can be turned back off. Windows decides where a click goes before this program hears
/// of it, so the window watches where the mouse is and lets clicks through everywhere but the
/// strip.</para>
/// <para><strong>Beside VRChat's menu.</strong> When VRChat's window is there, it sits in the free
/// strip to the right of VRChat's Esc menu, under its top-right column, and is drawn as big as that
/// strip allows: the panel is laid out at its own size and scaled, so it follows VRChat's window
/// when that is resized or moved (<see cref="VRChatHudLayout"/>). With no VRChat window, or a
/// minimised one, it is where it always was, down the screen's right-hand side. Looking at
/// VRChat's window is <c>VRChatWindow.cs</c>'s one question, asked four times a second only while
/// this window is up; the window never activates, moves or sends anything to VRChat's.</para>
/// </remarks>
internal sealed class DesktopOverlayWindow : Window, IOverlayPresenter
{
    /// <summary>Wide enough for a roster row's name, rank and flags without trimming any of them.</summary>
    private const double PanelWidth = VRChatHudLayout.OverlayDesignWidth;

    private const double PanelHeight = VRChatHudLayout.OverlayDesignHeight;

    /// <summary>How far in from the screen's edge it sits.</summary>
    private const int EdgeMargin = 32;

    /// <summary>How big the group's icon is drawn in the strip along the top.</summary>
    private const double IconSize = 22;

    private readonly ContentControl _panel = new();
    private readonly Image _groupIcon = new() { Width = IconSize, Height = IconSize, Stretch = Stretch.UniformToFill };
    private readonly Border _groupIconFrame;
    private readonly TextBlock _groupName = Ui.Text(
        "", Ui.T.Density.TextBase, Ui.T.TextBrush, FontWeight.SemiBold, wrap: false);

    private OverlayScreen? _drawn;
    private DesktopOverlaySettings _settings = DesktopOverlaySettings.Default;

    // The strip, and its two switches, repainted whenever the settings change.
    private Control? _head;
    private readonly Button _lockButton = SwitchButton();
    private readonly Button _throughButton = SwitchButton();

    private bool _editMode;

    /// <summary>
    /// Whether the strip shows the lock and the hand. Off by default and set from the window's
    /// Edit mode switch, as for the headset panels; what they were left at still holds.
    /// </summary>
    public bool EditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            _lockButton.IsVisible = value;
            _throughButton.IsVisible = value;
        }
    }

    // While click-through is on: where the mouse is, looked at often enough that reaching for the
    // strip finds it answering, and whether clicks are going through right now.
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

    /// <summary>A click on the panel, as the target under it. Null is a click on nothing.</summary>
    public event Action<OverlayTarget?>? PanelTapped;

    /// <summary>The lock or the hand on the strip, pressed: the settings as they should now be.</summary>
    public event Action<DesktopOverlaySettings>? SwitchPressed;

    /// <summary>The wheel, or j and k, as whole roster rows.</summary>
    public event Action<int>? RosterScrolled;

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

        _groupIconFrame = new Border
        {
            Width = IconSize,
            Height = IconSize,
            CornerRadius = new CornerRadius(IconSize / 2),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Child = _groupIcon,
        };

        var body = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _panel,
        };

        var head = Head();
        _head = head;
        DockPanel.SetDock(head, Dock.Top);

        // The panel is laid out at its own size and then drawn bigger or smaller by the scaler, so
        // layout, scrolling and clicking all work at any size (the transform is undone for a click).
        _scaler.Child = new DockPanel { Children = { head, body } };
        Content = _scaler;

        _panel.PointerPressed += OnPanelPressed;
        // The wheel moves the roster, which is the list the panel itself pages through. On the
        // other two screens it is left to the window's own scrolling, because taking it there
        // would be a wheel that does nothing over a card that is taller than the window.
        _panel.PointerWheelChanged += (_, e) =>
        {
            if (_drawn is not { Page: OverlayPage.Instance })
                return;

            RosterScrolled?.Invoke(e.Delta.Y > 0 ? -1 : 1);
            e.Handled = true;
        };

        KeyDown += OnKeyDown;
        TextInput += OnTextInput;

        _throughWatch.Tick += (_, _) => WatchTheMouse();
        _followWatch.Tick += (_, _) => FollowVRChatSafely();
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
    /// The strip along the top: whose community this is, and the way to move the window.
    /// </summary>
    /// <remarks>
    /// <para>The group's icon and the group's name, because that is what a moderator knows their
    /// community by. It used to say the product's name and then the address of the machine the
    /// server runs on, which told them nothing they did not already know and nothing they wanted.
    /// The address is only what the name falls back to.</para>
    /// <para>Dragging the strip is why it exists — the window has no border to drag — so the drag
    /// starts on the strip itself and never on the Close button, which used to swallow the press
    /// that was meant to close the window.</para>
    /// </remarks>
    private Control Head()
    {
        _groupName.VerticalAlignment = VerticalAlignment.Center;

        var close = Ui.Button("Close");
        close.Click += (_, _) => Dismiss();

        _lockButton.Click += (_, _) => SwitchPressed?.Invoke(_settings with { Locked = !_settings.Locked });
        _throughButton.Click += (_, _) => SwitchPressed?.Invoke(_settings with { ClickThrough = !_settings.ClickThrough });
        PaintSwitches();
        _lockButton.IsVisible = _editMode;
        _throughButton.IsVisible = _editMode;

        var switches = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 8, 0),
            Children = { _lockButton, _throughButton },
        };

        var handle = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Background = Brushes.Transparent,
            Children = { _groupIconFrame, _groupName },
        };

        handle.PointerPressed += (_, e) =>
        {
            if (!_settings.Locked && e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(switches, Dock.Right);
        row.Children.Add(close);
        row.Children.Add(switches);
        row.Children.Add(handle);

        return new Border
        {
            Padding = new Thickness(12, 8),
            Background = Ui.T.Surface2Brush,
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, Ui.T.Density.Hairline),
            Child = row,
        };
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

        _groupName.Text = screen.GroupLabel ?? "Not in a group instance";
        _groupName.Foreground = screen.GroupLabel is null ? Ui.T.TextDimBrush : Ui.T.TextBrush;

        var picture = GroupIcon?.Invoke(screen.GroupIconUrl);
        _groupIcon.Source = picture;
        _groupIconFrame.IsVisible = picture is not null;

        // The idle screen draws nothing at all in a headset, where the panel hangs in the world.
        // In a window there is a window either way, so it says so rather than going blank.
        _panel.Content = screen.IsIdle
            ? OverlayView.Build(screen with { ShowIdleCard = true }, GroupIcon)
            : OverlayView.Build(screen, GroupIcon);

        return true;
    }

    /// <summary>The screen last drawn, or null before the first. For the test remote's <c>state</c>.</summary>
    public OverlayScreen? Showing => _drawn;

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
        Background = GroundBrush();
        PaintSwitches();
        WatchTheMouseWhileNeeded();

        if (!settings.On)
            Dismiss();
    }

    private static Button SwitchButton() => new()
    {
        Width = Ui.T.Density.ControlHeight,
        Height = Ui.T.Density.ControlHeight,
        Padding = new Thickness(6),
        CornerRadius = new CornerRadius(Ui.T.Density.Radius),
        BorderThickness = new Thickness(Ui.T.Density.Hairline),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>The two switches drawn as they stand: the accent colour while on.</summary>
    private void PaintSwitches()
    {
        Paint(_lockButton, _settings.Locked, on => PanelFrame.LockIcon(on, Foreground(on)));
        Paint(_throughButton, _settings.ClickThrough, on => PanelFrame.HandIcon(on, Foreground(on), Background(on)));

        static IBrush Foreground(bool on) => on ? Ui.T.AccentForegroundBrush : Ui.T.TextBrush;

        static IBrush Background(bool on) => on ? Ui.T.AccentBrush : Ui.T.Surface2Brush;

        static void Paint(Button button, bool on, Func<bool, Control> icon)
        {
            button.Background = Background(on);
            button.BorderBrush = on ? Ui.T.AccentBrush : Ui.T.Border2Brush;
            button.Content = new Viewbox { Child = icon(on) };
        }
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
    /// Clicks go through everywhere but the strip: over the strip the window takes them, so the
    /// hand can be pressed again, and anywhere else the game gets them.
    /// </summary>
    private void WatchTheMouse()
    {
        if (_head is null || !GetCursorPos(out var mouse))
            return;

        var topLeft = _head.PointToScreen(new Point(0, 0));
        var bottomRight = _head.PointToScreen(new Point(_head.Bounds.Width, _head.Bounds.Height));
        var overStrip = mouse.X >= topLeft.X && mouse.X < bottomRight.X && mouse.Y >= topLeft.Y && mouse.Y < bottomRight.Y;

        PassClicks(!overStrip);
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
        if (_settings.NextShowing(IsVisible))
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
        WatchTheMouseWhileNeeded();
    }

    public void Dismiss()
    {
        _followWatch.Stop();

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
            Width = PanelWidth;
            Height = PanelHeight;
            _scaled = false;
        }

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
        Position = new PixelPoint(left, top);
        Width = fit.Width / scaling;
        Height = fit.Height / scaling;

        _fittedScaling = scaling;
        _scaled = true;
        return true;
    }

    private void OnPanelPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_panel.Content is not Visual root)
            return;

        // The same lookup a controller's ray uses, on the same tagged controls, with a mouse
        // position instead of a point on a texture.
        PanelTapped?.Invoke(OverlayTargets.At(root, e.GetPosition(root)));
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
                RosterScrolled?.Invoke(1);
                e.Handled = true;
                break;
            case Key.K or Key.Up:
                RosterScrolled?.Invoke(-1);
                e.Handled = true;
                break;
        }
    }

    private IBrush GroundBrush()
    {
        var ground = Ui.T.Palette.Background;
        return new SolidColorBrush(Color.FromArgb(_settings.Alpha, ground.R, ground.G, ground.B));
    }
}
