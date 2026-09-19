using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Modbot.Companion.Sounds;

namespace Modbot.Companion.App;

/// <summary>
/// The Settings page's Notifications card and its Restart card.
/// </summary>
/// <remarks>
/// <para>The bleep has its own switch and its own volume, kept apart from the voice's: a moderator
/// who wants a sound when somebody flagged walks in, and not a talking PC, is the ordinary case.
/// Which device it plays through is the voice's choice and is not repeated here.</para>
/// <para>Restarting stops reporting for a few seconds, so the button asks twice: the first press
/// arms it, the second does it, and it disarms itself if it is left alone. It reads nothing and
/// sends nothing; the whole of what it does is on <see cref="Modbot.Companion.Startup.CompanionRestart"/>.</para>
/// </remarks>
public sealed partial class MainWindow
{
    // Built once, like the Voice card's: a slider being dragged dies under a rebuild, and so does
    // a button in the middle of being pressed twice.
    private readonly CheckBox _bleepOn = new();
    private readonly Slider _bleepVolume = new();
    private readonly TextBlock _bleepVolumeValue = Ui.Text("", Ui.T.Density.TextSmall, Ui.T.TextDimBrush, wrap: false, mono: true);
    private readonly Button _bleepTest = Ui.Button("Test");
    private readonly Button _restart = Ui.Button("Restart Modbot Companion");
    private readonly TextBlock _restartLine = Ui.Faint("");

    /// <summary>How long the Restart button stays armed before it goes back to asking again.</summary>
    private readonly DispatcherTimer _restartDisarm = new() { Interval = TimeSpan.FromSeconds(5) };

    private bool _restartArmed;

    /// <summary>Wires the Notifications and Restart cards. Called once, from the constructor.</summary>
    private void SetUpNotifications()
    {
        _bleepOn.Content = Ui.Text("Sound on", Ui.T.Density.TextSmall, Ui.T.TextBrush);
        _bleepOn.IsCheckedChanged += (_, _) => NotificationsChanged();

        _bleepVolumeValue.VerticalAlignment = VerticalAlignment.Center;
        _bleepVolumeValue.Width = 32;

        _bleepVolume.Minimum = 0;
        _bleepVolume.Maximum = NotificationSettings.MaxVolume;
        _bleepVolume.TickFrequency = 1;
        _bleepVolume.IsSnapToTickEnabled = true;
        _bleepVolume.Width = 220;
        _bleepVolume.VerticalAlignment = VerticalAlignment.Center;
        _bleepVolume.ValueChanged += (_, _) =>
        {
            _bleepVolumeValue.Text = $"{(int)_bleepVolume.Value}";
            NotificationsChanged();
        };

        _bleepTest.Click += (_, _) => _actions.TestBleep();

        _restart.Click += async (_, _) => await CrashGuard.RunAsync("restarting Modbot", RestartPressedAsync);
        _restartDisarm.Tick += (_, _) => DisarmRestart();
    }

    /// <summary>What the Notifications card's controls say right now, as one settings record.</summary>
    private void NotificationsChanged()
    {
        if (_renderingSwitches)
            return;

        _actions.SetNotifications(_snapshot.NotificationsOrDefault with
        {
            Bleep = _bleepOn.IsChecked == true,
            Volume = (int)_bleepVolume.Value,
        });
    }

    /// <summary>Puts the snapshot into the card's controls without any of them answering back.</summary>
    private void RefreshNotificationControls(NotificationSettings notifications)
    {
        _bleepOn.IsChecked = notifications.Bleep;

        if (!_bleepVolume.IsPointerOver && !_bleepVolume.IsFocused)
            _bleepVolume.Value = NotificationSettings.ClampVolume(notifications.Volume);

        _bleepVolumeValue.Text = $"{(int)_bleepVolume.Value}";
    }

    /// <summary>The Notifications card: the sound, how loud, and a Test button.</summary>
    private Control NotificationsCard()
    {
        foreach (var control in new Control[] { _bleepOn, _bleepVolume, _bleepVolumeValue, _bleepTest })
            DetachFromParent(control);

        _bleepTest.IsEnabled = _snapshot.VoiceOrNone.HasOutput;

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                _bleepOn,
                Ui.Field("Volume", new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _bleepVolume, _bleepVolumeValue },
                }),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { _bleepTest },
                },
            },
        };
    }

    /// <summary>The Restart card: one button that asks twice.</summary>
    private Control RestartCard()
    {
        DetachFromParent(_restart);
        DetachFromParent(_restartLine);

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { _restart, _restartLine },
        };
    }

    private async Task RestartPressedAsync()
    {
        if (!_restartArmed)
        {
            ArmRestart();
            return;
        }

        _restartDisarm.Stop();
        _restart.IsEnabled = false;
        _restartLine.Text = "Restarting…";
        _restartLine.Foreground = Ui.T.TextFaintBrush;

        // The fresh copy is started first and waits for this one to go; if it could not be started
        // at all, nothing here has been stopped and the client carries on reporting.
        if (await _actions.RestartAsync())
            return;

        DisarmRestart();
        _restart.IsEnabled = true;
        _restartLine.Text = "Could not start another copy of Modbot.";
        _restartLine.Foreground = Ui.T.DangerBrush;
    }

    private void ArmRestart()
    {
        _restartArmed = true;
        _restart.Content = Ui.Text("Restart now", Ui.T.Density.TextSmall, Ui.T.TextBrush, FontWeight.Medium, wrap: false);
        _restartLine.Text = "";
        _restartDisarm.Stop();
        _restartDisarm.Start();
    }

    private void DisarmRestart()
    {
        _restartArmed = false;
        _restartDisarm.Stop();
        _restart.Content = Ui.Text("Restart Modbot Companion", Ui.T.Density.TextSmall, Ui.T.TextBrush, FontWeight.Medium, wrap: false);
    }
}
