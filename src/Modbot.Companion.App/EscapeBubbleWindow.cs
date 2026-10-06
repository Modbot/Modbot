using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Modbot.Companion.Presentation;
using Modbot.Overlay;

namespace Modbot.Companion.App;

/// <summary>
/// Modbot's bubble in VRChat's HUD row, the slot after Y: Modbot's face and the key that opens the
/// overlay, drawn like the bubbles VRChat draws itself (desktop overlay Escape Menu design §3).
/// </summary>
/// <remarks>
/// <para><strong>It is a window of its own, because VRChat's HUD is not ours to draw in.</strong>
/// The window is the bubble and its faint backing panel, drawn like the one VRChat puts behind its
/// own four bubbles, so it has no empty margin to block a click meant for the game: whatever it
/// covers is the bubble or its panel, and clicking either is the one thing it is for.</para>
/// <para><strong>It never takes the keyboard from VRChat.</strong> It is shown without being
/// activated and, on Windows, marked as a window a click does not activate.</para>
/// <para><strong>It reads nothing and sends nothing.</strong> It is told what to say and whether
/// the overlay is open; it tells its owner when it is clicked.</para>
/// <para><strong>Where it goes is not decided here.</strong> The owner places it from
/// <see cref="EscapeBubbleMetrics.Place"/> once VRChat's window is known; this only draws.</para>
/// </remarks>
internal sealed class EscapeBubbleWindow : Window
{
    private const string IconAsset = "avares://Modbot/Assets/escape-bubble-icon.png";

    /// <summary>VRChat's own label pill: a near-black, practically opaque, with light text. Not ours, so not a token.</summary>
    private static readonly Color PillOff = Color.Parse("#0A040C");

    /// <summary>VRChat's faint backing panel: black at 22%, which darkens what is behind it by that much.</summary>
    private static readonly Color PanelGround = Color.FromArgb((byte)Math.Round(EscapeBubbleMetrics.PanelAlpha * 255), 0, 0, 0);

    private static readonly Color PillText = Color.Parse("#d8d8e4");

    /// <summary>The grey of VRChat's icons.</summary>
    private static readonly Color IconOff = Color.Parse("#b4b2b4");

    private const int GwlExStyle = -20;

    /// <summary>Clicking it never brings it to the front, so VRChat keeps the keyboard.</summary>
    private const long WsExNoActivate = 0x08000000;

    /// <summary>Keeps it out of Alt-Tab; it is not a window anybody switches to.</summary>
    private const long WsExToolWindow = 0x00000080;

    private static readonly Lazy<Bitmap> IconPicture = new(
        () => new Bitmap(AssetLoader.Open(new Uri(IconAsset, UriKind.Absolute))));

    private readonly Border _panel;
    private readonly Rectangle _icon;
    private readonly Border _pill;
    private readonly TextBlock _label;
    private EscapeBubbleMetrics _metrics = new(1.0);
    private bool _lit;
    private bool _styled;

    public EscapeBubbleWindow()
    {
        Title = "Modbot";
        Icon = Brand.Icon();
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Hand);

        _icon = new Rectangle
        {
            OpacityMask = new ImageBrush(IconPicture.Value) { Stretch = Stretch.Uniform },
        };

        _label = new TextBlock
        {
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // The text keeps the size of VRChat's own labels; a long one makes the pill wider instead.
        _pill = new Border
        {
            CornerRadius = new CornerRadius(4),
            Child = _label,
        };

        // VRChat's own faint dark panel, behind the bubble, in the same window: one window, one
        // click target. The whole window takes the click.
        _panel = new Border { Background = new SolidColorBrush(PanelGround) };

        Content = new Canvas { Children = { _panel, _icon, _pill } };

        Apply(new EscapeBubbleMetrics(1.0));
        Paint();
        Opened += (_, _) => KeepItOutOfTheWay();
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                Clicked?.Invoke();
        };
    }

    /// <summary>The bubble was clicked: the owner opens or closes the overlay.</summary>
    public event Action? Clicked;

    /// <summary>
    /// How big VRChat's HUD is, so the bubble is the size of the bubbles beside it. Draws again
    /// at the new size.
    /// </summary>
    public void Apply(EscapeBubbleMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        _metrics = metrics;
        _label.FontSize = metrics.FontSize;
        _icon.Height = metrics.IconHeight;
        _icon.Width = metrics.IconHeight * 68.0 / 64;
        _pill.Height = metrics.PillHeight;
        _panel.CornerRadius = new CornerRadius(metrics.PanelRadius);
        _panel.Height = metrics.PanelHeight;
        Canvas.SetTop(_icon, metrics.PillOffsetY);
        Canvas.SetTop(_pill, metrics.PillOffsetY + metrics.PillTop);
        Height = metrics.PanelHeight;
        Fit();
    }

    /// <summary>What the pill says, as <see cref="EscapeBubbleLayout.Label"/> wrote it.</summary>
    public void SetLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        _label.Text = label;
        Fit();
    }

    /// <summary>The window's width, which is the backing panel's: as wide as the bubble and its label need.</summary>
    public int BubbleWidth => (int)Width;

    /// <summary>
    /// Makes the panel, and so the window, as wide as the label needs, keeps the bubble where it
    /// is on the row, and puts the icon over the middle of the pill.
    /// </summary>
    private void Fit()
    {
        _label.Measure(Size.Infinity);
        var pillWidth = _metrics.Width(_label.DesiredSize.Width);
        var width = _metrics.PanelWidth(_label.DesiredSize.Width);

        Width = width;
        _panel.Width = width;
        _pill.Width = pillWidth;
        Canvas.SetLeft(_pill, _metrics.PillOffsetX);
        Canvas.SetLeft(_icon, _metrics.PillOffsetX + ((pillWidth - _icon.Width) / 2));
    }

    /// <summary>Lit while the desktop overlay is open.</summary>
    public void SetLit(bool lit)
    {
        if (_lit == lit)
            return;

        _lit = lit;
        Paint();
    }

    /// <summary>Puts the top-left of the bubble at a screen position, in pixels.</summary>
    public void PlaceAt(PixelPoint point) => Position = point;

    private void Paint()
    {
        var accent = DesignTokens.Desktop.Palette.Accent;
        var white = DesignTokens.Desktop.Palette.AccentForeground;

        _icon.Fill = new SolidColorBrush(_lit ? white : IconOff);
        _pill.Background = new SolidColorBrush(_lit ? accent : PillOff);
        _label.Foreground = new SolidColorBrush(_lit ? white : PillText);
    }

    /// <summary>
    /// Tells Windows a click on this window does not activate it, so VRChat keeps the keyboard.
    /// Windows only; elsewhere the window is still shown without being activated.
    /// </summary>
    private void KeepItOutOfTheWay()
    {
        if (_styled || !OperatingSystem.IsWindows())
            return;

        if (TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero)
            return;

        try
        {
            var style = (long)GetWindowLongPtrW(handle, GwlExStyle);
            SetWindowLongPtrW(handle, GwlExStyle, (IntPtr)(style | WsExNoActivate | WsExToolWindow));
            _styled = true;
        }
        catch (EntryPointNotFoundException)
        {
            // A Windows without the 64-bit entry points: still never activated by being shown.
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
}
