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
/// one download of the speech model, and the pop-up chat each dictation opens.
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

    /// <summary>Dictate and Feedback take turns on the one microphone.</summary>
    private readonly MicrophoneTurns _microphoneTurns = new();

    private readonly Lock _speechModelGate = new();
    private readonly List<IProgress<double>> _speechModelWatchers = [];
    private Task<string?>? _speechModelFetch;

    private ChatAvailability? _chatAvailability;
    private volatile ServerPairing? _chatPairing;
    private readonly DispatcherTimer _chatAsk = new() { Interval = ChatAskEvery };
    private readonly CancellationTokenSource _chatStop = new();

    /// <summary>Redraws the button ten times a second while listening, so the level follows the voice.</summary>
    private readonly DispatcherTimer _dictateLevel = new() { Interval = TimeSpan.FromMilliseconds(100) };

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

        return screen.WithDictate(controller.State, controller.Percent, controller.Level, controller.Notice);
    }

    /// <summary>The desktop overlay's Dictate key and button: a tap, only while the button would be offered.</summary>
    private void ToggleDictateFromDesktop()
    {
        if (DictateAvailable)
            Dictation.Toggle();
    }

    /// <summary>
    /// Builds the chat client, the list of pop-up chats and the question "is AI Chat on?", and asks
    /// it. Once, after the saved pairings are connected.
    /// </summary>
    private void StartChat()
    {
        var client = new HttpChatClient(_http!);

        _chatAvailability = new ChatAvailability(client, () => _chatPairing);

        // Every pop-up chat gets a conversation of its own.
        _chatPopups = new ChatPopups(() => new ChatSession(client, () => _chatPairing, LogDictation));
        _chatPopups.Closed += ChatPopupClosed;

        // It can change on a worker thread, so what it draws is handed to the window's thread.
        _chatAvailability.Changed += () => Dispatcher.UIThread.Post(ChatAvailabilityChanged);

        StartFeedback();

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
        // Feedback asks at the same times: at start, on the slow timer and when the pairing changes.
        RefreshFeedbackAvailability();

        if (_chatAvailability is not { } availability)
            return;

        _ = CrashGuard.RunAsync("asking whether AI Chat is on", () => availability.RefreshAsync(_chatStop.Token));
    }

    private void ChatAvailabilityChanged()
    {
        // Dictating while the button goes away would still send what was said.
        if (!DictateAvailable)
        {
            _dictation?.Abandon();

            foreach (var panels in _chatPanelList)
                panels.Input?.Dictation.Abandon();
        }

        ShowButton();

        // The chats' microphone buttons come and go with it.
        foreach (var panels in _chatPanelList)
            ShowChat(panels.Popup);
    }

    /// <summary>Stops asking, and closes every pop-up chat.</summary>
    private void StopChat()
    {
        _chatAsk.Stop();
        _chatStop.Cancel();
        _chatPopups?.CloseAll();
    }

    /// <summary>
    /// What a tap on Dictate drives: <c>Toggle</c> for the tap, <c>State</c> and <c>Percent</c> for
    /// what the button shows, <c>Changed</c> to redraw and <c>Finished</c> with the text. Raised on
    /// whichever thread caused them, so the button's wiring hands them to the window's thread.
    /// </summary>
    internal DictationController Dictation => _dictation ??= MakeDictation();

    /// <summary>
    /// The one microphone and speech engine Dictate and Feedback share, built the first time either is
    /// asked for. Each gets its own turn on it from <see cref="_microphoneTurns"/>.
    /// </summary>
    private DictationListening SpeechListening()
        => _dictationListening ??= new DictationListening(
            DictationModel.Default, DictationModel.DictationFolder(_directory), DictationMicrophone, LogDictation);

    /// <summary>A button's own controller on the shared microphone, taking its turn under <paramref name="turnName"/>.</summary>
    private DictationController MakeSpeechController(string turnName)
    {
        var model = DictationModel.Default;
        var folder = DictationModel.DictationFolder(_directory);

        var listening = SpeechListening();

        return new DictationController(
            _microphoneTurns.Wrap(listening, turnName),
            listening,
            () => model.IsPresent(folder),
            progress => PrepareSpeechModel(model, folder, progress),
            LogDictation);
    }

    private DictationController MakeDictation()
    {
        var controller = MakeSpeechController("Dictate");

        // The button follows the controller. It can change on a worker thread, so the redraw is
        // handed to the window's thread. The text it finishes with goes to the chat panel.
        controller.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (controller.State is DictationState.Listening)
                _dictateLevel.Start();

            ShowButton();
        });

        _dictateLevel.Tick += (_, _) =>
        {
            if (controller.State is not DictationState.Listening)
                _dictateLevel.Stop();

            ShowButton();
        };

        controller.Finished += text => Dispatcher.UIThread.Post(() => AskChat(text));

        return controller;
    }

    /// <summary>
    /// The saved microphone for listening if one is picked and plugged in, otherwise null, which is
    /// the Windows default. Asked at every start, so a microphone plugged in since is found.
    /// </summary>
    private Microphone? DictationMicrophone()
    {
        var wanted = _state?.Settings.Listening.MicrophoneId;
        if (string.IsNullOrWhiteSpace(wanted))
            return null;

        return MicrophoneChoice.Resolve(wanted, PhraseListening.Microphones()).Microphone;
    }

    /// <summary>
    /// Fetches the speech model for whichever of Dictate and Feedback asked first; the other, asking
    /// while it is coming down, waits for the same download and shows the same progress instead of
    /// starting a second one. Answers as <see cref="PrepareDictationModel"/> does.
    /// </summary>
    private Task<string?> PrepareSpeechModel(DictationModel model, string folder, IProgress<double> progress)
    {
        lock (_speechModelGate)
        {
            _speechModelWatchers.Add(progress);

            // Run on a worker so a download that ends at once cannot clear itself before it is stored.
            return _speechModelFetch ??= Task.Run(() => FetchSpeechModel(model, folder));
        }
    }

    private async Task<string?> FetchSpeechModel(DictationModel model, string folder)
    {
        try
        {
            return await PrepareDictationModel(
                    model,
                    folder,
                    new Progress<double>(fraction =>
                    {
                        IProgress<double>[] watching;

                        lock (_speechModelGate)
                            watching = [.. _speechModelWatchers];

                        foreach (var watcher in watching)
                            watcher.Report(fraction);
                    }))
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_speechModelGate)
            {
                _speechModelFetch = null;
                _speechModelWatchers.Clear();
            }
        }
    }

    /// <summary>
    /// The one download of the speech model, started only because somebody tapped Dictate or Feedback. Answers
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
        _dictateLevel.Stop();
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
