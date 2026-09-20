using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Modbot.Companion.Listening;

namespace Modbot.Companion.App;

/// <summary>
/// The Settings page's Listening card: saying the client's name, and then what you want.
/// </summary>
/// <remarks>
/// <para>The switch is the whole of the consent, and it is the biggest switch in this client: off
/// means no microphone is ever opened. Off is what a fresh install has, off is what an updated
/// install has, and nothing is listened to until somebody moves it — see the listening design spec,
/// §4, and <see cref="Modbot.Companion.App.Listening.PhraseListening"/> for what happens when they
/// do.</para>
/// <para>While the microphone is open the window says so at the top of every page, not only here,
/// because somebody who is looking at the Events list should not have to come back to this card to
/// find out. In the few seconds after the name has been heard it says that instead, and stops
/// saying it the moment the client would stop acting on anything.</para>
/// <para>Built once, like the Voice and Clips cards: the window redraws on a timer and a control
/// being pressed dies under a rebuild.</para>
/// </remarks>
public sealed partial class MainWindow
{
    private readonly CheckBox _listeningOn = new();
    private readonly TextBlock _listeningLine = Ui.Faint("");
    private readonly TextBlock _listeningProblem = Ui.Faint("");

    /// <summary>
    /// Which microphone to listen on. Built once, like the Voice card's list: the window redraws
    /// on a timer and a list being open dies under a rebuild.
    /// </summary>
    private readonly ComboBox _listeningMicrophone = new();
    private readonly List<string?> _listeningMicrophoneIds = [];

    /// <summary>Wires the Listening card. Called once, from the constructor.</summary>
    private void SetUpListening()
    {
        _listeningOn.IsCheckedChanged += (_, _) => ListeningChanged();
        _listeningProblem.Foreground = Ui.T.DangerBrush;

        _listeningMicrophone.Height = Ui.T.Density.ControlHeight;
        _listeningMicrophone.MinWidth = 260;
        _listeningMicrophone.FontSize = Ui.T.Density.TextSmall;
        _listeningMicrophone.FontFamily = Ui.Sans;
        _listeningMicrophone.Background = Ui.T.BackgroundBrush;
        _listeningMicrophone.Foreground = Ui.T.TextBrush;
        _listeningMicrophone.BorderBrush = Ui.T.Border2Brush;
        _listeningMicrophone.BorderThickness = new Thickness(Ui.T.Density.Hairline);
        _listeningMicrophone.CornerRadius = new CornerRadius(Ui.T.Density.Radius);
        _listeningMicrophone.SelectionChanged += (_, _) => ListeningChanged();
    }

    /// <summary>What the Listening card's switch says right now, as one settings record.</summary>
    private void ListeningChanged()
    {
        if (_renderingSwitches)
            return;

        var index = _listeningMicrophone.SelectedIndex;
        var microphone = index >= 0 && index < _listeningMicrophoneIds.Count
            ? _listeningMicrophoneIds[index]
            : null;

        _actions.SetListening(_snapshot.ListeningOrNone.Settings with
        {
            On = _listeningOn.IsChecked == true,
            MicrophoneId = microphone,
        });
    }

    /// <summary>Puts the snapshot into the card's controls without any of them answering back.</summary>
    private void RefreshListeningControls(ListeningStatus listening)
    {
        _listeningOn.IsChecked = listening.Settings.On;

        var ids = new List<string?> { null };
        var names = new List<string> { OperatingSystem.IsWindows() ? "Windows default" : "System default" };
        foreach (var microphone in listening.MicrophonesOrNone)
        {
            ids.Add(microphone.Id);
            names.Add(microphone.Name);
        }

        // A picked microphone that is not plugged in right now still has to be selectable, or the
        // list would silently show the default while the file says otherwise.
        if (listening.Settings.MicrophoneId is { } wanted && !ids.Contains(wanted))
        {
            ids.Add(wanted);
            names.Add("Not connected");
        }

        if (!_listeningMicrophone.IsDropDownOpen && !ids.SequenceEqual(_listeningMicrophoneIds))
        {
            _listeningMicrophoneIds.Clear();
            _listeningMicrophoneIds.AddRange(ids);
            _listeningMicrophone.ItemsSource = names;
        }

        if (!_listeningMicrophone.IsDropDownOpen)
        {
            _listeningMicrophone.SelectedIndex =
                Math.Max(0, _listeningMicrophoneIds.IndexOf(listening.Settings.MicrophoneId));
        }
    }

    /// <summary>The Listening card: one switch, and what it is doing.</summary>
    private Control ListeningCard()
    {
        foreach (var control in new Control[] { _listeningOn, _listeningLine, _listeningMicrophone, _listeningProblem })
            DetachFromParent(control);

        var listening = _snapshot.ListeningOrNone;

        // The label names the control and says the word out loud, because a control called "Listen
        // for a phrase" would leave somebody guessing which phrase. Its own name is the whole of
        // what it listens for until that name is heard, so that is what the switch is called.
        _listeningOn.Content = Ui.Text(
            $"Listen for “{listening.Called}”", Ui.T.Density.TextSmall, Ui.T.TextBrush);

        // A machine that cannot listen says so and its switch does nothing, rather than reading
        // "Off" like a choice somebody made.
        _listeningOn.IsEnabled = listening.Supported;
        _listeningMicrophone.IsEnabled = listening.Supported;

        _listeningLine.Text = Describe(listening);

        // A label names a control and an error says what failed; this card's sentences are why it
        // cannot listen here at all, that a picked microphone is not plugged in, and the last
        // thing that went wrong.
        _listeningProblem.Text = listening.Unsupported ?? Missing(listening) ?? listening.LastProblem ?? "";
        _listeningProblem.IsVisible = _listeningProblem.Text.Length > 0;

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { _listeningOn, _listeningLine },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { Ui.Label("Microphone"), _listeningMicrophone },
                },
                _listeningProblem,
            },
        };
    }

    /// <summary>
    /// What the card says when a picked microphone is not plugged in, or null.
    /// </summary>
    private static string? Missing(ListeningStatus listening)
        => listening.MicrophoneMissing ? MicrophoneChoice.NotConnected : null;

    /// <summary>The one word beside the switch: what the listener is doing, and nothing more.</summary>
    /// <remarks>
    /// "Listening for what to do" is only ever said while the client would actually act on one,
    /// because the snapshot works that out from the same clock the wait itself is worked out from.
    /// A screen that said it a second late would be a screen a moderator learns not to believe.
    /// </remarks>
    private static string Describe(ListeningStatus listening) => listening.State switch
    {
        ListeningState.Off => "Off",
        ListeningState.NotOnThisMachine => "Not available on this machine",
        ListeningState.Getting =>
            $"Downloading {listening.ModelBytes / (1024.0 * 1024):N0} MB — {listening.Progress:P0}",
        ListeningState.NoModel => "Not downloaded",
        ListeningState.Waiting => "Waiting for VRChat",
        ListeningState.NoMicrophone => "Waiting for the microphone",
        _ when listening.IsWaitingForCommand => "Listening for what to do",
        _ => listening.LastHeard is { } heard ? $"Listening. Last heard: {heard}" : "Listening",
    };
}
