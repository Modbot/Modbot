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

    /// <summary>Wires the Listening card. Called once, from the constructor.</summary>
    private void SetUpListening()
    {
        _listeningOn.IsCheckedChanged += (_, _) => ListeningChanged();
        _listeningProblem.Foreground = Ui.T.DangerBrush;
    }

    /// <summary>What the Listening card's switch says right now, as one settings record.</summary>
    private void ListeningChanged()
    {
        if (_renderingSwitches)
            return;

        _actions.SetListening(_snapshot.ListeningOrNone.Settings with { On = _listeningOn.IsChecked == true });
    }

    /// <summary>Puts the snapshot into the card's controls without any of them answering back.</summary>
    private void RefreshListeningControls(ListeningStatus listening)
    {
        _listeningOn.IsChecked = listening.Settings.On;
    }

    /// <summary>The Listening card: one switch, and what it is doing.</summary>
    private Control ListeningCard()
    {
        foreach (var control in new Control[] { _listeningOn, _listeningLine, _listeningProblem })
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

        _listeningLine.Text = Describe(listening);

        // A label names a control and an error says what failed; this card's sentences are why it
        // cannot listen here at all, and the last thing that went wrong.
        _listeningProblem.Text = listening.Unsupported ?? listening.LastProblem ?? "";
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
                _listeningProblem,
            },
        };
    }

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
