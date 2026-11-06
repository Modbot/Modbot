using Avalonia.Threading;
using Modbot.Companion.App.Dictation;
using Modbot.Companion.App.Listening;
using Modbot.Companion.Dictation;
using Modbot.Companion.Listening;
using Modbot.Companion.Startup;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// Owns Dictate: the controller behind the button, its microphone and its speech engine, and the
/// one download of the speech model.
/// </summary>
/// <remarks>
/// <para>Nothing here runs until somebody taps Dictate. The controller is built the first time it
/// is asked for, which opens no microphone and loads no model; a tap does that, and the next tap
/// or the one-minute limit lets go of it again.</para>
/// <para>What leaves the machine: the one download of the speech model, on the first tap, from the
/// address pinned in <see cref="DictationModel"/> (see <see cref="DictationDownload"/>). A test
/// copy makes no download. Nothing a microphone hears is ever sent.</para>
/// </remarks>
internal sealed partial class CompanionHost
{
    /// <summary>Set to 1 to show Dictate. The AI chat that will decide this for real is not in the client yet.</summary>
    private const string DictateVariable = "MODBOT_DICTATE";

    private DictationListening? _dictationListening;
    private DictationController? _dictation;

    /// <summary>
    /// Whether the Dictate button is offered at all. Off unless <c>MODBOT_DICTATE=1</c>, read once
    /// per call; the chat availability that replaces this is added with the chat panel.
    /// </summary>
    internal static bool DictateAvailable
        => string.Equals(Environment.GetEnvironmentVariable(DictateVariable), "1", StringComparison.Ordinal);

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
        // handed to the window's thread. The text it finishes with is for the chat panel, which
        // arrives later; until then the button just goes back to idle.
        controller.Changed += () => Dispatcher.UIThread.Post(ShowButton);

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
