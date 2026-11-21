using Avalonia.Threading;
using Modbot.Companion.App.Feedback;
using Modbot.Companion.Dictation;
using Modbot.Companion.Feedback;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// Owns Feedback: the controller behind the button, the question "may this device's owner send
/// feedback?", and the draft window.
/// </summary>
/// <remarks>
/// <para>The microphone, the speech engine and the one download of the speech model are Dictate's
/// (<see cref="SpeechListening"/>, <see cref="PrepareSpeechModel"/>); the two take turns on the
/// microphone (<see cref="MicrophoneTurns"/>), so only one listens at a time.</para>
/// <para>What leaves the machine: the words that were said, as text, to the paired server only
/// (<see cref="HttpFeedbackClient"/>), first to be tidied and then, once the person sends the draft, to
/// be saved. No sound goes anywhere, and nothing goes to Modbot Cloud or GitHub.</para>
/// <para>The button is offered while the paired server says this device's owner may send feedback
/// (<see cref="FeedbackAvailability"/>), whether or not AI Chat is on. It is asked at the same times as
/// AI Chat: at start, every few minutes and when the pairing changes.</para>
/// </remarks>
internal sealed partial class CompanionHost
{
    private HttpFeedbackClient? _feedbackClient;
    private FeedbackAvailability? _feedbackAvailability;
    private FeedbackController? _feedback;
    private FeedbackDraftWindow? _feedbackDraft;

    /// <summary>Redraws the button ten times a second while listening, so the level follows the voice.</summary>
    private readonly DispatcherTimer _feedbackLevel = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>Whether the Feedback button is offered at all, as the server last said.</summary>
    private bool FeedbackAvailable => _feedbackAvailability?.Available is true;

    /// <summary>What a tap on Feedback drives. Built the first time it is asked for, which opens no microphone.</summary>
    internal FeedbackController Feedback => _feedback ??= MakeFeedback();

    /// <summary>What the Feedback button shows: nothing while it is not offered.</summary>
    private FeedbackCard FeedbackCardNow() => FeedbackAvailable ? FeedbackCard.Of(Feedback) : FeedbackCard.Off;

    /// <summary>Builds the client and the question "may this device's owner send feedback?". Once, with the chat.</summary>
    private void StartFeedback()
    {
        _feedbackClient = new HttpFeedbackClient(_http!);
        _feedbackAvailability = new FeedbackAvailability(_feedbackClient, () => _chatPairing);

        // It can change on a worker thread, so what it draws is handed to the window's thread.
        _feedbackAvailability.Changed += () => Dispatcher.UIThread.Post(FeedbackAvailabilityChanged);
    }

    private void RefreshFeedbackAvailability()
    {
        if (_feedbackAvailability is not { } availability)
            return;

        _ = CrashGuard.RunAsync("asking whether feedback may be sent", () => availability.RefreshAsync(_chatStop.Token));
    }

    private void FeedbackAvailabilityChanged()
    {
        // Speaking or editing while the button goes away would still send what was said.
        if (!FeedbackAvailable)
            _feedback?.Abandon();

        ShowButton();
    }

    /// <summary>The Feedback button on the main window: a tap, only while the button is offered.</summary>
    private void ToggleFeedback()
    {
        if (FeedbackAvailable)
            Feedback.Toggle();
    }

    /// <summary>Hands the main window what its Feedback button should say.</summary>
    private void ShowMainFeedback() => Window.SetFeedback(FeedbackCardNow());

    private FeedbackController MakeFeedback()
    {
        var model = DictationModel.Default;
        var folder = DictationModel.DictationFolder(_directory);

        var listening = SpeechListening();

        var controller = new FeedbackController(
            _microphoneTurns.Wrap(listening, "Feedback"),
            listening,
            () => model.IsPresent(folder),
            progress => PrepareSpeechModel(model, folder, progress),
            _feedbackClient!,
            () => _chatPairing,
            LogFeedback);

        // The button follows the controller. It can change on a worker thread, so the redraw is
        // handed to the window's thread.
        controller.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (controller.State is FeedbackState.Listening)
                _feedbackLevel.Start();

            ShowButton();
            FollowDraft(controller);
        });

        _feedbackLevel.Tick += (_, _) =>
        {
            if (controller.State is not FeedbackState.Listening)
                _feedbackLevel.Stop();

            ShowButton();
        };

        return controller;
    }

    /// <summary>Opens the draft window on <paramref name="draft"/>. Nothing is saved until Send.</summary>
    private void ShowFeedbackDraft(FeedbackController controller, FeedbackDraft draft)
    {
        // The button went away while the words were being tidied: nothing is offered, nothing is kept.
        if (!FeedbackAvailable)
        {
            controller.Abandon();
            return;
        }

        var window = new FeedbackDraftWindow(
            draft,
            controller.Cancel,
            (kind, title, details) => _ = CrashGuard.RunAsync("sending feedback", () => controller.SendAsync(kind, title, details)));

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_feedbackDraft, window))
                _feedbackDraft = null;
        };

        _feedbackDraft = window;

        try
        {
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            LogFeedback("the draft window could not be shown", ex);
            controller.Cancel();
        }
    }

    /// <summary>Keeps the draft window to what the controller says: its reason, locked while sending, closed once the draft is over.</summary>
    private void FollowDraft(FeedbackController controller)
    {
        if (_feedbackDraft is not { } window)
        {
            // A draft is ready and has no window yet (or its window was closed while it was being sent
            // and the send failed): the window opens on it.
            if (controller.State is FeedbackState.Draft && controller.Draft is { } kept)
                ShowFeedbackDraft(controller, kept);

            return;
        }

        if (controller.State is FeedbackState.Draft or FeedbackState.Sending)
        {
            window.ShowProblem(controller.Problem);
            window.SetSending(controller.State is FeedbackState.Sending);
            return;
        }

        _feedbackDraft = null;
        window.CloseFromHost();
    }

    /// <summary>Closes the microphone and the draft window and drops what was heard. Safe to call when Feedback never started.</summary>
    private void StopFeedback()
    {
        _feedbackLevel.Stop();
        _feedback?.Abandon();

        _feedbackDraft?.CloseFromHost();
        _feedbackDraft = null;
    }

    private static void LogFeedback(string line, Exception? ex)
    {
        if (ex is null)
            Log.Information("Feedback: {Line}", line);
        else
            Log.Warning(ex, "Feedback: {Line}", line);
    }
}
