namespace Modbot.Companion.Dictation;

/// <summary>Where Dictate is, as the button shows it.</summary>
public enum DictationState
{
    /// <summary>Waiting for a tap.</summary>
    Idle,

    /// <summary>The speech model is being fetched. Taps are ignored until it is ready.</summary>
    Preparing,

    /// <summary>The microphone is open and sound is being gathered.</summary>
    Listening,

    /// <summary>The microphone is closed and the sound is being written out as text.</summary>
    Writing,

    /// <summary>The words are written and on their way to AI Chat; the answer has not begun. A tap behaves as at idle.</summary>
    Sending,
}

/// <summary>Gathers sound from a microphone, in memory, between a start and a stop.</summary>
public interface IDictationRecorder
{
    /// <summary>Raised, on any thread, when a recording has reached its limit and should be stopped.</summary>
    event Action? LimitReached;

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
/// <para><strong>The taps.</strong> Idle with the model on the disk: start listening. Idle without
/// it: fetch it (the button shows the progress), then go back to idle, so the person taps again
/// when they are ready to speak rather than being recorded the moment a download ends. Listening:
/// stop and write out what was said. Preparing and writing: ignored. Sending: the same as idle, so a
/// stuck send never keeps the button from being used.</para>
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

    /// <summary>The model's rate, for counting seconds of sound.</summary>
    private const int SamplesPerSecond = 16_000;

    private readonly Lock _gate = new();
    private readonly IDictationRecorder _recorder;
    private readonly IDictationRecognizer _recognizer;
    private readonly Func<bool> _modelPresent;
    private readonly Func<IProgress<double>, Task<string?>> _prepare;
    private readonly Action<string, Exception?> _log;
    private readonly TimeSpan _noticeFor;

    private DictationState _state;
    private int _percent;
    private string? _problem;
    private string? _notice;
    private int _noticeNumber;
    private Task _work = Task.CompletedTask;

    /// <param name="modelPresent">Whether the speech model is on the disk.</param>
    /// <param name="prepare">Fetches the model, reporting 0 to 1. Answers null when it is ready, otherwise a sentence saying why not.</param>
    /// <param name="log">One-line notes: each stage and each failure. Never the words, never the sound.</param>
    /// <param name="noticeFor">How long a notice stays; <see cref="NoticeFor"/> when not given.</param>
    public DictationController(
        IDictationRecorder recorder,
        IDictationRecognizer recognizer,
        Func<bool> modelPresent,
        Func<IProgress<double>, Task<string?>> prepare,
        Action<string, Exception?> log,
        TimeSpan? noticeFor = null)
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
        _recorder.LimitReached += Finish;
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
                        if (_recorder.Start() is { } why)
                        {
                            _problem = why;
                            Notify(CouldNotListen);
                        }
                        else
                        {
                            _state = DictationState.Listening;
                        }
                    }
                    else
                    {
                        _state = DictationState.Preparing;
                        _percent = 0;
                        _log("getting the speech model ready (first use)", null);
                        _work = Task.Run(PrepareAsync);
                    }

                    break;

                case DictationState.Listening:
                    changed = StartWriting();
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
            changed = _state is DictationState.Listening;
            if (changed)
            {
                _recorder.Stop();
                _state = DictationState.Idle;
                _log("listening stopped without writing (shutting down or chat went off)", null);
            }
        }

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>The recording hit its limit: the same as a second tap.</summary>
    private void Finish()
    {
        bool changed;

        lock (_gate)
            changed = _state is DictationState.Listening && StartWriting();

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Listening to writing. Held under the lock by the caller.</summary>
    private bool StartWriting()
    {
        var samples = _recorder.Stop();
        _state = DictationState.Writing;
        _log($"listening stopped ({samples.Length / (double)SamplesPerSecond:0.0} seconds of sound)", null);
        _work = Task.Run(() => Write(samples));
        return true;
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
