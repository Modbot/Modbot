using System.Buffers.Binary;
using System.Runtime.Versioning;
using Modbot.Companion.Listening;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SherpaOnnx;

namespace Modbot.Companion.App.Listening;

/// <summary>
/// The one file in Modbot's client that opens a microphone.
/// </summary>
/// <remarks>
/// <para><strong>This narrows a promise the client used to make.</strong> Until 2026-09-19 the
/// client said, in this program's own source and in its documentation, that it never recorded
/// sound by any route, and the source guard failed the build if any recording API appeared
/// anywhere in it. It can now open a microphone, because a moderator in a headset cannot reach a
/// keyboard and asked to be able to say "Modbot, clip that" instead. The rules that make this a
/// bounded capability rather than an open one are in the listening design spec and are enforced
/// here and in <c>CompanionSourceGuardTests</c>: one file may open a microphone, and no other file
/// the client ships may name a recording API at all.</para>
/// <para><strong>Nothing is recorded. Nothing is kept. Nothing is sent.</strong> Sound arrives in
/// fractions of a second, is turned into the numbers the phrase matcher wants, is handed to the
/// matcher, and is overwritten by the next fraction of a second. No file is opened for writing
/// anywhere in this file, nothing is held beyond the few seconds of working memory the matcher
/// needs, and nothing here reaches the network: this file opens no socket and there is no upload
/// path in the client to reach. What leaves the machine: nothing.</para>
/// <para><strong>It is not transcribing you.</strong> The engine here is a phrase matcher, not a
/// recogniser. It is told four short phrases and answers one question — "was one of those just
/// said" — and it has no ability to produce a transcript of anything else, because it is a 3.3
/// megabyte model whose decoding is constrained to those phrases (listening design §2). That is
/// most of why it was chosen over the general recogniser the same library also offers.</para>
/// <para><strong>The microphone is shared, never taken.</strong> Windows is asked for the default
/// microphone in <em>shared</em> mode, which is what VRChat, Discord and every voice chat program
/// ask for: Windows mixes them and every program hears the same sound. Exclusive mode — the mode
/// that would lock everybody else out — is never asked for, and the one call that could ask for it
/// is not made. If some other program has already taken the device exclusively, opening it here
/// fails, that failure becomes a sentence on the settings screen, and everything else in the client
/// carries on.</para>
/// <para><strong>When it listens.</strong> Only while the switch is on — off unless a person turned
/// it on — and only while VRChat is running, which the client knows because lines are arriving in
/// VRChat's log (<see cref="ListeningRule"/>). VRChat closing closes the microphone. While it is
/// open the client says so on every page of its own window, for as long as it is open.</para>
/// <para><strong>What this reads.</strong> The four files of the phrase model in the client's own
/// <c>phrases</c> folder, once, when it starts. Nothing else on the disk.</para>
/// <para><strong>Its own thread.</strong> Matching must never hold up the loop that reads VRChat's
/// log and reports presence, which is the job that cannot be filled in later. It runs on one
/// background thread, below normal priority, that does nothing else.</para>
/// </remarks>
internal sealed class PhraseListening : IDisposable
{
    /// <summary>How much sound Windows collects before handing it over. A fifth of a second.</summary>
    private const int BufferMilliseconds = 200;

    /// <summary>
    /// How many samples of working memory are allowed to pile up before the oldest are dropped.
    /// </summary>
    /// <remarks>
    /// Five seconds' worth. It should never fill: the matcher runs far faster than sound arrives.
    /// If it ever does — a machine under a load nobody planned for — the right answer is to lose
    /// the oldest sound rather than to grow a buffer forever, because sound that old is no longer
    /// something anybody is waiting for.
    /// </remarks>
    private const int MostSamplesHeld = 5 * 16_000;

    /// <summary>How many numbers a second of sound is described by. The model's own shape.</summary>
    private const int FeatureSize = 80;

    private readonly Action<string, Exception?> _log;
    private readonly Action<string> _heard;
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _wake = new(false);

    /// <summary>Sound waiting for the matcher, at the model's rate, in mono. Held under the lock.</summary>
    private readonly List<float> _waiting = [];

    private Thread? _thread;
    private volatile bool _stopping;
    private IDisposable? _microphone;

    /// <summary>Where the resampler had got to, and the last sample before this buffer.</summary>
    private double _resamplePosition;

    private float _resamplePrevious;

    /// <summary>
    /// One stretch of sound folded down to mono, reused rather than allocated afresh five times a
    /// second. Touched only by Windows' own audio thread, which is the only thread that fills it.
    /// </summary>
    private float[] _mono = [];

    /// <param name="heard">
    /// Called on this class's own thread when one of the phrases was said. Whatever is handed this
    /// has to get itself onto the right thread.
    /// </param>
    /// <param name="log">Where one-line notes go: a microphone that could not be opened, a phrase heard.</param>
    public PhraseListening(Action<string> heard, Action<string, Exception?> log)
    {
        _heard = heard;
        _log = log;
    }

    /// <summary>What went wrong most recently, in one sentence, or null.</summary>
    public string? LastProblem { get; private set; }

    /// <summary>The phrase heard most recently this run, as somebody would say it, or null.</summary>
    public string? LastHeard { get; private set; }

    /// <summary>Whether the microphone is open right now.</summary>
    public bool IsListening { get; private set; }

    /// <summary>
    /// Whether this machine could listen at all. Windows only, for the same reason recording is:
    /// saying "Modbot, clip that" saves a clip, and there are no clips anywhere else.
    /// </summary>
    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>
    /// Loads the phrase model and opens the microphone, on a thread of its own. Returns false, with
    /// <see cref="LastProblem"/> set, when this machine cannot do it at all.
    /// </summary>
    public bool Start(PhraseModel model, string phrasesFolder)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (_thread is not null)
            return true;

        if (!Supported)
        {
            LastProblem = "Listening for a phrase only works on Windows.";
            return false;
        }

        _stopping = false;
        LastProblem = null;

        _thread = new Thread(() => Run(model, phrasesFolder))
        {
            IsBackground = true,
            Name = "Modbot listening for a phrase",

            // The moderator's frame rate matters more than how quickly a phrase is noticed.
            Priority = ThreadPriority.BelowNormal,
        };

        _thread.Start();
        return true;
    }

    /// <summary>Closes the microphone and stops the thread. Safe to call more than once.</summary>
    public void Stop()
    {
        _stopping = true;
        _wake.Set();

        var thread = _thread;
        _thread = null;
        thread?.Join(TimeSpan.FromSeconds(3));

        CloseMicrophone();
        IsListening = false;

        lock (_gate)
            _waiting.Clear();
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }

    /// <summary>
    /// The whole of the listening, on its own thread: load the matcher, open the microphone, and
    /// hand it what arrives until somebody stops it.
    /// </summary>
    private void Run(PhraseModel model, string phrasesFolder)
    {
        KeywordSpotter? matcher = null;
        OnlineStream? stream = null;

        try
        {
            matcher = Load(model, phrasesFolder);
            stream = matcher.CreateStream();

            if (!OpenMicrophone())
                return;

            IsListening = true;
            _log("The microphone is open, shared, and is being listened to for a phrase only.", null);

            var taken = new List<float>();

            while (!_stopping)
            {
                _wake.WaitOne(TimeSpan.FromMilliseconds(50));

                lock (_gate)
                {
                    if (_waiting.Count == 0)
                        continue;

                    taken.Clear();
                    taken.AddRange(_waiting);
                    _waiting.Clear();
                }

                Match(matcher, stream, model, [.. taken]);
            }
        }
        catch (Exception ex)
        {
            LastProblem = $"Listening stopped: {ex.Message}";
            _log("Listening for a phrase stopped.", ex);
        }
        finally
        {
            IsListening = false;
            CloseMicrophone();
            stream?.Dispose();
            matcher?.Dispose();
        }
    }

    /// <summary>
    /// Hands one stretch of sound to the matcher and asks whether a phrase was in it.
    /// </summary>
    /// <remarks>
    /// The samples are handed over and immediately go out of scope; nothing here writes them
    /// anywhere or keeps them. The matcher is reset after a match, which is what stops one sentence
    /// matching over and over as the rest of it arrives; <see cref="PhraseHeard"/> is the second
    /// guard against that, on the other side of the event.
    /// </remarks>
    private void Match(KeywordSpotter matcher, OnlineStream stream, PhraseModel model, float[] samples)
    {
        if (samples.Length == 0)
            return;

        stream.AcceptWaveform(model.SampleRate, samples);

        while (matcher.IsReady(stream))
            matcher.Decode(stream);

        var result = matcher.GetResult(stream);
        if (string.IsNullOrWhiteSpace(result.Keyword))
            return;

        matcher.Reset(stream);

        // Every phrase in the list means the same thing, so which of the four spellings the matcher
        // landed on is not worth telling a moderator; it goes in the client's log and the first
        // spelling is what the screen says.
        var said = model.Spoken.Count > 0 ? model.Spoken[0] : result.Keyword;
        LastHeard = said;
        _log($"Heard “{said}”.", null);
        _heard(said);
    }

    /// <summary>
    /// Loads the phrase matcher: the three parts of the model, its vocabulary, and the phrases.
    /// </summary>
    /// <remarks>
    /// One thread, on the processor. The model is 3.3 million numbers, which is about a thousandth
    /// of the voice, and it is loaded once when listening starts rather than kept loaded while the
    /// client sits in the tray.
    /// </remarks>
    private static KeywordSpotter Load(PhraseModel model, string phrasesFolder)
    {
        var config = new KeywordSpotterConfig();

        config.FeatConfig.SampleRate = model.SampleRate;
        config.FeatConfig.FeatureDim = FeatureSize;

        config.ModelConfig.Transducer.Encoder = model.EncoderPath(phrasesFolder);
        config.ModelConfig.Transducer.Decoder = model.DecoderPath(phrasesFolder);
        config.ModelConfig.Transducer.Joiner = model.JoinerPath(phrasesFolder);
        config.ModelConfig.Tokens = model.TokensPath(phrasesFolder);
        config.ModelConfig.NumThreads = 1;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;

        config.KeywordsFile = model.PhrasesPath(phrasesFolder);

        return new KeywordSpotter(config);
    }

    /// <summary>
    /// Asks Windows for the default microphone, shared.
    /// </summary>
    /// <remarks>
    /// <para><strong>Shared, and the default one.</strong> Shared mode is what every voice chat
    /// program asks for and is why VRChat keeps working while this is open: Windows mixes the
    /// programs that want the microphone and gives each of them the same sound. The exclusive mode
    /// that would lock VRChat out is never asked for.</para>
    /// <para><strong>No list is asked for.</strong> Windows is asked to route whichever device is
    /// the default microphone, rather than for the microphones this PC has, so the client still
    /// never enumerates capture devices — the same shape as never enumerating windows or
    /// processes. A moderator who changes their microphone in Windows changes this with it.</para>
    /// <para>Every way this can fail — no microphone at all, one another program has taken
    /// exclusively, one this account may not use — comes back as false with a sentence in
    /// <see cref="LastProblem"/>, and the client carries on doing everything else.</para>
    /// </remarks>
    private bool OpenMicrophone()
    {
        if (!OperatingSystem.IsWindows())
        {
            LastProblem = "Listening for a phrase only works on Windows.";
            return false;
        }

        try
        {
            _resamplePosition = 0;
            _resamplePrevious = 0;
            _microphone = new Microphone(Arrived, BufferMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            LastProblem =
                "The microphone could not be opened. Another program may have taken it for itself, "
                + $"or this PC may have none: {ex.Message}";
            _log("The microphone could not be opened; nothing else is affected.", ex);
            return false;
        }
    }

    /// <summary>
    /// Closes the microphone, once, whichever of the two threads gets here first.
    /// </summary>
    /// <remarks>
    /// Both the listening thread's own way out and <see cref="Stop"/> close it, because either can
    /// happen first and neither may leave a microphone open on somebody's PC.
    /// </remarks>
    private void CloseMicrophone()
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
    /// Sound from Windows, on Windows' own thread: turned into what the matcher wants and left for
    /// the matcher's thread to pick up.
    /// </summary>
    /// <remarks>
    /// <para>Nothing is written anywhere and nothing is kept: the bytes Windows lends are read
    /// once, turned into at most a fifth of a second of numbers, and the lend ends when this
    /// returns. What is left behind is a few seconds of those numbers at most, in memory, which the
    /// matcher's thread takes and drops.</para>
    /// <para>As little work as possible happens here, because this is Windows' own audio thread and
    /// holding it up is how a program makes everybody else's sound stutter.</para>
    /// </remarks>
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

        // Every channel folded into one, because the matcher hears in mono.
        if (_mono.Length < frames)
            _mono = new float[frames];

        var mono = _mono.AsSpan(0, frames);
        for (var frame = 0; frame < frames; frame++)
        {
            var total = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var at = ((frame * channels) + channel) * bytesPerSample;
                total += One(bytes.Slice(at, bytesPerSample), bits, isFloat);
            }

            mono[frame] = total / channels;
        }

        lock (_gate)
        {
            Resample(mono, sampleRate, _waiting);

            if (_waiting.Count > MostSamplesHeld)
                _waiting.RemoveRange(0, _waiting.Count - MostSamplesHeld);
        }

        _wake.Set();
    }

    /// <summary>
    /// Brings the machine's own rate — usually 48,000 samples a second — down to the 16,000 the
    /// model was trained at, by reading between the samples.
    /// </summary>
    /// <remarks>
    /// Straight-line interpolation, which is about fifteen lines and no library. It is not the
    /// best resampler there is; what it costs is a little noise above 8,000 cycles a second, and
    /// nothing in that band is part of telling one word from another. Where it had got to is
    /// carried between buffers, along with the last sample of the one before, so there is no seam
    /// every fifth of a second.
    /// </remarks>
    private void Resample(ReadOnlySpan<float> mono, int sampleRate, List<float> into)
    {
        var step = sampleRate / (double)PhraseModel.Default.SampleRate;
        var position = _resamplePosition;
        var last = mono.Length - 1;

        while (position <= last)
        {
            var whole = (int)Math.Floor(position);
            var part = (float)(position - whole);

            var before = whole < 0 ? _resamplePrevious : mono[whole];
            var after = mono[whole + 1];

            into.Add(before + ((after - before) * part));
            position += step;
        }

        _resamplePosition = position - mono.Length;
        _resamplePrevious = mono[last];
    }

    private static float One(ReadOnlySpan<byte> sample, int bits, bool isFloat)
    {
        if (isFloat)
            return BitConverter.ToSingle(sample);

        return bits switch
        {
            8 => (sample[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32_768f,
            24 => ((sample[2] << 24) | (sample[1] << 16) | (sample[0] << 8)) / 2_147_483_648f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2_147_483_648f,
        };
    }

    /// <summary>Sound as Windows hands it over, before anything has been done to it.</summary>
    private delegate void SoundArrived(ReadOnlySpan<byte> bytes, int sampleRate, int channels, int bits, bool isFloat);

    /// <summary>
    /// The default microphone, opened shared.
    /// </summary>
    /// <remarks>
    /// Its own small class for the same reason the voice's Windows output is one: it is the only
    /// part of this that is Windows-only, so it is the only part that has to be built behind a
    /// check for Windows. It holds no buffer of its own — the sound Windows lends it goes straight
    /// out of the callback and is gone.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private sealed class Microphone : IDisposable
    {
        private readonly WasapiRecorder _recorder;

        public Microphone(SoundArrived arrived, int bufferMilliseconds)
        {
            _recorder = new WasapiRecorderBuilder()
                // Whichever microphone Windows calls the default, followed when Windows moves it.
                // Never a list of this PC's microphones.
                .WithDefaultDeviceStreamRouting()

                // The one line that matters most in this file. Shared is what voice chat asks for;
                // the exclusive mode beside it would lock VRChat out of the microphone, and that
                // call is one this client does not make anywhere — the source guard checks.
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(bufferMilliseconds)
                .Build();

            var format = _recorder.WaveFormat;
            var isFloat = format.Encoding is WaveFormatEncoding.IeeeFloat;
            var rate = format.SampleRate;
            var channels = format.Channels;
            var bits = format.BitsPerSample;

            _recorder.DataAvailable += (buffer, flags, _, _) =>
            {
                // Windows says "this stretch was silence" rather than sending the zeros. Nothing to
                // listen to, and nothing to hand on.
                if (flags.HasFlag(AudioClientBufferFlags.Silent))
                    return;

                arrived(buffer, rate, channels, bits, isFloat);
            };

            _recorder.StartRecording();
        }

        public void Dispose()
        {
            try
            {
                _recorder.StopRecording();
            }
            catch (Exception)
            {
                // On the way out either way.
            }

            _recorder.Dispose();
        }
    }
}
