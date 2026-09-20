using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Modbot.Companion.Clips;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Modbot.Companion.App;

/// <summary>
/// The one file in Modbot's client that records another program's sound.
/// </summary>
/// <remarks>
/// <para><strong>This narrows a promise the client used to make.</strong> Until 2026-09-19 a clip
/// was a silent picture, and the client said so in its own source, on its documentation site and in
/// its privacy policy: <em>"no sound, at all"</em>. That is no longer true. A moderator asked for
/// clips to carry VRChat's own sound, because a silent picture of somebody being abusive shows
/// nothing, and a clip is evidence or it is nothing. The rules that make this a bounded capability
/// rather than an open one are in the clips design spec §14 and are enforced here and in
/// <c>CompanionSourceGuardTests</c>.</para>
/// <para><strong>Two named programs, and never the machine.</strong> Windows can hand over the
/// sound <em>one program</em> is playing, named by its process, rather than everything the speakers
/// are playing. That is what this file asks for, and it is the whole of the difference between
/// recording VRChat and recording somebody's music. Two programs at most:
/// <list type="bullet">
/// <item><description><strong>VRChat</strong>, whenever Clips is on. It is the point of the
/// feature.</description></item>
/// <item><description><strong>Discord</strong>, only when a moderator has switched that on
/// themselves. Off in a fresh install and off in an updated one.</description></item>
/// </list>
/// There is no third, there is no setting for one, and the call that would hand over everything the
/// machine is playing — the one that would pick up a browser, a music player and every notification
/// sound — is not made anywhere in this client. The source guard fails the build if it appears.</para>
/// <para><strong>Not a microphone.</strong> This records what a program <em>plays</em>. Nothing
/// here opens a microphone, asks Windows for a recording device or asks what recording devices
/// exist; the one file that may open a microphone is <c>PhraseListening.cs</c> and it is a
/// different file with different rules. What reaches a clip through here is the sound VRChat plays
/// — which, in an instance, is what the people around the moderator said, and what makes this a
/// real disclosure rather than a detail.</para>
/// <para><strong>How the programs are named, without a list.</strong> VRChat's process is asked of
/// VRChat's own window, which the recorder already had (<c>ScreenRecording.cs</c>). Discord's is
/// asked of the connection point Discord itself publishes for other programs to find it by — one
/// named ask for one named thing, and nothing is sent down it. The client still never walks a list
/// of what is running, never walks a list of windows, and never asks the sound system which
/// programs are playing through it. "What does this program know about the rest of your PC" still
/// has a short answer: VRChat's window, and which process VRChat and Discord are.</para>
/// <para><strong>What this reads: nothing. What leaves the machine: nothing.</strong> There is
/// no file opened for reading or writing anywhere in this file and no socket; it hands sound to the
/// recorder, which puts it in the same clip on the same disk as the picture. Nothing is sent to a
/// paired server, to Modbot Cloud or anywhere else, and the client has no upload path to
/// reach.</para>
/// <para><strong>When it records.</strong> Only while the recorder is recording, which is only
/// while Clips is on and VRChat is running. A program that is not running, or that will not hand
/// its sound over, costs the clip that program's sound and nothing else: the picture is recorded
/// exactly as before and no failure here ever stops a recording.</para>
/// <para><strong>Its own threads.</strong> Windows hands the sound over on threads of its own, and
/// all that happens on them is a copy into a small buffer. Nothing here can hold up the loop that
/// reads VRChat's log and reports presence.</para>
/// </remarks>
internal sealed class ClipSound : IDisposable
{
    /// <summary>How much sound Windows gathers before handing it over. A tenth of a second.</summary>
    private const int BufferMilliseconds = 100;

    /// <summary>How long Windows is given to answer about Discord's connection point.</summary>
    private const int WaitForDiscordMs = 120;

    /// <summary>
    /// How many of Discord's connection points are tried. Discord publishes the first free one.
    /// </summary>
    /// <remarks>
    /// Four rather than one because a second Discord — the public test build beside the ordinary
    /// one — takes the first, and rather than ten because every one that is not there costs the
    /// wait above and this happens while a moderator is waiting for recording to start.
    /// </remarks>
    private const int DiscordConnectionPoints = 4;

    /// <summary>How often Discord is looked for again while its sound is wanted and missing.</summary>
    private static readonly TimeSpan LookForDiscordEvery = TimeSpan.FromSeconds(30);

    private readonly Action<string, Exception?> _log;
    private readonly List<ISound> _programs = [];
    private readonly Lock _gate = new();

    private byte[] _scratch = [];
    private bool _wantDiscord;
    private bool _haveDiscord;
    private DateTimeOffset _lookForDiscordAt;

    private ClipSound(int sampleRate, Action<string, Exception?> log)
    {
        SampleRate = sampleRate;
        _log = log;
    }

    /// <summary>Samples a second in everything this hands over. The same for every program.</summary>
    public int SampleRate { get; }

    /// <summary>How many programs' sound is being recorded right now.</summary>
    public int Programs
    {
        get
        {
            lock (_gate)
                return _programs.Count;
        }
    }

    /// <summary>Whether this machine could record a program's sound at all.</summary>
    /// <remarks>
    /// Windows 10 version 2004 is where Windows learned to hand over one program's sound rather
    /// than the whole machine's. An older Windows is told the truth — no sound in the clip — rather
    /// than quietly given the whole machine's sound, which is the one thing this feature is not
    /// allowed to record.
    /// </remarks>
    public static bool Supported
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>
    /// Starts recording VRChat's sound, and Discord's when that was asked for. Returns null when
    /// nothing could be opened at all, which costs the clip its sound and nothing else.
    /// </summary>
    /// <param name="gameProcessId">
    /// Which process VRChat is, taken from VRChat's own window by the recorder. Zero means Windows
    /// would not say, and then there is nothing to record.
    /// </param>
    /// <param name="discordToo">Whether the moderator switched Discord's sound on.</param>
    /// <param name="now">The moment, for deciding when to look for Discord again. Never a clock.</param>
    /// <param name="log">Where one-line notes go.</param>
    public static ClipSound? Start(
        uint gameProcessId,
        bool discordToo,
        DateTimeOffset now,
        Action<string, Exception?> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (!Supported)
        {
            log("This Windows is too old to hand over one program's sound, so the clip has none.", null);
            return null;
        }

        if (gameProcessId == 0)
        {
            log("Windows would not say which process VRChat is, so the clip has no sound.", null);
            return null;
        }

        // The preferred rate first and the other only if Windows refuses it, because whichever one
        // VRChat opens at is the rate the whole clip is written at and Discord has to match it.
        foreach (var rate in new[] { ClipSoundRule.PreferredSampleRate, ClipSoundRule.FallbackSampleRate })
        {
            var sound = new ClipSound(rate, log);

            if (!sound.Add("VRChat", gameProcessId))
            {
                sound.Dispose();
                continue;
            }

            sound._wantDiscord = discordToo;
            sound._lookForDiscordAt = now;

            if (discordToo)
                sound.LookForDiscord(now);

            return sound;
        }

        log("VRChat's sound could not be recorded, so the clip has none. Everything else is unaffected.", null);
        return null;
    }

    /// <summary>
    /// Takes the next <paramref name="bytes"/> of sound into <paramref name="into"/>, mixed, with
    /// silence where a program had nothing to give.
    /// </summary>
    /// <remarks>
    /// Called once for every picture written, by the recorder's own thread. It always fills what it
    /// was asked for: a program that is silent, closed or behind leaves silence rather than a
    /// shorter stretch, because a stretch shorter than the picture it belongs to is how sound
    /// drifts away from the picture over a few minutes.
    /// </remarks>
    public void Take(byte[] into, int bytes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(into);

        if (bytes <= 0 || bytes > into.Length)
            return;

        Array.Clear(into, 0, bytes);

        if (_scratch.Length < bytes)
            _scratch = new byte[bytes];

        lock (_gate)
        {
            var first = true;

            foreach (var program in _programs)
            {
                if (first)
                {
                    program.Take(into.AsSpan(0, bytes));
                    first = false;
                    continue;
                }

                var scratch = _scratch.AsSpan(0, bytes);
                scratch.Clear();
                program.Take(scratch);
                MixInto(into.AsSpan(0, bytes), scratch);
            }
        }

        // Discord may have been started after recording was. Looked for again now and then rather
        // than once, and never more often than that, because every look costs a wait.
        if (_wantDiscord && !_haveDiscord && now >= _lookForDiscordAt)
            LookForDiscord(now);
    }

    /// <summary>Throws away the sound gathered so far, so a clip starts level.</summary>
    /// <remarks>
    /// Called when the first picture lands and the two rolling files are opened. Sound has been
    /// gathering since VRChat's window was found, which may be seconds earlier; putting that in
    /// front of the first picture would be a clip whose sound runs ahead of it.
    /// </remarks>
    public void Forget()
    {
        lock (_gate)
        {
            foreach (var program in _programs)
                program.Forget();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var program in _programs)
                program.Dispose();

            _programs.Clear();
        }
    }

    /// <summary>Adds one named program's sound, or says why it could not be added.</summary>
    private bool Add(string name, uint processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return false;

        try
        {
            var program = new OneProgram(processId, SampleRate, BufferMilliseconds);

            lock (_gate)
                _programs.Add(program);

            _log($"{name}'s own sound is being recorded into the clip, at {SampleRate} samples a second.", null);
            return true;
        }
        catch (Exception ex)
        {
            _log($"{name}'s sound could not be recorded; the clip carries on without it.", ex);
            return false;
        }
    }

    /// <summary>Looks for Discord once, and remembers when to look again.</summary>
    private void LookForDiscord(DateTimeOffset now)
    {
        _lookForDiscordAt = now + LookForDiscordEvery;

        if (DiscordProcess() is not { } discord)
            return;

        _haveDiscord = Add("Discord", discord);
    }

    /// <summary>Two programs' sound added together, in place.</summary>
    private static void MixInto(Span<byte> into, ReadOnlySpan<byte> other)
    {
        var mine = MemoryMarshal.Cast<byte, short>(into);
        var theirs = MemoryMarshal.Cast<byte, short>(other);

        for (var at = 0; at < mine.Length && at < theirs.Length; at++)
            mine[at] = ClipSoundRule.Mix(mine[at], theirs[at]);
    }

    /// <summary>
    /// Which process Discord is, asked of the connection point Discord itself publishes.
    /// </summary>
    /// <remarks>
    /// <para><strong>Why this rather than a list.</strong> VRChat is found by its window, which is
    /// one named ask for one named window. Discord has no window Windows can be asked for by name —
    /// its title is whichever channel is open — so the choice was between walking the list of
    /// what is running, walking the list of windows, or asking the sound system which programs are
    /// playing through it. All three are lists of other people's programs, and not having one is a
    /// promise this client makes and keeps.</para>
    /// <para>What is used instead is the connection point Discord publishes so that games can tell
    /// it what somebody is playing. It is named, it exists only while Discord is running, and
    /// Windows will say which process published it. This asks that one question and closes the
    /// connection: <strong>nothing is written to it and nothing is read from it</strong>, Discord is
    /// never spoken to, and no Rich Presence, account or anything else is exchanged.</para>
    /// <para>Discord not running is the ordinary answer here, and it is not a failure: the clip
    /// keeps VRChat's sound and says nothing about Discord.</para>
    /// </remarks>
    private uint? DiscordProcess()
    {
        for (var at = 0; at < DiscordConnectionPoints; at++)
        {
            try
            {
                using var connection = new NamedPipeClientStream(
                    ".",
                    $"discord-ipc-{at}",
                    PipeDirection.InOut,
                    PipeOptions.None);

                connection.Connect(WaitForDiscordMs);

                if (GetNamedPipeServerProcessId(connection.SafePipeHandle, out var owner) && owner != 0)
                    return owner;
            }
            catch (TimeoutException)
            {
                // Discord is not running, or is not publishing this one. Ordinary, and not a
                // failure: a clip with VRChat's sound and no Discord is what was asked for when
                // Discord is not there.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Something else published a connection point under that name, or this account may
                // not open Discord's. Try the next one; never stop the recording over it.
                _log("One of Discord's connection points could not be read; the clip is unaffected.", ex);
            }
        }

        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint serverProcessId);

    /// <summary>
    /// One program's sound, held for the recorder to take a picture's worth at a time.
    /// </summary>
    /// <remarks>
    /// The part that talks to Windows' audio system sits behind this, for the same reason the
    /// phrase listener's microphone sits behind a small class of its own: it is the only
    /// Windows-only piece here, so it is the only piece that has to be built behind a check for
    /// Windows.
    /// </remarks>
    private interface ISound : IDisposable
    {
        /// <summary>Takes what there is into <paramref name="into"/>, leaving the rest silent.</summary>
        void Take(Span<byte> into);

        /// <summary>Throws away what has been gathered, so a clip starts level.</summary>
        void Forget();
    }

    /// <summary>
    /// One program's own sound, as Windows hands it over, with the last half second kept.
    /// </summary>
    /// <remarks>
    /// <para>Windows is asked for <em>this process and the ones it started</em>, because a modern
    /// program is several processes and the one that plays the sound is rarely the one with the
    /// window: Discord plays through a helper it started, and asking only about the program a
    /// moderator sees would record silence. Nothing outside that family of processes is ever handed
    /// over — this is not the machine's sound with a filter on it, it is Windows mixing one
    /// program's sound separately and giving Modbot only that.</para>
    /// <para>What is held is half a second, in one buffer that never grows
    /// (<see cref="HeldSound"/>), so a machine that falls behind loses the oldest moment rather
    /// than filling memory.</para>
    /// </remarks>
    [SupportedOSPlatform("windows10.0.19041.0")]
    private sealed class OneProgram : ISound
    {
        private readonly WasapiRecorder _recorder;
        private readonly HeldSound _held;

        public OneProgram(uint processId, int sampleRate, int bufferMilliseconds)
        {
            _held = new HeldSound(ClipSoundRule.MostSamplesHeld(sampleRate) * ClipSoundRule.BytesPerSample);

            _recorder = new WasapiRecorderBuilder()

                // The line this whole file is about. One named process and the processes it
                // started, and nothing else the machine is playing. The call beside it that would
                // hand over everything coming out of the speakers is never made in this client.
                .WithProcessLoopback(processId, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithEventSync()
                .WithBufferLength(bufferMilliseconds)

                // One shape for everything, so the recorder never has to stretch or convert: two
                // channels, sixteen bits, at the rate the whole clip is written at.
                .WithFormat(new WaveFormat(sampleRate, ClipSoundRule.BytesPerChannel * 8, ClipSoundRule.Channels))
                .BuildAsync()
                .GetAwaiter()
                .GetResult();

            _recorder.DataAvailable += (buffer, _, _, _) => _held.Put(buffer);
            _recorder.StartRecording();
        }

        public void Take(Span<byte> into) => _held.Take(into);

        public void Forget() => _held.Forget();

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
