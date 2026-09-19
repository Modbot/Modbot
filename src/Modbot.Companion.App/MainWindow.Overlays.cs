using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Modbot.Companion.Overlay;

namespace Modbot.Companion.App;

/// <summary>
/// The notification overlay's card on the SteamVR page: the switch, where on the screen it sits,
/// how big it is and how long a pop-up stays.
/// </summary>
/// <remarks>
/// <para>Its own file because the two overlays are two things with two sets of settings, and the
/// window is long enough already.</para>
/// <para>The settings stay on the page whichever way the switch is set. A moderator setting up for
/// the first time arranges where the pop-ups will appear and then turns them on, and the way to do
/// that used to be to turn them on, arrange them, and turn them off again (two overlay modes
/// design §6.1).</para>
/// </remarks>
public sealed partial class MainWindow
{
    // Built once and re-attached on every render, like the other switches and sliders: the window
    // redraws on a timer, and a control rebuilt every second loses the click or the drag on it.
    private readonly CheckBox _notifyOnBox = new();
    private Slider _notifyAcross = null!;
    private Slider _notifyDown = null!;
    private Slider _notifyDistance = null!;
    private Slider _notifyWidth = null!;
    private Slider _notifyOpacity = null!;
    private Slider _notifySeconds = null!;
    private bool _notifyWired;

    private NotificationSettings NotifySettings => _snapshot.NotificationsOrNone.SettingsOrDefault;

    /// <summary>The notification overlay's card, added to the page under the main overlay's.</summary>
    private void RenderNotificationOverlay()
    {
        var notifications = _snapshot.NotificationsOrNone;
        var settings = notifications.SettingsOrDefault;

        WireNotifyControls();

        _renderingSwitches = true;
        try
        {
            _notifyOnBox.IsChecked = settings.On;
            Refill(_notifyAcross, settings.Across);
            Refill(_notifyDown, settings.Down);
            Refill(_notifyDistance, settings.Distance);
            Refill(_notifyWidth, settings.Width);
            Refill(_notifyOpacity, settings.Opacity);
            Refill(_notifySeconds, settings.Seconds);
        }
        finally
        {
            _renderingSwitches = false;
        }

        foreach (var control in new Control[]
            { _notifyOnBox, _notifyAcross, _notifyDown, _notifyDistance, _notifyWidth, _notifyOpacity, _notifySeconds })
        {
            DetachFromParent(control);
        }

        var pill = !settings.On
            ? Ui.Pill("Off", Ui.T.Palette.TextFaint, Ui.T.Palette.Surface2)
            : notifications.Attached
                ? Ui.Pill("Attached", Ui.T.Palette.Ok, Ui.T.Palette.OkDim)
                : notifications.State switch
                {
                    "refused" => Ui.Pill("Refused", Ui.T.Palette.Danger, Ui.T.Palette.DangerDim),
                    "SteamVR not installed" or "not set up" => Ui.Pill("No SteamVR", Ui.T.Palette.Info, Ui.T.Palette.InfoDim),
                    _ => Ui.Pill("Not running", Ui.T.Palette.Warn, Ui.T.Palette.WarnDim),
                };

        var lines = new StackPanel { Spacing = 12, Children = { _notifyOnBox } };

        // Only while something is running is there anything to say about it.
        if (settings.On && notifications.Attached)
        {
            var stats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 10 };
            Control[] tiles =
            [
                Ui.Stat("attached since", Clock(notifications.AttachedAt)),
                Ui.Stat("frames drawn", $"{notifications.FramesDrawn:N0}"),
                Ui.Stat("pop-ups up", $"{notifications.PopUps:N0}"),
            ];

            for (var index = 0; index < tiles.Length; index++)
            {
                Grid.SetColumn(tiles[index], index);
                stats.Children.Add(tiles[index]);
            }

            lines.Children.Add(stats);
        }

        lines.Children.Add(Ui.Field("Where on the screen", Spots(settings.Spot)));
        lines.Children.Add(Ui.Field($"Across {settings.Across:0.00} m", Stepper(_notifyAcross, 0.02)));
        lines.Children.Add(Ui.Field($"Down {settings.Down:0.00} m", Stepper(_notifyDown, 0.02)));
        lines.Children.Add(Ui.Field($"Distance {settings.Distance:0.00} m", Stepper(_notifyDistance, 0.05)));
        lines.Children.Add(Ui.Field($"Width {settings.Width:0.00} m", Stepper(_notifyWidth, 0.05)));
        lines.Children.Add(Ui.Field($"Opacity {settings.Opacity:0%}", Stepper(_notifyOpacity, 0.05)));
        lines.Children.Add(Ui.Field($"Pop-up stays {settings.Seconds:0} s", Stepper(_notifySeconds, 1)));

        var reset = Ui.Button("Put it back");
        reset.Click += (_, _) => _actions.SetNotifyOverlay(NotificationSettings.Default with { On = NotifySettings.On });
        lines.Children.Add(reset);

        _body.Children.Add(Ui.Card(lines, "Notification overlay", pill));
    }

    private Control Spots(ScreenSpot chosen)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        foreach (var spot in Enum.GetValues<ScreenSpot>())
        {
            var button = Ui.Button(NotificationSettings.Name(spot), primary: spot == chosen);
            var picked = spot;
            button.Click += (_, _) => _actions.SetNotifyOverlay(NotifySettings with { Spot = picked });
            row.Children.Add(button);
        }

        return row;
    }

    private void WireNotifyControls()
    {
        if (_notifyWired)
            return;

        _notifyWired = true;

        _notifyAcross = NotifySlider(-NotificationSettings.MaxFine, NotificationSettings.MaxFine, 0.02, (s, v) => s with { Across = (float)v });
        _notifyDown = NotifySlider(-NotificationSettings.MaxFine, NotificationSettings.MaxFine, 0.02, (s, v) => s with { Down = (float)v });
        _notifyDistance = NotifySlider(NotificationSettings.MinDistance, NotificationSettings.MaxDistance, 0.05, (s, v) => s with { Distance = (float)v });
        _notifyWidth = NotifySlider(NotificationSettings.MinWidth, NotificationSettings.MaxWidth, 0.05, (s, v) => s with { Width = (float)v });
        _notifyOpacity = NotifySlider(NotificationSettings.MinOpacity, 1, 0.05, (s, v) => s with { Opacity = (float)v });
        _notifySeconds = NotifySlider(NotificationSettings.MinSeconds, NotificationSettings.MaxSeconds, 1, (s, v) => s with { Seconds = (float)v });

        _notifyOnBox.Content = Ui.Text("Notification overlay on", Ui.T.Density.TextSmall, Ui.T.TextBrush);
        _notifyOnBox.VerticalAlignment = VerticalAlignment.Center;
        _notifyOnBox.IsCheckedChanged += (_, _) =>
        {
            if (!_renderingSwitches)
                _actions.SetNotifyOverlay(NotifySettings with { On = _notifyOnBox.IsChecked == true });
        };
    }

    private Slider NotifySlider(double minimum, double maximum, double step, Func<NotificationSettings, double, NotificationSettings> change)
    {
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Width = 300,
            MinHeight = 40,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        slider.ValueChanged += (_, e) =>
        {
            if (!_renderingSwitches)
                _actions.SetNotifyOverlay(change(NotifySettings, e.NewValue));
        };

        return slider;
    }
}
