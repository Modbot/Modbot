using Modbot.Companion.Voice;
using SherpaOnnx;

namespace Modbot.Companion.App.Voice;

/// <summary>
/// Text to speech in this process: the Piper voice, run by sherpa-onnx on the CPU.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> The voice's own folder under <c>%APPDATA%\Modbot\voices</c>
/// — the model, its token list and the espeak-ng data — once, when the voice is first needed.
/// Nothing else on the disk, and nothing is written.</para>
/// <para><strong>Nothing leaves the machine.</strong> A sentence goes in as text and comes out as
/// samples, here, with no network involved; the engine is a library inside this process, not a
/// program it starts and not a service it calls.</para>
/// <para>Loading costs a second or so and a couple of hundred megabytes, so it happens once and
/// the engine is kept. Making one sentence takes a few hundred milliseconds of one or two cores,
/// which is why it is asked for on a worker thread and never on the window's.</para>
/// </remarks>
internal sealed class SherpaVoice : IVoiceSynthesizer
{
    /// <summary>
    /// Two threads: enough to keep a sentence under half a second on an ordinary desktop, and
    /// few enough to leave the game the machine is also running most of its cores.
    /// </summary>
    private const int Threads = 2;

    private readonly OfflineTts _tts;

    private SherpaVoice(OfflineTts tts)
    {
        _tts = tts;
    }

    /// <summary>The voice's sample rate, as the engine reports it.</summary>
    public int SampleRate => _tts.SampleRate;

    /// <summary>
    /// Loads the voice from its folder. The files must already be there and checked
    /// (<see cref="VoiceModel.IsPresent"/>): the engine does not fail politely on a missing file.
    /// </summary>
    public static SherpaVoice Load(VoiceModel model, string voicesFolder)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!model.IsPresent(voicesFolder))
            throw new FileNotFoundException("The voice is not on this PC.", model.ModelPath(voicesFolder));

        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = model.ModelPath(voicesFolder);
        config.Model.Vits.Tokens = model.TokensPath(voicesFolder);
        config.Model.Vits.DataDir = model.DataPath(voicesFolder);
        config.Model.NumThreads = Threads;
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;
        config.MaxNumSentences = 1;

        return new SherpaVoice(new OfflineTts(config));
    }

    public VoiceClip Speak(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var audio = _tts.Generate(text, speed: 1.0f, speakerId: 0);
        try
        {
            return new VoiceClip(audio.Samples, audio.SampleRate);
        }
        finally
        {
            audio.Dispose();
        }
    }

    public void Dispose() => _tts.Dispose();
}
