namespace Modbot.Companion.Dictation;

/// <summary>Where Dictate is, as the button shows it.</summary>
public enum DictationState
{
    /// <summary>Waiting for a tap.</summary>
    Idle,

    /// <summary>The speech model is being fetched. Taps are ignored until it is ready.</summary>
    Preparing,

    /// <summary>A tap has started the microphone and it has not yet delivered its first sound. What is said now is kept.</summary>
    Starting,

    /// <summary>The microphone is open and sound is being gathered.</summary>
    Listening,

    /// <summary>The tap to stop has been made and the sound is being written out as text. The microphone stays open a moment more, so the last words are not cut off.</summary>
    Writing,

    /// <summary>The words are written and on their way to AI Chat; the answer has not begun. A tap behaves as at idle.</summary>
    Sending,
}

/// <summary>Gathers sound from a microphone, in memory, between a start and a stop.</summary>
public interface IDictationRecorder
{
    /// <summary>Raised, on any thread, when a recording has reached its limit and should be stopped.</summary>
    event Action? LimitReached;

    /// <summary>
    /// Raised, on any thread, once per <see cref="Start"/>, when the microphone has delivered its first
    /// sound (silence included), so what is said from then on is certain to be heard.
    /// </summary>
    event Action? Flowing;

    /// <summary>Opens the microphone. Returns null when it opened, otherwise a sentence saying why not.</summary>
    string? Start();

    /// <summary>How loud the last fraction of a second was, 0 (silence) to 1 (the loudest a microphone can hear), while listening.</summary>
    float Level { get; }

    /// <summary>Closes the microphone and hands over everything it heard as 16 kHz mono numbers.</summary>
    float[] Stop();
}

/// <summary>Writes sound out as text.</summary>
public interface IDictationRecognizer
{
    /// <summary>The text for these 16 kHz mono samples, or an empty string when nothing was said. Throws on a failure.</summary>
    string Recognize(float[] samples);
}

/// <summary>
/// What a tap on Dictate does: the whole of the button's behaviour, apart from drawing it and
/// the microphone and the engine behind it.
/// </summary>
/// <remarks>
/// <para><strong>The taps.</strong> Idle with the model on the disk: start the microphone, which
/// takes a moment, so the button says Starting until the microphone has delivered its first sound and
/// then Listening; whatever is said while it says Starting is kept. Idle without
/// it: fetch it (the button shows the progress), then go back to idle, so the person taps again
/// when they are ready to speak rather than being recorded the moment a download ends. Starting or
/// listening: stop and write out what was said, after a moment more of listening so the last words
/// are not cut off (the button says Writing through it). Preparing and writing: ignored. Sending: the
/// same as idle, so a stuck send never keeps the button from being used.</para>
/// <para><strong>What comes out.</strong> <see cref="Finished"/> with the text, only when there is
/// some. A recording that was quiet all the way through, or that the engine heard nothing in, ends
/// back at idle having sent nothing at all. The sound itself never leaves this class's recorder
/// and engine: it is handed from one to the other in memory and dropped.</para>
/// <para><strong>Threads.</strong> Safe to call from any thread. Events are raised outside the
/// lock, on whichever thread caused them, so a caller that draws must hand them to its own.</para>
/// </remarks>
public sealed class DictationController
{
    /// <summary>
    /// A recording whose loudest moment is quieter than this is treated as silence and not
    /// written out. About 34 dB below the loudest a microphone can hear: well under ordinary speech,
    /// and above the hiss of a microphone that is open with nobody talking. The engine invents words
    /// for silence if asked, so it is not asked.
    /// </summary>
    public const float QuietBelow = 0.02f;

    /// <summary>A tenth of a second at the model's rate. Shorter than this is a mis-tap, not speech.</summary>
    public const int FewestSamples = 1_600;

    /// <summary>How many steps <see cref="Level"/> has above silence.</summary>
    public const int LevelSteps = 8;

    /// <summary>What the button says for a few seconds when a recording had no words in it.</summary>
    public const string NothingHeard = "Nothing heard";

    /// <summary>What the button says for a few seconds once the speech model is on the disk.</summary>
    public const string Ready = "Ready";

    public const string CouldNotListen = "Could not listen";
    public const string CouldNotWrite = "Could not write";
    public const string CouldNotGetReady = "Could not get ready";
    public const string CouldNotSend = "Could not send";

    /// <summary>How long a notice stays on the button.</summary>
    public static readonly TimeSpan NoticeFor = TimeSpan.FromSeconds(4);

    /// <summary>How much longer the microphone listens after the tap to stop, so the last words are kept.</summary>
    public static readonly TimeSpan ListenOnFor = TimeSpan.FromSeconds(1.5);

    private static TaskCompletionSource Done()
    {
        var done = new TaskCompletionSource();
        done.SetResult();
        return done;
    }

    /// <summary>The model's rate, for counting seconds of sound.</summary>
    private const int SamplesPerSecond = 16_000;

    private readonly Lock _gate = new();
    private readonly IDictationRecorder _recorder;
    private readonly IDictationRecognizer _recognizer;
    private readonly Func<bool> _modelPresent;
    private readonly Func<IProgress<double>, Task<string?>> _prepare;
    private readonly Action<string, Exception?> _log;
    private readonly TimeSpan _noticeFor;
    private readonly TimeSpan _listenOnFor;

    private DictationState _state;
    private bool _microphoneOpen;
    private bool _flowed;
    private bool _opening;
    private CancellationTokenSource? _listenOn;
    private TaskCompletionSource _opened = Done();
    private int _percent;
    private string? _problem;
    private string? _notice;
    private int _noticeNumber;
    private Task _work = Task.CompletedTask;

    /// <param name="modelPresent">Whether the speech model is on the disk.</param>
    /// <param name="prepare">Fetches the model, reporting 0 to 1. Answers null when it is ready, otherwise a sentence saying why not.</param>
    /// <param name="log">One-line notes: each stage and each failure. Never the words, never the sound.</param>
    /// <param name="noticeFor">How long a notice stays; <see cref="NoticeFor"/> when not given.</param>
    /// <param name="listenOnFor">How much longer to listen after the tap to stop; <see cref="ListenOnFor"/> when not given.</param>
    public DictationController(
        IDictationRecorder recorder,
        IDictationRecognizer recognizer,
        Func<bool> modelPresent,
        Func<IProgress<double>, Task<string?>> prepare,
        Action<string, Exception?> log,
        TimeSpan? noticeFor = null,
        TimeSpan? listenOnFor = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(modelPresent);
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(log);

        _recorder = recorder;
        _recognizer = recognizer;
        _modelPresent = modelPresent;
        _prepare = prepare;
        _log = log;
        _noticeFor = noticeFor ?? NoticeFor;
        _listenOnFor = listenOnFor ?? ListenOnFor;
        _recorder.LimitReached += Finish;
        _recorder.Flowing += Flowed;
    }

    /// <summary>Raised whenever <see cref="State"/>, <see cref="Percent"/>, <see cref="Notice"/> or <see cref="Problem"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Raised with what was said, once it is written out. Never raised with blank text.</summary>
    public event Action<string>? Finished;

    public DictationState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>How far the model has got, 0 to 100, while <see cref="DictationState.Preparing"/>.</summary>
    public int Percent
    {
        get { lock (_gate) return _percent; }
    }

    /// <summary>What went wrong on the last try, in one sentence, or null.</summary>
    public string? Problem
    {
        get { lock (_gate) return _problem; }
    }

    /// <summary>
    /// A few words for the button to show for a few seconds after something ended without sending
    /// anything, or failed; null otherwise. Only shown while at idle.
    /// </summary>
    public string? Notice
    {
        get { lock (_gate) return _notice; }
    }

    /// <summary>How loud it is now, 0 to <see cref="LevelSteps"/>, while <see cref="DictationState.Listening"/>; 0 otherwise.</summary>
    public int Level
    {
        get
        {
            lock (_gate)
                return _state is DictationState.Listening ? StepOf(_recorder.Level) : 0;
        }
    }

    /// <summary>
    /// The step, 0 to <see cref="LevelSteps"/>, for a peak sample value from 0 to 1. A scale of decibels,
    /// from about 46 below the loudest a microphone can hear (nothing) to about 6 below it (all steps),
    /// which is how a voice is heard: ordinary speech lands in the middle, a whisper low, a shout high.
    /// </summary>
    public static int StepOf(float peak)
    {
        if (float.IsNaN(peak) || peak <= 0.005f)
            return 0;

        var decibels = 20 * Math.Log10(Math.Min(peak, 1f));
        var fraction = Math.Clamp((decibels + 46) / 40, 0, 1);

        return (int)Math.Round(fraction * LevelSteps);
    }

    /// <summary>Completes when the work started by the last tap is over. For tests.</summary>
    public Task WhenSettled
    {
        get { lock (_gate) return _work; }
    }

    /// <summary>A tap on the button.</summary>
    public void Toggle()
    {
        bool changed;

        lock (_gate)
        {
            switch (_state)
            {
                case DictationState.Idle:
                case DictationState.Sending:
                    changed = true;
                    _problem = null;
                    _notice = null;
                    _state = DictationState.Idle;

                    if (_modelPresent())
                    {
                        // Opening the microphone takes a moment, so it is done off the caller's
                        // thread while the button says Starting.
                        _state = DictationState.Starting;
                        _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _work = Task.Run(Open);
                    }
                    else
                    {
                        _state = DictationState.Preparing;
                        _percent = 0;
                        _log("getting the speech model ready (first use)", null);
                        _work = Task.Run(PrepareAsync);
                    }

                    break;

                case DictationState.Starting:
                case DictationState.Listening:
                    changed = StartWriting(_listenOnFor);
                    break;

                default:
                    changed = false;
                    break;
            }
        }

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Closes the microphone and drops what it heard, without writing it out. For shutting down.</summary>
    public void Abandon()
    {
        bool changed;

        lock (_gate)
        {
            // Writing with a listen-on still to run counts too: the microphone is still open.
            changed = _state is DictationState.Starting or DictationState.Listening
                || (_state is DictationState.Writing && _listenOn is not null);

            if (changed)
            {
                _listenOn?.Cancel();
                _listenOn = null;
                CloseMicrophone();
                _state = DictationState.Idle;
                _log("listening stopped without writing (shutting down or chat went off)", null);
            }
        }

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>The recording hit its limit: the same as a second tap, with nothing more to listen on for.</summary>
    private void Finish()
    {
        bool changed;

        lock (_gate)
            changed = (_state is DictationState.Starting or DictationState.Listening) && StartWriting(TimeSpan.Zero);

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>The microphone has delivered its first sound: Starting becomes Listening.</summary>
    private void Flowed()
    {
        bool changed;

        lock (_gate)
        {
            _flowed = true;
            changed = _state is DictationState.Starting && _microphoneOpen;
            if (changed)
                _state = DictationState.Listening;
        }

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Opens the microphone for a tap that found the model ready. Runs off the caller's thread.</summary>
    private void Open()
    {
        lock (_gate)
        {
            // Abandoned before it got going, or already open or opening for an earlier tap: nothing to open.
            // A stop tap that came first still needs the microphone, for the tail and for what it hears.
            if (_opening)
                return;

            if (!WantsMicrophone() || _microphoneOpen)
            {
                _opened.TrySetResult();
                return;
            }

            _opening = true;
            _flowed = false;
        }

        // Opening can take a while, so the lock is not held: the button must still be able to paint
        // Starting, and a second tap must not freeze.
        string? why;

        try
        {
            why = _recorder.Start();
        }
        catch (Exception ex)
        {
            _log("the microphone could not be opened", ex);
            why = $"The microphone could not be opened: {ex.Message}";
        }

        var changed = false;

        lock (_gate)
        {
            _opening = false;

            if (!WantsMicrophone())
            {
                // Abandoned while the microphone opened: it is open now, so shut it again.
                if (why is null)
                    _recorder.Stop();
            }
            else if (why is not null)
            {
                _listenOn?.Cancel();
                _listenOn = null;
                _problem = why;
                _state = DictationState.Idle;
                Notify(CouldNotListen);
                changed = true;
            }
            else
            {
                // Opened. Writing here means the stop tap came first: it closes the microphone after its tail.
                _microphoneOpen = true;
                changed = _flowed && _state is DictationState.Starting;
                if (changed)
                    _state = DictationState.Listening;
            }

            _opened.TrySetResult();
        }

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Whether a tap is waiting on the microphone: starting, or stopped with its tail still to run. Held under the lock by the caller.</summary>
    private bool WantsMicrophone() =>
        _state is DictationState.Starting || (_state is DictationState.Writing && _listenOn is not null);

    /// <summary>Starting or listening to writing. Held under the lock by the caller.</summary>
    /// <param name="listenOn">How much longer the microphone stays open before what it heard is written out.</param>
    private bool StartWriting(TimeSpan listenOn)
    {
        _state = DictationState.Writing;
        var cancel = _listenOn = new CancellationTokenSource();
        var opened = _opened.Task;
        _work = Task.Run(() => ListenOnThenWrite(listenOn, cancel, opened));
        return true;
    }

    /// <summary>Keeps listening for <paramref name="listenOn"/>, then closes the microphone and writes out what it heard.</summary>
    /// <param name="opened">Completes once the microphone has been opened (or given up on): a stop tap that came before that waits for it, so the words are kept.</param>
    private async Task ListenOnThenWrite(TimeSpan listenOn, CancellationTokenSource cancel, Task opened)
    {
        await opened.ConfigureAwait(false);

        if (listenOn > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(listenOn, cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        float[] samples;

        lock (_gate)
        {
            // Abandoned while it listened on.
            if (_listenOn != cancel)
                return;

            _listenOn = null;
            samples = _microphoneOpen ? CloseMicrophone() : [];
        }

        cancel.Dispose();
        _log($"listening stopped ({samples.Length / (double)SamplesPerSecond:0.0} seconds of sound)", null);
        Write(samples);
    }

    /// <summary>Closes the microphone and hands over what it heard. Held under the lock by the caller.</summary>
    private float[] CloseMicrophone()
    {
        if (!_microphoneOpen)
            return [];

        _microphoneOpen = false;
        return _recorder.Stop();
    }

    /// <summary>
    /// The words went to AI Chat: the answer has begun (<paramref name="problem"/> null), or the message
    /// could not be sent or answered (a sentence). Does nothing unless still sending, so a tap that
    /// moved on to the next dictation is left alone.
    /// </summary>
    public void SendEnded(string? problem)
    {
        bool changed;

        lock (_gate)
        {
            changed = _state is DictationState.Sending;
            if (changed)
            {
                _state = DictationState.Idle;

                if (problem is not null)
                {
                    _problem = problem;
                    Notify(CouldNotSend);
                }
            }
        }

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Shows <paramref name="text"/> on the button for a few seconds. Held under the lock by the caller.</summary>
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

    private async Task PrepareAsync()
    {
        string? problem;

        try
        {
            problem = await _prepare(new Progress<double>(Moved)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("The speech model could not be set up.", ex);
            problem = $"The speech model could not be set up: {ex.Message}";
        }

        lock (_gate)
        {
            _state = DictationState.Idle;
            _problem = problem;
            Notify(problem is not null ? CouldNotGetReady : Ready);
        }

        _log(problem is null ? "the speech model is ready" : $"the speech model could not be got ready: {problem}", null);
        Changed?.Invoke();
    }

    private void Moved(double fraction)
    {
        var percent = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);

        lock (_gate)
        {
            if (_state is not DictationState.Preparing || percent == _percent)
                return;

            _percent = percent;
        }

        Changed?.Invoke();
    }

    private void Write(float[] samples)
    {
        string text = string.Empty;
        string? problem = null;

        if (samples.Length < FewestSamples || IsQuiet(samples))
        {
            _log("nothing heard (too short or too quiet); nothing was written out", null);
        }
        else
        {
            try
            {
                text = _recognizer.Recognize(samples).Trim();
            }
            catch (Exception ex)
            {
                _log("could not write the sound out as words", ex);
                problem = $"That could not be written out: {ex.Message}";
            }
        }

        lock (_gate)
        {
            _problem = problem;

            if (text.Length > 0)
            {
                _state = DictationState.Sending;
                _log($"written ({WordCount(text)} words)", null);
            }
            else
            {
                _state = DictationState.Idle;
                Notify(problem is not null ? CouldNotWrite : NothingHeard);
            }
        }

        Changed?.Invoke();

        if (text.Length > 0)
            Finished?.Invoke(text);
    }

    private static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Whether nothing in the recording was loud enough to be speech.</summary>
    public static bool IsQuiet(float[] samples)
    {
        foreach (var sample in samples)
        {
            if (Math.Abs(sample) >= QuietBelow)
                return false;
        }

        return true;
    }
}
