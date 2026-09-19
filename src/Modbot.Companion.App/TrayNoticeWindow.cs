using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// The small panel by the clock that says Modbot is still running after the window is closed.
/// </summary>
/// <remarks>
/// <para><strong>Why a window of our own.</strong> Avalonia's tray icon has an icon, a tooltip, a
/// menu and a click, and nothing that shows a notification; the alternative was a Windows toast,
/// which means another dependency and an app registration for one sentence. So the client draws it:
/// a panel at the bottom right of the working area, above the tray, which takes no focus, appears in
/// no taskbar, closes itself after a few seconds and closes at once if it is clicked.</para>
/// <para>It is shown the first few times only — the count lives in <c>settings.json</c>
/// (<see cref="Modbot.Companion.Sounds.NotificationSettings"/>) — because somebody who closes the
/// window twenty times a day must not be told twenty times.</para>
/// <para>It reads nothing and sends nothing. It is one line of text on this desktop.</para>
/// </remarks>
internal sealed class TrayNoticeWindow : Window
{
    /// <summary>How long it stays up. Long enough to read one line, short enough not to be in the way.</summary>
    private static readonly TimeSpan HowLongItStays = TimeSpan.FromSeconds(4);

    /// <summary>How far in from the corner of the working area it sits.</summary>
    private const int GapFromTheCorner = 12;

    private readonly DispatcherTimer _close = new() { Interval = HowLongItStays };

    private TrayNoticeWindow(string line)
    {
        Title = "Modbot";
        Icon = Brand.Icon();
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        // The window's own background is the panel's, so a borderless window with no rounding has
        // nothing showing through at its edges on any desktop.
        Background = Ui.T.SurfaceBrush;

        var text = Ui.Text(line, Ui.T.Density.TextSmall, Ui.T.TextBrush, wrap: false);
        text.VerticalAlignment = VerticalAlignment.Center;

        var mark = Brand.Mark(20);
        mark.VerticalAlignment = VerticalAlignment.Center;

        Content = new Border
        {
            Background = Ui.T.SurfaceBrush,
            BorderBrush = Ui.T.Border2Brush,
            BorderThickness = new Thickness(Ui.T.Density.Hairline),
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { mark, text },
            },
        };

        _close.Tick += (_, _) => Close();
        PointerPressed += (_, _) => Close();
    }

    /// <summary>
    /// Shows the notice, or does nothing at all if this desktop will not have it. A notice that
    /// cannot be drawn is not a reason for anything else to stop.
    /// </summary>
    public static void Show(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        try
        {
            var notice = new TrayNoticeWindow(line);
            notice.Opened += (_, _) => notice.SitAboveTheTray();
            notice.Closed += (_, _) => notice._close.Stop();
            notice.Show();
            notice._close.Start();
        }
        catch (Exception ex)
        {
            Log.Information(ex, "The tray notice could not be shown; everything else is unaffected");
        }
    }

    /// <summary>
    /// Puts it in the bottom right of the screen's working area, which is where the tray is on a
    /// default Windows and is out of the way everywhere else.
    /// </summary>
    private void SitAboveTheTray()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var width = (int)Math.Round(Bounds.Width * scaling);
        var height = (int)Math.Round(Bounds.Height * scaling);
        var gap = (int)Math.Round(GapFromTheCorner * scaling);

        Position = new PixelPoint(
            area.X + Math.Max(0, area.Width - width - gap),
            area.Y + Math.Max(0, area.Height - height - gap));
    }
}
