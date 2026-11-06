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
}

/// <summary>Gathers sound from a microphone, in memory, between a start and a stop.</summary>
public interface IDictationRecorder
{
    /// <summary>Raised, on any thread, when a recording has reached its limit and should be stopped.</summary>
    event Action? LimitReached;

    /// <summary>Opens the microphone. Returns null when it opened, otherwise a sentence saying why not.</summary>
    string? Start();

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
/// stop and write out what was said. Preparing and writing: ignored.</para>
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

    private readonly Lock _gate = new();
    private readonly IDictationRecorder _recorder;
    private readonly IDictationRecognizer _recognizer;
    private readonly Func<bool> _modelPresent;
    private readonly Func<IProgress<double>, Task<string?>> _prepare;
    private readonly Action<string, Exception?> _log;

    private DictationState _state;
    private int _percent;
    private string? _problem;
    private Task _work = Task.CompletedTask;

    /// <param name="modelPresent">Whether the speech model is on the disk.</param>
    /// <param name="prepare">Fetches the model, reporting 0 to 1. Answers null when it is ready, otherwise a sentence saying why not.</param>
    /// <param name="log">One-line notes: a failure, a recording that was dropped.</param>
    public DictationController(
        IDictationRecorder recorder,
        IDictationRecognizer recognizer,
        Func<bool> modelPresent,
        Func<IProgress<double>, Task<string?>> prepare,
        Action<string, Exception?> log)
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
        _recorder.LimitReached += Finish;
    }

    /// <summary>Raised whenever <see cref="State"/>, <see cref="Percent"/> or <see cref="Problem"/> changes.</summary>
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
                    changed = true;
                    _problem = null;

                    if (_modelPresent())
                    {
                        if (_recorder.Start() is { } why)
                            _problem = why;
                        else
                            _state = DictationState.Listening;
                    }
                    else
                    {
                        _state = DictationState.Preparing;
                        _percent = 0;
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
        _work = Task.Run(() => Write(samples));
        return true;
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
        }

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
            _log("Dictation heard nothing; nothing was written out.", null);
        }
        else
        {
            try
            {
                text = _recognizer.Recognize(samples).Trim();
            }
            catch (Exception ex)
            {
                _log("Dictation could not write that out.", ex);
                problem = $"That could not be written out: {ex.Message}";
            }
        }

        lock (_gate)
        {
            _state = DictationState.Idle;
            _problem = problem;
        }

        Changed?.Invoke();

        if (text.Length > 0)
            Finished?.Invoke(text);
    }

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
