using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Modbot.Core;

namespace Modbot.Companion.App;

/// <summary>
/// The sidebar's foot: which version this is, and a newer one when the updater has found it.
/// </summary>
/// <remarks>
/// <para>The version is always there, on every page, because it is the first thing anybody
/// helping a moderator asks. A newer version shows while it downloads, and once it has
/// downloaded a button installs it by restarting. Until somebody presses that, it installs the
/// next time Modbot starts, as it always has (update checking design, §4).</para>
/// <para>The button asks twice, like the Restart card: the first press turns it into
/// <b>Restart now</b> for five seconds. A restart stops reporting for a moment, and a stray click
/// on the edge of the window should not be what does that.</para>
/// <para>Built once and filled in on every tick that changes anything, like the nav rows.</para>
/// </remarks>
public sealed partial class MainWindow
{
    private const string RestartToUpdate = "Restart to update";

    private readonly TextBlock _versionLine = Ui.Faint($"Version {ModbotVersion.Release}");
    private readonly TextBlock _updateLine = Ui.Faint("");
    private readonly Button _updateRestart = Ui.Button(RestartToUpdate, primary: true);
    private readonly DispatcherTimer _updateRestartDisarm = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _updateRestartArmed;

    /// <summary>Set when a restart could not start a new copy, so the next tick does not hide why.</summary>
    private bool _updateRestartFailed;

    private readonly Button _checkUpdates = Ui.Button("Check for updates");
    private readonly TextBlock _checkLine = Ui.Faint("");

    /// <summary>
    /// How long what a check came to stays under the button. "Up to date" is true when it is said
    /// and stops being worth believing an hour later, so it does not stay.
    /// </summary>
    private readonly DispatcherTimer _checkLineClear = new() { Interval = TimeSpan.FromSeconds(10) };

    /// <summary>Wires the foot's buttons. Called once, from the constructor.</summary>
    private void SetUpVersion()
    {
        _updateRestart.Margin = new Thickness(0, 4, 0, 0);
        _updateRestart.Click += async (_, _) => await CrashGuard.RunAsync("restarting to update Modbot", UpdateRestartPressedAsync);
        _updateRestartDisarm.Tick += (_, _) => DisarmUpdateRestart();

        _checkUpdates.Margin = new Thickness(0, 4, 0, 0);
        _checkUpdates.Click += async (_, _) => await CrashGuard.RunAsync("checking for updates", CheckForUpdatesPressedAsync);
        _checkLine.IsVisible = false;
        _checkLineClear.Tick += (_, _) =>
        {
            _checkLineClear.Stop();
            _checkLine.IsVisible = false;
        };
    }

    /// <summary>
    /// One check now, so a moderator who has heard of a new version does not have to restart
    /// Modbot to make it look. What it finds is downloaded, and the foot then offers the restart
    /// that installs it.
    /// </summary>
    private async Task CheckForUpdatesPressedAsync()
    {
        _checkUpdates.IsEnabled = false;
        _checkLineClear.Stop();
        ShowCheckLine("Checking…", Ui.T.TextFaintBrush);

        try
        {
            var outcome = await _actions.CheckForUpdatesAsync();
            switch (outcome)
            {
                case UpdateCheckOutcome.UpToDate:
                    ShowCheckLine("Up to date", Ui.T.TextFaintBrush);
                    _checkLineClear.Start();
                    break;
                case UpdateCheckOutcome.Failed:
                    ShowCheckLine("Could not check for updates", Ui.T.DangerBrush);
                    _checkLineClear.Start();
                    break;
                default:
                    // Downloaded: the line above says so and the restart button is up. Already
                    // checking: that check's answer arrives the same way.
                    _checkLine.IsVisible = false;
                    break;
            }
        }
        finally
        {
            _checkUpdates.IsEnabled = true;
        }
    }

    private void ShowCheckLine(string text, Avalonia.Media.IBrush colour)
    {
        _checkLine.Text = text;
        _checkLine.Foreground = colour;
        _checkLine.IsVisible = true;
    }

    /// <summary>The version, and what the updater has found, put into the foot's kept controls.</summary>
    private void RenderVersion()
    {
        var (line, ready) = (_snapshot.UpdateReady, _snapshot.UpdateFound) switch
        {
            ({ } downloaded, _) => ($"Update {downloaded} is ready", true),
            (null, { } found) => ($"Downloading update {found}", false),
            _ => ("", false),
        };

        _updateRestart.IsVisible = ready;

        // Offered only by a copy that updates itself, and not while a newer version is already
        // downloading or waiting: then the thing to press is the restart.
        _checkUpdates.IsVisible = _snapshot.CanCheckForUpdates && !ready && _snapshot.UpdateFound is null;

        if (_updateRestartFailed)
            return;

        _updateLine.Text = line;
        _updateLine.IsVisible = line.Length > 0;
    }

    /// <summary>The foot under the brand row: version, update line, and the button.</summary>
    private Control VersionFoot() => new StackPanel
    {
        Spacing = 2,
        Margin = new Thickness(0, 6, 0, 0),
        Children = { _versionLine, _updateLine, _updateRestart, _checkUpdates, _checkLine },
    };

    private async Task UpdateRestartPressedAsync()
    {
        if (!_updateRestartArmed)
        {
            _updateRestartFailed = false;
            _updateLine.Foreground = Ui.T.TextFaintBrush;
            _updateRestartArmed = true;
            SetUpdateRestartCaption("Restart now");
            _updateRestartDisarm.Stop();
            _updateRestartDisarm.Start();
            return;
        }

        _updateRestartDisarm.Stop();
        _updateRestart.IsEnabled = false;
        _updateLine.Text = "Restarting…";

        // The fresh copy is started first and installs the update before it opens; if it could not
        // be started at all, nothing here has been stopped and the client carries on reporting.
        if (await _actions.RestartAsync())
            return;

        DisarmUpdateRestart();
        _updateRestart.IsEnabled = true;
        _updateRestartFailed = true;
        _updateLine.Text = "Could not start another copy of Modbot.";
        _updateLine.Foreground = Ui.T.DangerBrush;
    }

    private void DisarmUpdateRestart()
    {
        _updateRestartArmed = false;
        _updateRestartDisarm.Stop();
        SetUpdateRestartCaption(RestartToUpdate);
    }

    private void SetUpdateRestartCaption(string caption)
        => _updateRestart.Content = Ui.Text(
            caption, Ui.T.Density.TextSmall, Ui.T.AccentForegroundBrush, FontWeight.Medium, wrap: false);
}
