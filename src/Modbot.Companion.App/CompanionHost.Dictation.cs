using Avalonia.Threading;
using Modbot.Companion.App.Dictation;
using Modbot.Companion.App.Listening;
using Modbot.Companion.Chat;
using Modbot.Companion.Dictation;
using Modbot.Companion.Ingest;
using Modbot.Companion.Listening;
using Modbot.Companion.Startup;
using Modbot.Overlay.Views;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// Owns Dictate: the controller behind the button, its microphone and its speech engine, the
/// one download of the speech model, and the chat panel the text goes to.
/// </summary>
/// <remarks>
/// <para>Nothing here runs until somebody taps Dictate. The controller is built the first time it
/// is asked for, which opens no microphone and loads no model; a tap does that, and the next tap
/// or the one-minute limit lets go of it again.</para>
/// <para>What leaves the machine: the one download of the speech model, on the first tap, from the
/// address pinned in <see cref="DictationModel"/> (see <see cref="DictationDownload"/>). A test
/// copy makes no download. Nothing a microphone hears is ever sent: the words it was turned into
/// go to the paired server's AI Chat as a typed message would (<see cref="HttpChatClient"/>), and
/// no sound goes anywhere.</para>
/// <para>The button is offered only while AI Chat is on for the paired server and this device's
/// owner may use it (<see cref="ChatAvailability"/>), asked at start, every few minutes and when
/// the pairing changes.</para>
/// </remarks>
internal sealed partial class CompanionHost
{
    /// <summary>How often the server is asked again whether AI Chat is on.</summary>
    private static readonly TimeSpan ChatAskEvery = TimeSpan.FromMinutes(5);

    private DictationListening? _dictationListening;
    private DictationController? _dictation;

    private ChatAvailability? _chatAvailability;
    private ChatSession? _chatSession;
    private volatile ServerPairing? _chatPairing;
    private readonly DispatcherTimer _chatAsk = new() { Interval = ChatAskEvery };
    private readonly CancellationTokenSource _chatStop = new();

    /// <summary>
    /// Whether the Dictate button is offered at all: AI Chat is on and this device's owner may use it,
    /// as the server last said.
    /// </summary>
    private bool DictateAvailable => _chatAvailability?.Available is true;

    /// <summary>
    /// The button's card with the Dictate row on it while <paramref name="offered"/>, and as it was
    /// otherwise. The controller is asked for only when the row is wanted.
    /// </summary>
    internal static ButtonScreen WithDictateIfOffered(ButtonScreen screen, bool offered, Func<DictationController> dictation)
    {
        if (!offered)
            return screen;

        var controller = dictation();

        return screen.WithDictate(controller.State, controller.Percent);
    }

    /// <summary>
    /// Builds the chat client, the conversation behind the panel and the question "is AI Chat on?",
    /// and asks it. Once, after the saved pairings are connected.
    /// </summary>
    private void StartChat()
    {
        var client = new HttpChatClient(_http!);

        _chatAvailability = new ChatAvailability(client, () => _chatPairing);
        _chatSession = new ChatSession(client, () => _chatPairing);

        // Both can change on a worker thread, so what they draw is handed to the window's thread.
        _chatAvailability.Changed += () => Dispatcher.UIThread.Post(ChatAvailabilityChanged);
        _chatSession.Changed += () => Dispatcher.UIThread.Post(ShowChat);

        _chatAsk.Tick += (_, _) => RefreshChatAvailability();
        _chatAsk.Start();

        ChatPairingChanged();
    }

    /// <summary>The pairing the chat goes to changed (or was just loaded): pick it again and ask the server.</summary>
    private void ChatPairingChanged()
    {
        _chatPairing = _state?.Connections.Select(connection => connection.Pairing).FirstOrDefault();

        RefreshChatAvailability();
    }

    private void RefreshChatAvailability()
    {
        if (_chatAvailability is not { } availability)
            return;

        _ = CrashGuard.RunAsync("asking whether AI Chat is on", () => availability.RefreshAsync(_chatStop.Token));
    }

    private void ChatAvailabilityChanged()
    {
        // Dictating while the button goes away would still send what was said.
        if (!DictateAvailable)
            _dictation?.Abandon();

        ShowButton();
    }

    /// <summary>Shows the chat panel and puts the dictated words into the conversation, the open one if the panel was already up.</summary>
    private void AskChat(string text)
    {
        if (_chatSession is not { } session || _chatHost is not { } host)
        {
            Log.Information("Dictate: the chat panel is not up, so what was said was not sent");
            return;
        }

        host.Show(new ChatScreen(session.Lines));

        _ = CrashGuard.RunAsync("asking AI Chat", () => session.AskAsync(text));
    }

    /// <summary>Draws the conversation on the panel while it is up.</summary>
    private void ShowChat()
    {
        if (_chatSession is { } session && _chatHost is { Hidden: false } host)
            host.Update(new ChatScreen(session.Lines));
    }

    /// <summary>Stops asking, and lets go of the conversation.</summary>
    private void StopChat()
    {
        _chatAsk.Stop();
        _chatStop.Cancel();
        _chatSession?.Close();
    }

    /// <summary>
    /// What a tap on Dictate drives: <c>Toggle</c> for the tap, <c>State</c> and <c>Percent</c> for
    /// what the button shows, <c>Changed</c> to redraw and <c>Finished</c> with the text. Raised on
    /// whichever thread caused them, so the button's wiring hands them to the window's thread.
    /// </summary>
    internal DictationController Dictation => _dictation ??= MakeDictation();

    private DictationController MakeDictation()
    {
        var model = DictationModel.Default;
        var folder = DictationModel.DictationFolder(_directory);

        var listening = new DictationListening(model, folder, DictationMicrophoneId, LogDictation);
        _dictationListening = listening;

        var controller = new DictationController(
            listening,
            listening,
            () => model.IsPresent(folder),
            progress => PrepareDictationModel(model, folder, progress),
            LogDictation);

        // The button follows the controller. It can change on a worker thread, so the redraw is
        // handed to the window's thread. The text it finishes with goes to the chat panel.
        controller.Changed += () => Dispatcher.UIThread.Post(ShowButton);
        controller.Finished += text => Dispatcher.UIThread.Post(() => AskChat(text));

        return controller;
    }

    /// <summary>
    /// The saved microphone for listening if one is picked and plugged in, otherwise null, which is
    /// the Windows default. Asked at every start, so a microphone plugged in since is found.
    /// </summary>
    private string? DictationMicrophoneId()
    {
        var wanted = _state?.Settings.Listening.MicrophoneId;
        if (string.IsNullOrWhiteSpace(wanted))
            return null;

        return MicrophoneChoice.Resolve(wanted, PhraseListening.Microphones()).Microphone?.Id;
    }

    /// <summary>
    /// The one download of the speech model, started only because somebody tapped Dictate. Answers
    /// null when the model is ready, otherwise a sentence saying why not.
    /// </summary>
    private async Task<string?> PrepareDictationModel(DictationModel model, string folder, IProgress<double> progress)
    {
        // A test copy downloads nothing; it says why and the button goes back to idle.
        if (!_data.MayDownload)
        {
            Log.Information("Test copy: the speech model for Dictate is not downloaded");
            return DataFolder.NoDownloadInATestCopy;
        }

        Log.Information(
            "Downloading the speech model {Name} from {Url} ({Size:N0} bytes)", model.Name, model.Url, model.Size);

        var result = await new DictationDownload(_http!).RunAsync(model, folder, progress).ConfigureAwait(false);

        if (result.Ready)
        {
            Log.Information("The speech model for Dictate is ready");
            return null;
        }

        Log.Warning("The speech model download failed: {Problem}", result.Problem);
        return result.Problem ?? "The speech model could not be downloaded.";
    }

    /// <summary>Closes the microphone and drops what it heard. Safe to call when Dictate never started.</summary>
    private void StopDictation()
    {
        _dictation?.Abandon();
        _dictationListening?.Dispose();
    }

    private static void LogDictation(string line, Exception? ex)
    {
        if (ex is null)
            Log.Information("Dictate: {Line}", line);
        else
            Log.Warning(ex, "Dictate: {Line}", line);
    }
}
