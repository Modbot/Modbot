using Modbot.Companion.Dictation;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Feedback;

/// <summary>Where Feedback is, as the button shows it.</summary>
public enum FeedbackState
{
    /// <summary>Waiting for a tap.</summary>
    Idle,

    /// <summary>The speech model is being fetched. Taps are ignored until it is ready.</summary>
    Preparing,

    /// <summary>The microphone is open and sound is being gathered.</summary>
    Listening,

    /// <summary>The microphone is closed and the sound is being written out as text.</summary>
    Writing,

    /// <summary>The words are written and with the server to be tidied.</summary>
    Tidying,

    /// <summary>The draft is up for the person to change. Nothing is saved until they send it.</summary>
    Draft,

    /// <summary>The confirmed draft is on its way to the server.</summary>
    Sending,
}

/// <summary>
/// What a tap on Feedback does, apart from drawing it: speak, have the words tidied, show a draft,
/// and save it only when the person sends it. Has no window of its own, so the desktop window can
/// drive it.
/// </summary>
/// <remarks>
/// <para><strong>The speech.</strong> The microphone, the speech engine and the speech model are
/// Dictate's, driven by a <see cref="DictationController"/> of its own. Hand it a recorder from
/// <see cref="MicrophoneTurns.Wrap"/> so that Feedback and Dictate cannot listen at the same time.</para>
/// <para><strong>The draft.</strong> With AI on for the server the words come back as a kind, a title
/// and details. With AI off, or when the server cannot be reached, the draft is the words as they were
/// said, with no kind chosen. The person picks the kind, changes whatever they like and sends or
/// cancels. A cancelled draft sends nothing and keeps nothing.</para>
/// <para><strong>Threads.</strong> Safe to call from any thread. Events are raised outside the lock,
/// on whichever thread caused them, so a caller that draws must hand them to its own.</para>
/// </remarks>
public sealed class FeedbackController
{
    public const string Sent = "Sent";

    /// <summary>The most the server keeps of a title and of details.</summary>
    public const int MaxTitle = 120;

    public const int MaxDetails = 4000;

    private enum Stage
    {
        None,
        Tidying,
        Draft,
        Sending,
    }

    private readonly Lock _gate = new();
    private readonly DictationController _speech;
    private readonly IFeedbackClient _client;
    private readonly Func<ServerPairing?> _pairing;
    private readonly Action<string, Exception?> _log;
    private readonly TimeSpan _noticeFor;

    private Stage _stage;
    private FeedbackDraft? _draft;
    private string? _problem;
    private string? _notice;
    private int _noticeNumber;
    private int _turn;
    private CancellationTokenSource? _tidyCancel;
    private Task _work = Task.CompletedTask;

    /// <param name="recorder">The microphone, from <see cref="MicrophoneTurns.Wrap"/> so it is shared with Dictate.</param>
    /// <param name="recognizer">The speech engine.</param>
    /// <param name="modelPresent">Whether the speech model is on the disk.</param>
    /// <param name="prepare">Fetches the model, reporting 0 to 1. Answers null when it is ready, otherwise a sentence saying why not.</param>
    /// <param name="client">The paired server's feedback requests.</param>
    /// <param name="pairing">The server feedback goes to, or null when there is none.</param>
    /// <param name="log">One-line notes: each stage and each failure. Never the words, never the sound.</param>
    /// <param name="noticeFor">How long a notice stays; <see cref="DictationController.NoticeFor"/> when not given.</param>
    public FeedbackController(
        IDictationRecorder recorder,
        IDictationRecognizer recognizer,
        Func<bool> modelPresent,
        Func<IProgress<double>, Task<string?>> prepare,
        IFeedbackClient client,
        Func<ServerPairing?> pairing,
        Action<string, Exception?> log,
        TimeSpan? noticeFor = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(log);

        _client = client;
        _pairing = pairing;
        _log = log;
        _noticeFor = noticeFor ?? DictationController.NoticeFor;

        _speech = new DictationController(recorder, recognizer, modelPresent, prepare, log, noticeFor);
        _speech.Changed += () => Changed?.Invoke();
        _speech.Finished += BeginTidy;
    }

    /// <summary>Raised whenever <see cref="State"/>, <see cref="Percent"/>, <see cref="Notice"/> or <see cref="Problem"/> changes.</summary>
    public event Action? Changed;

    public FeedbackState State
    {
        get
        {
            lock (_gate)
            {
                return _stage switch
                {
                    Stage.Tidying => FeedbackState.Tidying,
                    Stage.Draft => FeedbackState.Draft,
                    Stage.Sending => FeedbackState.Sending,
                    _ => _speech.State switch
                    {
                        DictationState.Preparing => FeedbackState.Preparing,
                        DictationState.Listening => FeedbackState.Listening,
                        DictationState.Writing => FeedbackState.Writing,

                        // The words are written and the tidying has not started yet.
                        DictationState.Sending => FeedbackState.Tidying,
                        _ => FeedbackState.Idle,
                    },
                };
            }
        }
    }

    /// <summary>How far the speech model has got, 0 to 100, while <see cref="FeedbackState.Preparing"/>.</summary>
    public int Percent => _speech.Percent;

    /// <summary>How loud it is now, 0 to <see cref="DictationController.LevelSteps"/>, while listening; 0 otherwise.</summary>
    public int Level => _speech.Level;

    /// <summary>The draft to change, while <see cref="FeedbackState.Draft"/> or <see cref="FeedbackState.Sending"/>; null otherwise.</summary>
    public FeedbackDraft? Draft
    {
        get { lock (_gate) return _draft; }
    }

    /// <summary>What went wrong on the last try, in one sentence, or null.</summary>
    public string? Problem
    {
        get { lock (_gate) return _problem ?? _speech.Problem; }
    }

    /// <summary>A few words for the button to show for a few seconds, or null. Only shown while at idle.</summary>
    public string? Notice
    {
        get { lock (_gate) return _notice ?? _speech.Notice; }
    }

    /// <summary>Completes when the work started by the last tap is over. For tests.</summary>
    public Task WhenSettled
    {
        get
        {
            lock (_gate)
                return Task.WhenAll(_work, _speech.WhenSettled);
        }
    }

    /// <summary>A tap on the button. While a draft is up, tidying or sending, a tap does nothing.</summary>
    public void Toggle()
    {
        lock (_gate)
        {
            if (_stage is not Stage.None)
                return;

            _problem = null;
            _notice = null;
        }

        _speech.Toggle();
    }

    /// <summary>Closes the microphone and drops what it heard and any draft, without saving anything. For shutting down, or when sending went away.</summary>
    public void Abandon()
    {
        bool changed;

        lock (_gate)
        {
            // A send already on its way cannot be called back; the rest is dropped.
            changed = _stage is Stage.Tidying or Stage.Draft;
            if (changed)
                Drop();
        }

        _speech.Abandon();

        if (changed)
        {
            _speech.SendEnded(null);
            Changed?.Invoke();
        }
    }

    /// <summary>The person cancelled the draft: nothing is sent and nothing is kept.</summary>
    public void Cancel()
    {
        bool changed;

        lock (_gate)
        {
            changed = _stage is Stage.Draft;
            if (changed)
                Drop();
        }

        if (changed)
        {
            _log("feedback draft cancelled; nothing was sent", null);
            _speech.SendEnded(null);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The person sent the draft as it now reads. Saved on the server only now. When it could not be, the
    /// draft stays up with <see cref="Problem"/> saying why, so nothing they wrote is lost.
    /// </summary>
    public Task SendAsync(string? kind, string? title, string? details)
    {
        var known = FeedbackKinds.Known(kind);
        var cleanDetails = Cut(details?.Trim(), MaxDetails);
        var cleanTitle = Cut(title?.Trim(), MaxTitle) ?? string.Empty;

        Task sending;

        lock (_gate)
        {
            if (_stage is not Stage.Draft || known is null || string.IsNullOrEmpty(cleanDetails))
                return Task.CompletedTask;

            _stage = Stage.Sending;
            _problem = null;
            _draft = new FeedbackDraft(known, cleanTitle, cleanDetails, _draft?.Tidied ?? false);
            sending = _work = Task.Run(() => PostAsync(known, cleanTitle, cleanDetails));
        }

        Changed?.Invoke();
        return sending;
    }

    /// <summary>The words are written: the tidying starts, and taps are ignored from this moment.</summary>
    private void BeginTidy(string text)
    {
        var words = Cut(text.Trim(), MaxDetails) ?? string.Empty;
        CancellationTokenSource cancel;
        int turn;

        lock (_gate)
        {
            _stage = Stage.Tidying;
            _problem = null;
            turn = ++_turn;
            cancel = _tidyCancel = new CancellationTokenSource();
            _work = Task.Run(() => TidyAsync(words, turn, cancel));
        }

        Changed?.Invoke();
    }

    private async Task TidyAsync(string words, int turn, CancellationTokenSource cancel)
    {
        FeedbackDraft? tidied = null;

        try
        {
            if (_pairing() is { } pairing)
                tidied = await _client.TidyAsync(pairing, words, cancel.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log("the words could not be tidied; the draft is the words as they were said", ex);
        }
        catch (OperationCanceledException)
        {
            // Dropped while waiting: nothing to show.
        }

        FeedbackDraft draft;

        lock (_gate)
        {
            if (turn != _turn || _stage is not Stage.Tidying)
                return;

            // AI off, or the server could not be asked: the person's own words, no kind chosen.
            draft = tidied is { } good
                ? good with { Title = Cut(good.Title?.Trim(), MaxTitle), Details = Cut(good.Details.Trim(), MaxDetails) ?? words }
                : new FeedbackDraft(null, null, words, false);

            _draft = draft;
            _stage = Stage.Draft;
            _tidyCancel = null;
        }

        _log(draft.Tidied ? "feedback draft ready (tidied by the server)" : "feedback draft ready (the words as said)", null);

        Changed?.Invoke();
    }

    private async Task PostAsync(string kind, string title, string details)
    {
        FeedbackSendResult result;

        try
        {
            result = _pairing() is { } pairing
                ? await _client.SendAsync(pairing, kind, title, details, CancellationToken.None).ConfigureAwait(false)
                : new FeedbackSendResult(HttpFeedbackClient.CouldNotSend);
        }
        catch (Exception ex)
        {
            _log("the feedback could not be sent", ex);
            result = new FeedbackSendResult(HttpFeedbackClient.CouldNotSend);
        }

        lock (_gate)
        {
            if (result.Problem is not null)
            {
                _stage = Stage.Draft;
                _problem = result.Problem;
            }
            else
            {
                Drop();
                Notify(Sent);
            }
        }

        if (result.Problem is null)
        {
            _log("feedback sent", null);
            _speech.SendEnded(null);
        }
        else
        {
            _log($"the feedback could not be sent: {result.Problem}", null);
        }

        Changed?.Invoke();
    }

    /// <summary>Forgets the draft and the stage. Held under the lock by the caller.</summary>
    private void Drop()
    {
        _turn++;
        _tidyCancel?.Cancel();
        _tidyCancel = null;
        _stage = Stage.None;
        _draft = null;
    }

    private void Notify(string text)
    {
        _notice = text;
        var number = ++_noticeNumber;

        _ = Task.Delay(_noticeFor).ContinueWith(
            _ =>
            {
                bool gone;

                lock (_gate)
                {
                    gone = _noticeNumber == number && _notice is not null;
                    if (gone)
                        _notice = null;
                }

                if (gone)
                    Changed?.Invoke();
            },
            TaskScheduler.Default);
    }

    private static string? Cut(string? text, int most)
        => text is null || text.Length <= most ? text : text[..most];
}
