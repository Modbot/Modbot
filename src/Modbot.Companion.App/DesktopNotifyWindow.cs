using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Win32;
using Modbot.Companion.Presentation;
using Modbot.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Companion.App;

/// <summary>
/// The notification overlay on a monitor: a small window in a corner of the screen that shows a
/// notification as it arrives and then lets it go.
/// </summary>
/// <remarks>
/// <para><strong>It is not the window the shortcut brings up.</strong> That one is read when a
/// moderator decides to look; this one is only ever there to tell them something they were not
/// looking for. Different window, its own switch, its own lifetime: it is not summoned by the
/// shortcut and it does not go away when the other window is dismissed (desktop overlay design
/// §7).</para>
/// <para><strong>It never takes the keyboard from VRChat.</strong> It is shown without being
/// activated, and on Windows it is marked as a window that cannot be activated and that a click
/// passes straight through — so a moderator who clicks where it happens to be is clicking the game.
/// There is nothing on it to press, except while the update prompt is up (<c>UpdatePrompt</c>):
/// then, and only then, a click lands on its Update and Later buttons. It is still never
/// activated, so VRChat keeps the keyboard, and it is click-through again the moment the prompt is
/// answered (update checking design §10).</para>
/// <para><strong>It sits where it was put.</strong> One of the six corners from settings, on the
/// screen Modbot's own window is on. It is not dragged, and nothing it shows can move it.</para>
/// <para><strong>Nothing is drawn while nothing is happening.</strong> An empty stack hides the
/// window outright rather than leaving an empty frame in the corner, which is the thing that makes
/// people turn overlays off.</para>
/// <para><strong>It reads nothing and sends nothing.</strong> The notifications are pushed into it
/// by the client, out of what the client already holds. This window has no client, no address and
/// no socket, and no server can tell it to appear or to show anything.</para>
/// </remarks>
internal sealed class DesktopNotifyWindow : Window
{
    /// <summary>Wide enough for a name and a line about it, narrow enough to stay out of the way.</summary>
    private const double PanelWidth = 340;

    private const int GwlExStyle = -20;

    /// <summary>A click goes through to whatever is underneath, which is the game.</summary>
    private const long WsExTransparent = 0x00000020;

    /// <summary>Clicking it never brings it to the front, so VRChat keeps the keyboard.</summary>
    private const long WsExNoActivate = 0x08000000;

    /// <summary>Keeps it out of Alt-Tab; it is not a window anybody switches to.</summary>
    private const long WsExToolWindow = 0x00000080;

    /// <summary>Windows asks a window, before a click activates it, whether it may.</summary>
    private const uint WmMouseActivate = 0x0021;

    /// <summary>The answer to <see cref="WmMouseActivate"/>: take the click, but do not activate.</summary>
    private const int MaNoActivate = 3;

    private readonly ContentControl _cards = new();

    private NotificationScreen _drawn = NotificationScreen.Empty;
    private DesktopNotifySettings _settings = DesktopNotifySettings.Default;
    private OverlayLook? _look;
    private bool _styled;

    // Whether a click lands on this window at all: only while a card with buttons is up.
    private bool _clickable;

    public DesktopNotifyWindow()
    {
        Title = "Modbot";
        Icon = Brand.Icon();
        Width = PanelWidth;
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;

        // The whole point: it appears without taking the front from the game.
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Height;

        // Each card paints its own surface; the rest of the window lets the game through, so a
        // corner with one short card in it is not a slab over the game.
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

        Content = _cards;
        _cards.PointerPressed += OnCardsPressed;

        // While clicks land on it, the style bit alone is not the only guard: a click is also
        // answered "do not activate" when Windows asks, so it can never take VRChat's focus.
        if (OperatingSystem.IsWindows())
            Win32Properties.AddWndProcHookCallback(this, KeepFocusOnTheGame);

        // The window is as tall as what is in it, so where it goes is worked out again every time
        // that changes — otherwise a bottom corner would drift as cards come and go.
        SizeChanged += (_, _) => Place();
        Opened += (_, _) =>
        {
            KeepItOutOfTheWay();
            Place();
        };
    }

    /// <summary>
    /// A button of a card that asks was clicked: the card's id, and whether it was the first button.
    /// </summary>
    public event Action<string, bool>? PopUpAnswered;

    /// <summary>
    /// Which visual the window is placed beside, so it lands on the monitor Modbot's own window is
    /// on. Null puts it on the main screen.
    /// </summary>
    public Visual? PlaceNear { get; set; }

    /// <summary>
    /// How the pop-ups are drawn: VRChat's colours and cards when it is given its look, and the
    /// desktop's own look as it has always been when it is given none or the plain one. Drawn again
    /// at once while something is up.
    /// </summary>
    public OverlayLook? Look
    {
        get => _look;
        set
        {
            if (ReferenceEquals(_look, value))
                return;

            _look = value;

            if (!_drawn.IsEmpty)
            {
                _cards.Content = Cards(_drawn);
                Place();
            }
        }
    }

    /// <summary>The cards in the look in use. The desktop's palette and density: the headset's is sized for a panel a metre away, and a card that size on a monitor would cover a corner of the game.</summary>
    private Control Cards(NotificationScreen screen)
        => _look is { VRChat: not null } look
            ? NotificationView.Build(screen, look, NotificationView.DesktopScale)
            : NotificationView.Build(screen, DesignTokens.Desktop);

    /// <summary>The corner, the seconds, and whether it may be up at all.</summary>
    public void Apply(DesktopNotifySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;

        if (!settings.On)
            Clear();
        else
            Place();
    }

    /// <summary>
    /// What is up right now, newest first. An empty stack hides the window; nothing is drawn for
    /// nothing.
    /// </summary>
    public bool Update(NotificationScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        if (!_settings.On)
        {
            Clear();
            return false;
        }

        if (_drawn.LooksTheSameAs(screen) && IsVisible == !screen.IsEmpty)
            return false;

        _drawn = screen;

        if (screen.IsEmpty)
        {
            Clear();
            return true;
        }

        _cards.Content = Cards(screen);
        SetClickable(screen.PopUps.Any(p => p.Ask is not null));

        if (!IsVisible)
            Show();

        Place();
        return true;
    }

    /// <summary>Takes it away: nothing to say, or the switch turned off.</summary>
    public void Clear()
    {
        _drawn = NotificationScreen.Empty;
        _cards.Content = null;
        SetClickable(false);

        if (IsVisible)
            Hide();
    }

    /// <summary>
    /// Puts the window in its corner of the work area, on the screen Modbot's own window is on.
    /// </summary>
    /// <remarks>
    /// The work area rather than the whole screen, so it does not sit on the taskbar. Which corner
    /// lands where is <see cref="DesktopNotifySettings.Corner"/>, which is plain numbers and is
    /// tested without a screen.
    /// </remarks>
    private void Place()
    {
        // Nothing to place while it is not up, and the screens cannot be asked for before then.
        if (!IsVisible)
            return;

        var screen = (PlaceNear is not null ? Screens.ScreenFromVisual(PlaceNear) : null)
            ?? Screens.Primary
            ?? Screens.All.FirstOrDefault();

        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var width = (int)(Width * scaling);
        var height = (int)(Math.Max(Height, Bounds.Height) * scaling);
        var margin = (int)(DesktopNotifySettings.EdgeMargin * scaling);

        var (x, y) = DesktopNotifySettings.Corner(
            _settings.Spot, area.X, area.Y, area.Width, area.Height, width, height, margin);

        Position = new PixelPoint(x, y);
    }

    /// <summary>
    /// Tells Windows this is a window that is never activated and that a click passes through
    /// (see <see cref="SetClickable"/> for the one time it does not).
    /// </summary>
    /// <remarks>
    /// Without it, clicking where the notification happens to be would take the front away from
    /// VRChat — which is the one thing an overlay over a game must never do. Windows only, because
    /// this is Windows' own idea; everywhere else the window is still shown without being
    /// activated, which is most of it.
    /// </remarks>
    private void KeepItOutOfTheWay()
    {
        if (_styled)
            return;

        _styled = ApplyStyle();
    }

    /// <summary>
    /// Lets clicks land on the window while a card with buttons is up, and sends them through to the
    /// game again otherwise. It is never activated either way.
    /// </summary>
    private void SetClickable(bool clickable)
    {
        if (_clickable == clickable)
            return;

        _clickable = clickable;

        if (_styled)
            ApplyStyle();
    }

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
            if (!_clickable)
                wanted |= WsExTransparent;

            SetWindowLongPtrW(handle, GwlExStyle, (IntPtr)wanted);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            // A Windows without the 64-bit entry points. The window is still never activated by
            // being shown; it just is not click-through.
            return false;
        }
    }

    private IntPtr KeepFocusOnTheGame(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmMouseActivate || !_clickable)
            return IntPtr.Zero;

        handled = true;
        return (IntPtr)MaNoActivate;
    }

    private void OnCardsPressed(object? sender, PointerPressedEventArgs e)
    {
        // The left button only: a right or middle click on the card does nothing.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (_cards.Content is not Visual root)
            return;

        if (OverlayTargets.At(root, e.GetPosition(root)) is OverlayTarget.AnswerPopUp answer)
        {
            PopUpAnswered?.Invoke(answer.Id, answer.Yes);
            e.Handled = true;
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
}
