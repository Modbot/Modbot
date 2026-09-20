using Avalonia.Controls;
using Avalonia.Layout;
using Modbot.Companion.Clips;

namespace Modbot.Companion.App;

/// <summary>
/// The Settings page's Clips card: keeping the last few minutes and their sound, and saving them.
/// </summary>
/// <remarks>
/// <para>The switch is the whole of the consent. Off is what a fresh install has, off is what an
/// updated install has, and nothing is recorded until somebody moves it — see the clips design
/// spec, §2, and <see cref="ScreenRecording"/> for what is recorded when they do.</para>
/// <para>The switch says <em>with VRChat's sound</em> because that is what it does. Since
/// 2026-09-19 a clip carries what people said in the instance, which is a different thing to keep
/// on somebody's disk from a silent picture, and the one place a moderator decides about it is
/// this line of text. Discord's sound is the second box and is off until somebody ticks it; there
/// is no third box, and nothing else the machine is playing can be recorded at all.</para>
/// <para>Built once, like the Voice and Notifications cards: the window redraws on a timer and a
/// slider being dragged or a folder being typed into dies under a rebuild.</para>
/// </remarks>
public sealed partial class MainWindow
{
    private readonly CheckBox _clipsOn = new();
    private readonly CheckBox _clipsDiscordSound = new();
    private readonly Slider _clipsMinutes = new();
    private readonly TextBlock _clipsMinutesValue = Ui.Text("", Ui.T.Density.TextSmall, Ui.T.TextDimBrush, wrap: false, mono: true);
    private readonly TextBox _clipsFolderBox = Ui.Input();
    private readonly Button _clipsFolderSave = Ui.Button("Save");
    private readonly Button _clipsFolderReset = Ui.Button("Use the usual folder");
    private readonly Button _clipsSave = Ui.Button("Save a clip", primary: true);
    private readonly TextBlock _clipsLine = Ui.Faint("");
    private readonly TextBlock _clipsProblem = Ui.Faint("");

    /// <summary>Wires the Clips card. Called once, from the constructor.</summary>
    private void SetUpClips()
    {
        // The switch names what it does, sound included. A moderator turning this on is turning on
        // a recording of what people say in the instance, and finding that out afterwards would be
        // the wrong way round.
        _clipsOn.Content = Ui.Text("Keep the last few minutes, with VRChat's sound", Ui.T.Density.TextSmall, Ui.T.TextBrush);
        _clipsOn.IsCheckedChanged += (_, _) => ClipsChanged();

        _clipsDiscordSound.Content = Ui.Text("Discord's sound too", Ui.T.Density.TextSmall, Ui.T.TextBrush);
        _clipsDiscordSound.IsCheckedChanged += (_, _) => ClipsChanged();

        _clipsMinutesValue.VerticalAlignment = VerticalAlignment.Center;
        _clipsMinutesValue.Width = 32;

        _clipsMinutes.Minimum = ClipSettings.MinMinutes;
        _clipsMinutes.Maximum = ClipSettings.MaxMinutes;
        _clipsMinutes.TickFrequency = 1;
        _clipsMinutes.IsSnapToTickEnabled = true;
        _clipsMinutes.Width = 220;
        _clipsMinutes.VerticalAlignment = VerticalAlignment.Center;
        _clipsMinutes.ValueChanged += (_, _) =>
        {
            _clipsMinutesValue.Text = $"{(int)_clipsMinutes.Value}";
            ClipsChanged();
        };

        _clipsFolderBox.FontFamily = Ui.Mono;

        _clipsFolderSave.Click += (_, _) => _actions.SetClips(
            _snapshot.ClipsOrNone.Settings with { Folder = _clipsFolderBox.Text });

        _clipsFolderReset.Click += (_, _) => _actions.SetClips(
            _snapshot.ClipsOrNone.Settings with { Folder = null });

        _clipsSave.Click += (_, _) => _actions.SaveClip();

        _clipsProblem.Foreground = Ui.T.DangerBrush;
    }

    /// <summary>What the Clips card's switch and slider say right now, as one settings record.</summary>
    private void ClipsChanged()
    {
        if (_renderingSwitches)
            return;

        _actions.SetClips(_snapshot.ClipsOrNone.Settings with
        {
            On = _clipsOn.IsChecked == true,
            Minutes = (int)_clipsMinutes.Value,
            DiscordSound = _clipsDiscordSound.IsChecked == true,
        });
    }

    /// <summary>Puts the snapshot into the card's controls without any of them answering back.</summary>
    private void RefreshClipControls(ClipsStatus clips)
    {
        _clipsOn.IsChecked = clips.Settings.On;
        _clipsDiscordSound.IsChecked = clips.Settings.DiscordSound;

        if (!_clipsMinutes.IsPointerOver && !_clipsMinutes.IsFocused)
            _clipsMinutes.Value = ClipSettings.ClampMinutes(clips.Settings.Minutes);

        _clipsMinutesValue.Text = $"{(int)_clipsMinutes.Value}";

        // Only refilled while nobody is typing in it: the window redraws on a timer.
        if (!_clipsFolderBox.IsFocused)
        {
            _clipsFolderBox.Text = clips.Settings.Folder ?? "";
            _clipsFolderBox.Watermark = clips.Folder.Path;
        }
    }

    /// <summary>The Clips card: the switch, how many minutes, where clips go, and Save a clip.</summary>
    private Control ClipsCard()
    {
        foreach (var control in new Control[]
        {
            _clipsOn, _clipsDiscordSound, _clipsMinutes, _clipsMinutesValue, _clipsFolderBox,
            _clipsFolderSave, _clipsFolderReset, _clipsSave, _clipsLine, _clipsProblem,
        })
        {
            DetachFromParent(control);
        }

        var clips = _snapshot.ClipsOrNone;

        _clipsSave.IsEnabled = clips.CanSave;
        _clipsLine.Text = Describe(clips);

        // A machine that cannot record says so and its switch does nothing, rather than reading
        // "Off" like a choice somebody made.
        _clipsOn.IsEnabled = clips.Supported;
        _clipsDiscordSound.IsEnabled = clips.Supported;
        _clipsMinutes.IsEnabled = clips.Supported;
        _clipsFolderBox.IsEnabled = clips.Supported;

        // A label names a control and an error says what failed; this card's sentences are the
        // folder's problem, the last thing that went wrong, and why it cannot record here at all.
        _clipsProblem.Text = clips.Unsupported ?? clips.Folder.Problem ?? clips.LastProblem ?? "";
        _clipsProblem.IsVisible = _clipsProblem.Text.Length > 0;

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                _clipsOn,
                _clipsDiscordSound,
                Ui.Field("Minutes", new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _clipsMinutes, _clipsMinutesValue },
                }),
                Ui.Field("Folder", new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        _clipsFolderBox,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children = { _clipsFolderSave, _clipsFolderReset },
                        },
                    },
                }),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { _clipsSave, _clipsLine },
                },
                _clipsProblem,
            },
        };
    }

    /// <summary>The one word beside the button: what the recorder is doing, and nothing more.</summary>
    private static string Describe(ClipsStatus clips) => clips.State switch
    {
        ClipRecordingState.Off => "Off",
        ClipRecordingState.Waiting => "Waiting for VRChat",
        ClipRecordingState.Recording => clips.LastSaved is { } saved
            ? $"Recording. Last clip: {saved}"
            : "Recording",
        ClipRecordingState.NoWindow => "Waiting for VRChat's window",
        ClipRecordingState.NothingRecordedYet => "Nothing recorded yet",
        ClipRecordingState.NotOnThisMachine => "Not available on this machine",
        ClipRecordingState.FolderUnusable => "The folder cannot be used",
        _ => "Stopped",
    };
}
