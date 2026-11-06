using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Win32;
using Modbot.Companion.Presentation;
using Modbot.Overlay;
using Modbot.Overlay.Views;

namespace Modbot.Companion.App;

/// <summary>
/// The AI Chat conversation on a monitor: what was dictated and what came back, in a window beside
/// the desktop overlay.
/// </summary>
/// <remarks>
/// <para><strong>It is the headset chat panel's log, in a window.</strong> The words are drawn by
/// <see cref="ChatView"/>, the same code the headset panel uses, so the two cannot disagree about
/// what a conversation looks like.</para>
/// <para><strong>It never takes the keyboard from VRChat.</strong> It is shown without being
/// activated and a click on it is answered "do not activate", as the notification window is. There
/// is nothing to type into; the words come from Dictate.</para>
/// <para><strong>Its own strip.</strong> Grab it to move the window unless it is locked; the X at the
/// top right ends the conversation and puts the headset panel away too. In edit mode the strip also
/// has the lock and the hand, which are the desktop overlay's own two switches (one setting,
/// <see cref="DesktopOverlaySettings"/>), and a corner grip makes the window bigger, up to twice its
/// first size. With the hand on, clicks go through to the game everywhere but the strip and the grip.</para>
/// </remarks>
internal sealed class DesktopChatWindow : Window
{
    /// <summary>How far one click of the wheel scrolls the log, in layout pixels.</summary>
    private const double WheelStep = 48;

    private const double GripSize = 22;

    /// <summary>The gap kept between this window and the one it is placed beside, in screen pixels.</summary>
    private const int EdgeMargin = 12;

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;
    private const uint WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    private readonly Border _frame = new();
    private readonly Border _strip = new();
    private readonly ContentControl _log = new();
    private readonly Border _grip = new() { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.BottomRightCorner) };
    private readonly Button _lockButton = DesktopOverlayWindow.SwitchButton();
    private readonly Button _throughButton = DesktopOverlayWindow.SwitchButton();
    private readonly Button _closeButton = DesktopOverlayWindow.SwitchButton();
    private readonly StackPanel _switches = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Style> _hoverStyles = [];

    // While click-through is on: where the mouse is, looked at often enough that reaching for the
    // strip finds it answering.
    private readonly DispatcherTimer _throughWatch = new() { Interval = TimeSpan.FromMilliseconds(50) };

    // The three dots move a step every frame while an answer is waiting to start.
    private readonly DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(ChatView.FrameMilliseconds) };

    private ChatScreen _screen = ChatScreen.Empty;
    private DesktopOverlaySettings _settings = DesktopOverlaySettings.Default;
    private OverlayLook _look = OverlayLook.Headset;
    private bool _editMode;
    private bool _everDrawn;
    private bool _placed;
    private bool _styled;
    private bool _passingClicks;
    private int _drawnFrame;

    // Following the newest words: on until the log is scrolled up, on again at its end.
    private bool _follow = true;
    private double _offset;

    private DesktopChatSpot? _spot;
    private PixelPoint? _placedAt;
    private (PixelPoint Pointer, double Width, double Height)? _resizing;

    public DesktopChatWindow(DesktopChatSpot? spot)
    {
        Title = "Modbot";
        Icon = Brand.Icon();
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;

        // Shown without taking the front from the game.
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

        Width = DesktopChatSpot.ClampWidth(spot?.Width ?? DesktopChatSpot.DefaultWidth);
        Height = DesktopChatSpot.ClampHeight(spot?.Height ?? DesktopChatSpot.DefaultHeight);
        _spot = spot;

        _lockButton.Click += (_, _) => SwitchPressed?.Invoke(_settings with { Locked = !_settings.Locked });
        _throughButton.Click += (_, _) => SwitchPressed?.Invoke(_settings with { ClickThrough = !_settings.ClickThrough });
        _closeButton.Click += (_, _) => CloseTapped?.Invoke();
        _switches.Children.Add(_lockButton);
        _switches.Children.Add(_throughButton);

        var handle = new Border { Background = Brushes.Transparent };
        handle.PointerPressed += (_, e) =>
        {
            if (!_settings.Locked && e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        DockPanel.SetDock(_closeButton, Dock.Right);
        DockPanel.SetDock(_switches, Dock.Left);
        _strip.Child = new Grid
        {
            Children =
            {
                handle,
                new DockPanel { LastChildFill = false, Children = { _closeButton, _switches } },
            },
        };

        DockPanel.SetDock(_strip, Dock.Top);
        _frame.Child = new DockPanel { Children = { _strip, _log } };

        _grip.Width = _grip.Height = GripSize;
        _grip.HorizontalAlignment = HorizontalAlignment.Right;
        _grip.VerticalAlignment = VerticalAlignment.Bottom;
        _grip.PointerPressed += GripPressed;
        _grip.PointerMoved += GripMoved;
        _grip.PointerReleased += GripReleased;
        _grip.PointerCaptureLost += (_, _) => _resizing = null;

        Content = new Grid { Children = { _frame, _grip } };

        _log.PointerWheelChanged += (_, e) =>
        {
            if (_log.Content is not Control tree)
                return;

            _offset = ChatView.SetOffset(tree, _offset - (e.Delta.Y * WheelStep));
            _follow = _offset >= ChatView.MaxOffset(tree) - 1;
            e.Handled = true;
        };

        _throughWatch.Tick += (_, _) => WatchTheMouse();
        _typing.Tick += (_, _) =>
        {
            if (!_screen.IsTyping || TypingFrame() == _drawnFrame)
                return;

            Draw();
        };

        // Windows asks a window, before a click activates it, whether it may. Never.
        if (OperatingSystem.IsWindows())
            Win32Properties.AddWndProcHookCallback(this, KeepFocusOnTheGame);

        Opened += (_, _) =>
        {
            _styled = ApplyStyle();
            PaintGround();
        };
        PositionChanged += OnMoved;
        Paint();
    }

    /// <summary>Which window it is placed beside the first time it comes up when it has no saved spot, and which screen it lands on.</summary>
    public Window? Beside { get; set; }

    /// <summary>The X, pressed.</summary>
    public event Action? CloseTapped;

    /// <summary>The lock or the hand, pressed: the settings as they should now be.</summary>
    public event Action<DesktopOverlaySettings>? SwitchPressed;

    /// <summary>The window was moved or resized, and where it is now.</summary>
    public event Action<DesktopChatSpot>? SpotChanged;

    /// <summary>Whether the window is put away.</summary>
    public bool Hidden => !IsVisible;

    /// <summary>
    /// Whether the strip shows the lock and the hand, and the corner grip is there. Set from the
    /// Edit mode switch, as for the other panels.
    /// </summary>
    public bool EditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            PaintEdit();
        }
    }

    /// <summary>The opacity, the lock and the hand.</summary>
    public void Apply(DesktopOverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        Paint();
        WatchTheMouseWhileNeeded();
    }

    /// <summary>Draws the window in the look the desktop overlay is in.</summary>
    public void UseLook(OverlayLook look)
    {
        ArgumentNullException.ThrowIfNull(look);

        if (_look == look)
            return;

        _look = look;
        Paint();
        Draw();
    }

    /// <summary>Brings the window up, at the end of the conversation, without taking the keyboard.</summary>
    public void Show(ChatScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        _screen = screen;
        _follow = true;

        if (!IsVisible)
        {
            Place();
            Show();
            _typing.Start();
        }

        Draw();
        WatchTheMouseWhileNeeded();
    }

    /// <summary>Draws the conversation, when it would look different from what is drawn.</summary>
    public void Update(ChatScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        if (_everDrawn && _screen.LooksTheSameAs(screen))
            return;

        _screen = screen;
        Draw();
    }

    /// <summary>Puts the window away.</summary>
    public void Dismiss()
    {
        _typing.Stop();
        _throughWatch.Stop();
        PassClicks(false);

        if (IsVisible)
            Hide();
    }

    private int TypingFrame() => _screen.IsTyping ? (int)(Environment.TickCount64 / ChatView.FrameMilliseconds) % ChatView.Frames : 0;

    private void Draw()
    {
        _drawnFrame = TypingFrame();
        _everDrawn = true;

        var tree = ChatView.Build(_screen, _offset, _drawnFrame, _look);
        _log.Content = tree;

        if (!IsVisible)
            return;

        // Laid out now, so the end of the log is known and the newest words can be followed.
        _log.UpdateLayout();
        _offset = ChatView.SetOffset(tree, _follow ? ChatView.MaxOffset(tree) : _offset);
    }

    private bool CanRoundTheCorners => ActualTransparencyLevel == WindowTransparencyLevel.Transparent;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ActualTransparencyLevelProperty)
            PaintGround();
    }

    private void Paint()
    {
        PaintGround();
        PaintHoverStyle();
        PaintButtons();
        PaintEdit();
    }

    /// <summary>The ground and the strip, in the look in use: VRChat's panel and bar, or the plain thinned-out ground.</summary>
    private void PaintGround()
    {
        if (_look.VRChat is { } v)
        {
            var round = CanRoundTheCorners;
            _frame.Background = v.Panel(_settings.Alpha);
            _frame.BorderBrush = v.Edge;
            _frame.BorderThickness = new Thickness(VRChatLook.EdgeWidth);
            _frame.CornerRadius = round ? new CornerRadius(VRChatLook.FrameRadius) : default;

            var inner = round ? VRChatLook.FrameRadius - VRChatLook.EdgeWidth : 0;
            _strip.Background = v.Bar(_settings.Alpha);
            _strip.BorderThickness = default;
            _strip.CornerRadius = new CornerRadius(inner, inner, 0, 0);
            _strip.Padding = new Thickness(14, 8);
            return;
        }

        var ground = Ui.T.Palette.Background;
        _frame.Background = new SolidColorBrush(Color.FromArgb(_settings.Alpha, ground.R, ground.G, ground.B));
        _frame.BorderBrush = null;
        _frame.BorderThickness = default;
        _frame.CornerRadius = default;

        _strip.Background = Ui.T.Surface2Brush;
        _strip.BorderBrush = Ui.T.BorderBrush;
        _strip.BorderThickness = new Thickness(0, 0, 0, Ui.T.Density.Hairline);
        _strip.CornerRadius = default;
        _strip.Padding = new Thickness(12, 8);
    }

    /// <summary>The strip's buttons: the lock and the hand as the desktop overlay's own, and the X.</summary>
    private void PaintButtons()
    {
        var v = _look.VRChat;

        foreach (var button in new[] { _lockButton, _throughButton, _closeButton })
            button.Classes.Set("vrchat", v is not null);

        PaintButton(_lockButton, _settings.Locked, on => PanelFrame.LockIcon(on, Ink(on)));
        PaintButton(_throughButton, _settings.ClickThrough, on => PanelFrame.HandIcon(on, Ink(on), Fill(on)));
        PaintButton(_closeButton, false, _ => DesktopOverlayWindow.CloseIcon(Ink(false)));

        IBrush Ink(bool on) => v is null
            ? on ? Ui.T.AccentForegroundBrush : Ui.T.TextBrush
            : on ? v.SelectedText : v.Icon;

        IBrush Fill(bool on) => v is null
            ? on ? Ui.T.AccentBrush : Ui.T.Surface2Brush
            : on ? v.Selected : v.Button;

        void PaintButton(Button button, bool on, Func<bool, Control> icon)
        {
            button.Background = Fill(on);
            button.BorderBrush = v is null
                ? on ? Ui.T.AccentBrush : Ui.T.Border2Brush
                : on ? v.RaisedBrightEdge : v.RaisedEdge;
            button.Width = button.Height = v is null ? Ui.T.Density.ControlHeight : VRChatLook.RoundButton;
            button.CornerRadius = new CornerRadius(v is null ? Ui.T.Density.Radius : VRChatLook.RoundButton / 2);
            button.BorderThickness = new Thickness(v is null ? Ui.T.Density.Hairline : VRChatLook.EdgeWidth);
            button.Padding = new Thickness(v is null ? 6 : 9);
            button.Content = new Viewbox { Child = icon(on) };
        }
    }

    /// <summary>The buttons light up under the mouse in the palette's own colours rather than in the theme's grey.</summary>
    private void PaintHoverStyle()
    {
        foreach (var old in _hoverStyles)
            Styles.Remove(old);

        _hoverStyles.Clear();

        if (_look.VRChat is not { } v)
            return;

        foreach (var state in new[] { ":pointerover", ":pressed" })
        {
            var style = new Style(x => x.OfType<Button>().Class("vrchat").Class(state).Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter"));
            style.Setters.Add(new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, v.Hover));
            style.Setters.Add(new Setter(Avalonia.Controls.Presenters.ContentPresenter.BorderBrushProperty, v.RaisedEdge));
            Styles.Add(style);
            _hoverStyles.Add(style);
        }
    }

    /// <summary>The lock, the hand and the grip, only in edit mode; the grip only while the window is not locked.</summary>
    private void PaintEdit()
    {
        _switches.IsVisible = _editMode;
        _grip.IsVisible = _editMode && !_settings.Locked;
        _grip.Child = _grip.IsVisible ? Grip() : null;
    }

    /// <summary>Two short diagonal strokes in the bottom right corner.</summary>
    private Control Grip()
    {
        IBrush ink = _look.VRChat?.Icon ?? Ui.T.TextDimBrush;

        return new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M16,6 L6,16 M16,11 L11,16"),
            Stroke = ink,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            Margin = new Thickness(0, 0, 3, 3),
        };
    }

    private void GripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_settings.Locked || !e.GetCurrentPoint(_grip).Properties.IsLeftButtonPressed)
            return;

        _resizing = (this.PointToScreen(e.GetPosition(this)), Width, Height);
        e.Pointer.Capture(_grip);
        e.Handled = true;
    }

    private void GripMoved(object? sender, PointerEventArgs e)
    {
        if (_resizing is not { } start)
            return;

        var now = this.PointToScreen(e.GetPosition(this));
        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;

        Width = DesktopChatSpot.ClampWidth(start.Width + ((now.X - start.Pointer.X) / scaling));
        Height = DesktopChatSpot.ClampHeight(start.Height + ((now.Y - start.Pointer.Y) / scaling));
        e.Handled = true;
    }

    private void GripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizing is null)
            return;

        _resizing = null;
        e.Pointer.Capture(null);
        Saved();
    }

    private void OnMoved(object? sender, PixelPointEventArgs e)
    {
        if (!IsVisible || e.Point == _placedAt)
            return;

        _placedAt = null;
        Saved();
    }

    /// <summary>Tells whoever keeps the spot where the window is and how big it is.</summary>
    private void Saved()
    {
        _spot = new DesktopChatSpot(Position.X, Position.Y, Width, Height);
        SpotChanged?.Invoke(_spot);
    }

    /// <summary>
    /// Where it was left, brought onto a screen that still exists; otherwise to the left of the
    /// window it is placed beside, and otherwise down the right-hand side of the screen.
    /// </summary>
    private void Place()
    {
        if (_placed)
            return;

        if (_spot is { } spot && Screens.ScreenFromPoint(new PixelPoint(spot.Left, spot.Top)) is { } kept)
        {
            Put(kept.WorkingArea, spot.Left, spot.Top, (int)(Width * kept.Scaling), (int)(Height * kept.Scaling));
            return;
        }

        var screen = (Beside is not null ? Screens.ScreenFromVisual(Beside) : null)
            ?? Screens.Primary
            ?? Screens.All.FirstOrDefault();

        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var width = (int)(Width * screen.Scaling);
        var height = (int)(Height * screen.Scaling);

        if (Beside is { IsVisible: true } beside)
            Put(area, beside.Position.X - width - EdgeMargin, beside.Position.Y, width, height);
        else
            Put(area, area.Right - width - EdgeMargin, area.Y + ((area.Height - height) / 2), width, height);
    }

    private void Put(PixelRect area, int left, int top, int width, int height)
    {
        var at = new PixelPoint(
            Math.Clamp(left, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(top, area.Y, Math.Max(area.Y, area.Bottom - height)));

        _placedAt = at;
        Position = at;
        _placed = true;
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

    /// <summary>Clicks go through everywhere but the strip and the grip: over those the window takes them.</summary>
    private void WatchTheMouse()
    {
        if (!GetCursorPos(out var mouse))
            return;

        var overIt = Over(_strip, mouse) || (_grip.IsVisible && Over(_grip, mouse));

        PassClicks(!overIt);
    }

    private static bool Over(Control control, MousePoint mouse)
    {
        var topLeft = control.PointToScreen(new Point(0, 0));
        var bottomRight = control.PointToScreen(new Point(control.Bounds.Width, control.Bounds.Height));

        return mouse.X >= topLeft.X && mouse.X < bottomRight.X && mouse.Y >= topLeft.Y && mouse.Y < bottomRight.Y;
    }

    private void PassClicks(bool pass)
    {
        if (_passingClicks == pass)
            return;

        _passingClicks = pass;

        if (_styled)
            ApplyStyle();
    }

    /// <summary>Tells Windows this window is never activated, is out of Alt-Tab, and lets clicks through while it should.</summary>
    private bool ApplyStyle()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero)
            return false;

        try
        {
            var style = (long)GetWindowLongPtrW(handle, GwlExStyle);
            var wanted = (style | WsExNoActivate | WsExToolWindow) & ~WsExTransparent;
            if (_passingClicks)
                wanted |= WsExTransparent;

            SetWindowLongPtrW(handle, GwlExStyle, (IntPtr)wanted);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            // A Windows without the 64-bit entry points: the window is still shown without being activated.
            return false;
        }
    }

    private static IntPtr KeepFocusOnTheGame(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmMouseActivate)
            return IntPtr.Zero;

        handled = true;
        return (IntPtr)MaNoActivate;
    }

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
}
