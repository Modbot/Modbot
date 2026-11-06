using Modbot.Companion.App.Listening;
using Modbot.Companion.Dictation;
using Modbot.Companion.Listening;
using SherpaOnnx;

namespace Modbot.Companion.App.Dictation;

/// <summary>
/// Dictate's microphone and its speech engine: the second, and last, file in Modbot's client that
/// opens a microphone, and the only one that writes speech out as text.
/// </summary>
/// <remarks>
/// <para><strong>This narrows a promise the client made on 2026-09-19</strong> (see
/// <c>PhraseListening</c>): that one file may open a microphone. It is now two, and the source
/// guard names both. The reason is Dictate, which lets a moderator speak a message to the AI chat
/// instead of typing it. The rules are the same ones, held to the same test: no other file the
/// client ships may name a recording API at all, and <c>CompanionSourceGuardTests</c> fails the
/// build if one learns to.</para>
/// <para><strong>Nothing is saved. Nothing is sent.</strong> Sound is gathered in memory only from
/// the moment somebody taps Dictate until they tap again (or one minute passes), handed to the
/// speech engine as numbers, and dropped the moment the text is out. No file is opened for writing
/// anywhere in this file, no sound is held after the text is written, and nothing here reaches the
/// network: this file opens no socket and has no upload path to reach. The only thing that goes
/// anywhere afterwards is the text, and that is the caller's to send, not this file's. What leaves
/// the machine: nothing.</para>
/// <para><strong>Only while asked.</strong> The microphone is opened by a tap and closed by the
/// next one, by the one-minute limit, or by the client quitting. It is never open to wait for
/// something to be said. The speech engine is loaded to write one recording out and let go of
/// straight afterwards, so nothing of it stays in memory between uses.</para>
/// <para><strong>The microphone is shared, never taken.</strong> It is opened by the same small
/// class phrase listening uses, which asks Windows for shared mode, the mode every voice chat
/// program asks for. Dictating while VRChat or Discord has the microphone open works, and the
/// exclusive mode that would lock them out is never asked for. If something else has taken the
/// microphone exclusively, opening it fails and the failure comes back as a sentence.</para>
/// <para><strong>Which microphone.</strong> The one picked in Settings for listening, or the
/// Windows default when none is. The caller works that out; this file is handed an id and asks
/// Windows for no list of devices.</para>
/// <para><strong>What this reads.</strong> The five files of the speech model, by the paths
/// <see cref="DictationModel"/> gives, when a recording is written out. Nothing else on the
/// disk.</para>
/// </remarks>
internal sealed class DictationListening : IDictationRecorder, IDictationRecognizer, IDisposable
{
    /// <summary>How much sound Windows collects before handing it over. A fifth of a second.</summary>
    private const int BufferMilliseconds = 200;

    private readonly DictationModel _model;
    private readonly string _folder;
    private readonly Func<string?> _microphoneId;
    private readonly Action<string, Exception?> _log;

    private readonly Lock _gate = new();
    private readonly Resampler _resampler;
    private readonly List<float> _heard = [];

    private IDisposable? _microphone;
    private float[] _mono = new float[4_096];
    private bool _limitRaised;

    /// <param name="model">The pinned model.</param>
    /// <param name="folder">The companion's <c>dictation</c> folder.</param>
    /// <param name="microphoneId">Asked for at every start: the id of the microphone to open, or null for the Windows default.</param>
    /// <param name="log">One-line notes: a failure.</param>
    public DictationListening(
        DictationModel model,
        string folder,
        Func<string?> microphoneId,
        Action<string, Exception?> log)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(microphoneId);
        ArgumentNullException.ThrowIfNull(log);

        _model = model;
        _folder = folder;
        _microphoneId = microphoneId;
        _log = log;
        _resampler = new Resampler(model.SampleRate);
    }

    /// <inheritdoc />
    public event Action? LimitReached;

    /// <inheritdoc />
    public string? Start()
    {
        if (!OperatingSystem.IsWindows())
            return "Dictate only works on Windows.";

        lock (_gate)
        {
            _heard.Clear();
            _resampler.Reset();
            _limitRaised = false;
        }

        try
        {
            _microphone = new PhraseListening.SharedMicrophone(Arrived, BufferMilliseconds, _microphoneId());
            return null;
        }
        catch (Exception ex)
        {
            _log("The microphone could not be opened for Dictate; nothing else is affected.", ex);
            return "The microphone could not be opened. Another program may have taken it for itself, "
                + $"or this PC may have none: {ex.Message}";
        }
    }

    /// <inheritdoc />
    public float[] Stop()
    {
        Close();

        lock (_gate)
        {
            var heard = _heard.ToArray();
            _heard.Clear();
            return heard;
        }
    }

    /// <summary>
    /// Writes the recording out as text. Loads the engine, uses it once and lets go of it, so a
    /// client that is not being dictated to holds none of it.
    /// </summary>
    public string Recognize(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Moonshine.Preprocessor = _model.PathOf(_folder, DictationModel.PreprocessorFile);
        config.ModelConfig.Moonshine.Encoder = _model.PathOf(_folder, DictationModel.EncoderFile);
        config.ModelConfig.Moonshine.UncachedDecoder = _model.PathOf(_folder, DictationModel.UncachedDecoderFile);
        config.ModelConfig.Moonshine.CachedDecoder = _model.PathOf(_folder, DictationModel.CachedDecoderFile);
        config.ModelConfig.Tokens = _model.PathOf(_folder, DictationModel.TokensFile);
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;

        using var recognizer = new OfflineRecognizer(config);
        using var stream = recognizer.CreateStream();

        stream.AcceptWaveform(_model.SampleRate, samples);
        recognizer.Decode(stream);

        return stream.Result.Text?.Trim() ?? string.Empty;
    }

    public void Dispose() => Close();

    /// <summary>Closes the microphone, once, whichever thread gets here first.</summary>
    private void Close()
    {
        var microphone = Interlocked.Exchange(ref _microphone, null);

        try
        {
            microphone?.Dispose();
        }
        catch (Exception ex)
        {
            _log("The microphone could not be closed cleanly.", ex);
        }
    }

    /// <summary>
    /// Sound from Windows, on Windows' own thread: folded to mono, brought to the model's rate and
    /// added to the recording in memory. Nothing is written anywhere.
    /// </summary>
    private void Arrived(ReadOnlySpan<byte> bytes, int sampleRate, int channels, int bits, bool isFloat)
    {
        if (bytes.Length == 0 || channels <= 0 || sampleRate <= 0)
            return;

        var bytesPerSample = bits / 8;
        if (bytesPerSample <= 0)
            return;

        var frames = bytes.Length / (bytesPerSample * channels);
        if (frames <= 0)
            return;

        if (_mono.Length < frames)
            _mono = new float[frames];

        var mono = _mono.AsSpan(0, frames);
        for (var frame = 0; frame < frames; frame++)
        {
            var total = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var at = ((frame * channels) + channel) * bytesPerSample;
                total += PhraseListening.One(bytes.Slice(at, bytesPerSample), bits, isFloat);
            }

            mono[frame] = total / channels;
        }

        var raise = false;

        lock (_gate)
        {
            if (_limitRaised)
                return;

            _resampler.Add(mono, sampleRate, _heard);

            if (_heard.Count >= _model.MostSamples)
            {
                _heard.RemoveRange(_model.MostSamples, _heard.Count - _model.MostSamples);
                _limitRaised = raise = true;
            }
        }

        // Stopping closes the microphone, which waits on this very thread, so it is raised from another one.
        if (raise)
            ThreadPool.QueueUserWorkItem(_ => LimitReached?.Invoke());
    }
}
