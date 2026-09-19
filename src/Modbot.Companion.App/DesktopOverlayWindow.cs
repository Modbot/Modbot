using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Journal;
using Modbot.Companion.Presentation;
using Modbot.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

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
/// </remarks>
internal sealed class DesktopOverlayWindow : Window, IOverlayPresenter
{
    /// <summary>Wide enough for a roster row's name, flags and count without wrapping.</summary>
    private const double PanelWidth = 460;

    private const double PanelHeight = 720;

    /// <summary>How far in from the screen's edge it sits.</summary>
    private const int EdgeMargin = 32;

    /// <summary>How many of the client's own recent events are shown under the panel.</summary>
    private const int RecentEvents = 5;

    private readonly ContentControl _panel = new();
    private readonly StackPanel _recent = new() { Spacing = 0 };
    private readonly Border _recentCard;
    private readonly TextBlock _where = Ui.Faint("");

    private OverlayScreen? _drawn;
    private DesktopOverlaySettings _settings = DesktopOverlaySettings.Default;

    /// <summary>A click on the panel, as the target under it. Null is a click on nothing.</summary>
    public event Action<OverlayTarget?>? PanelTapped;

    /// <summary>The wheel, or j and k, as whole roster rows.</summary>
    public event Action<int>? RosterScrolled;

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

        _recentCard = new Border
        {
            Background = Ui.T.SurfaceBrush,
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(Ui.T.Density.Hairline),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Margin = new Thickness(12, 0, 12, 12),
            Child = _recent,
        };

        var body = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _panel,
        };

        var head = Head();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(_recentCard, Dock.Bottom);

        Content = new DockPanel { Children = { head, _recentCard, body } };

        _panel.PointerPressed += OnPanelPressed;
        _panel.PointerWheelChanged += (_, e) =>
        {
            RosterScrolled?.Invoke(e.Delta.Y > 0 ? -1 : 1);
            e.Handled = true;
        };

        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// The strip along the top: the product's name, where the panel is looking, and the way to
    /// move the window. Dragging it is why it exists — the window has no border to drag.
    /// </summary>
    private Control Head()
    {
        var name = Ui.Text("Modbot", Ui.T.Density.TextBase, Ui.T.TextBrush, FontWeight.SemiBold, wrap: false);
        name.VerticalAlignment = VerticalAlignment.Center;

        _where.VerticalAlignment = VerticalAlignment.Center;

        var close = Ui.Button("Close");
        close.Click += (_, _) => Dismiss();

        var row = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(name);
        row.Children.Add(close);
        row.Children.Add(_where);

        var head = new Border
        {
            Padding = new Thickness(12, 8),
            Background = Ui.T.Surface2Brush,
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, Ui.T.Density.Hairline),
            Child = row,
        };

        head.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(head).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        return head;
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
        _where.Text = screen.GroupLabel ?? "Not in a group instance";

        // The idle screen draws nothing at all in a headset, where the panel hangs in the world.
        // In a window there is a window either way, so it says so rather than going blank.
        _panel.Content = screen.IsIdle
            ? OverlayView.Build(screen with { ShowIdleCard = true })
            : OverlayView.Build(screen);

        return true;
    }

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

    /// <summary>The opacity, and whether the window may be up at all.</summary>
    public void Apply(DesktopOverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        Background = GroundBrush();

        if (!settings.On)
            Dismiss();
    }

    /// <summary>The last few things the client did, under the panel. The Events page's own rows.</summary>
    public void Refresh(CompanionAppSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!IsVisible)
            return;

        var groups = snapshot.Servers.ToDictionary(s => s.ServerId, s => s.GroupName, StringComparer.Ordinal);
        var rows = snapshot.Events.Take(RecentEvents).ToList();

        _recentCard.IsVisible = rows.Count > 0;
        _recent.Children.Clear();

        var first = true;
        foreach (var row in rows)
        {
            _recent.Children.Add(MainWindow.EventRow(row, first, groups));
            first = false;
        }
    }

    /// <summary>
    /// The shortcut, or Escape: the window goes by the rule on the settings record, which is where
    /// it is tested.
    /// </summary>
    public void Press(bool escape)
    {
        if (_settings.NextShowing(IsVisible, escape))
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
        Place(PlaceNear);
        Show();
        Activate();
        Focus();
    }

    public void Dismiss()
    {
        if (IsVisible)
            Hide();
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

    private void OnPanelPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_panel.Content is not Visual root)
            return;

        // The same lookup a controller's ray uses, on the same tagged controls, with a mouse
        // position instead of a point on a texture.
        PanelTapped?.Invoke(OverlayTargets.At(root, e.GetPosition(root)));
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Press(escape: true);
                e.Handled = true;
                break;
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
