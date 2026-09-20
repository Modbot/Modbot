using System.Diagnostics;
using System.Runtime.InteropServices;
using Modbot.Companion.Clips;
using Modbot.Core.Time;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Modbot.Companion.App;

/// <summary>
/// The one file in Modbot's client that records a picture of a screen.
/// </summary>
/// <remarks>
/// <para><strong>This reverses a promise the client used to make.</strong> Until 2026-09-19 the
/// client said, in this program's own source and in its documentation, that it never captured the
/// screen by any route. It can now, because a moderator asked to be able to keep the last few
/// minutes and save them when something happens. The rules that make that a bounded capability
/// rather than an open one are in the clips design spec and are enforced here and in
/// <c>CompanionSourceGuardTests</c>: one file may record, and no other file the client ships may
/// name a capture API.</para>
/// <para><strong>What it records: VRChat's window, and VRChat's sound.</strong> Not the monitor and
/// not the machine's sound. This file asks Windows for VRChat's own window by name, takes the part
/// of the screen that window is drawn in, and records that, shrunk to at most 1280 pixels wide, at
/// 15 frames a second. It also asks that one window which process VRChat is, which is how
/// <see cref="ClipSound"/> can be handed one program rather than the speakers. It does not ask
/// Windows for a list of the programs that are running or a list of their windows, and it never
/// has.</para>
/// <para><strong>The picture fills the frame.</strong> The frame's size is fixed when recording
/// starts, because a video file cannot change size part way through and closing both rolling files
/// on every resize would throw away the minutes a moderator is about to want. So VRChat's window as
/// it is now is scaled into that frame — by the same amount across and down, so nobody is
/// stretched — rather than drawn at whatever size it happens to come out at with the rest painted
/// black. A window that changes size changes the scale. <see cref="ClipWindowRule.Fit"/> decides
/// where it goes and <see cref="ClipPicture"/> draws it, and both are arithmetic that can be
/// checked without a screen.</para>

/// <para><strong>What can still end up in a clip.</strong> Windows hands this file a copy of what
/// the desktop already drew, and what the desktop drew inside VRChat's rectangle is whatever is on
/// top of it — a chat program's in-game overlay, a notification, Modbot's own panel over the game.
/// Those are in the clip. What is not is everything outside that rectangle, and everything on the
/// screen while VRChat is behind another program: while a moderator is working in something else,
/// the last picture of VRChat is written again rather than what they have moved to. So a clip is
/// VRChat and what was drawn over VRChat, and it is never the rest of their screen.</para>
/// <para><strong>It never hands over a file with nothing in it.</strong> Having a window, a
/// graphics device and an encoder does not mean a picture has been captured — the rule while
/// VRChat is not the window in front is to write the last picture of it again, and before the
/// first one there is no last picture. Writing then would give a file that opens, runs for the
/// right number of minutes and is black, which a moderator would find out about at the moment they
/// needed it. So no encoder is opened and no frame written until the first real picture lands
/// (<see cref="AnyPictureTaken"/>), a save asked for before then is refused in words, and both the
/// settings screen and the overlay say <em>Nothing recorded yet</em> rather than offering a Save.
/// What it is doing instead — which graphics card and screen, where VRChat's window is, how many
/// pictures have been copied and how many frames held — goes into the client's own log file, so a
/// clip that still comes out wrong can be read about rather than guessed at.</para>
/// <para><strong>Sound: VRChat's own, and Discord's only if that was switched on.</strong> Until
/// 2026-09-19 a clip was a silent picture and this paragraph said so. It now carries the sound
/// VRChat is playing — which in an instance is what the people around the moderator said — because
/// a silent picture of somebody being abusive shows nothing. Discord's sound is a second switch,
/// off unless a person turned it on. <strong>Nothing else the machine is playing is ever
/// recorded</strong>: not music, not a browser, not another chat program, not Windows' own sounds.
/// Windows hands over one named program's sound rather than the speakers', which is what makes that
/// a fact rather than a promise; the capture itself is in <see cref="ClipSound"/>, the one file
/// allowed to do it, and this one only writes what it is handed into the same file as the picture.
/// No microphone is opened here or there. No keyboard, no clipboard, no file anywhere else on the
/// disk.</para>
/// <para><strong>When it records.</strong> Only while the Clips switch is on — off unless a person
/// turned it on — and only while VRChat is running, which the client knows because lines are
/// arriving in VRChat's log (<see cref="ClipRecordingRule"/>). VRChat closing stops it and deletes
/// what was kept.</para>
/// <para><strong>What it writes to your disk.</strong> Two files under <c>%APPDATA%\Modbot\clips</c>,
/// each holding at most the chosen number of minutes, each replaced by a fresh one as it fills, and
/// both deleted when recording stops or the client quits. Saving a clip moves the older of the two
/// into the folder named on the settings screen and nothing is copied anywhere else.</para>
/// <para><strong>What leaves the machine: nothing.</strong> No clip, no frame and no fact that a
/// clip exists is sent to a paired server, to Modbot Cloud, or anywhere else. This file opens no
/// socket and there is no upload path in the client to reach. Attaching a clip to a case is still
/// what it always was — a moderator, in Modbot's web interface, in a browser, choosing a file.</para>
/// <para><strong>Its own thread.</strong> Capturing and encoding must never hold up the loop that
/// reads VRChat's log and reports presence, which is the job that cannot be filled in later. It runs
/// on one background thread that does nothing else, and every failure it can have ends as a sentence
/// on the settings screen rather than as a stopped client.</para>
/// </remarks>
internal sealed class ScreenRecording : IDisposable
{
    /// <summary>Frames a second. Low on purpose: a moderator's frame rate matters more than clip quality.</summary>
    private const int FramesPerSecond = 15;

    /// <summary>How long one frame lasts, in the hundred-nanosecond units Media Foundation counts in.</summary>
    private const long FrameDuration = 10_000_000L / FramesPerSecond;

    /// <summary>Bits a second given to the encoder. About 11 MB a minute.</summary>
    private const int Bitrate = 1_500_000;

    /// <summary>A full frame at least this often, so a saved clip can start near where it was cut.</summary>
    private const int KeyFrameEverySeconds = 2;

    /// <summary>DXGI's "nothing has changed on screen yet".</summary>
    private const int WaitTimeout = unchecked((int)0x887A0027);

    /// <summary>DXGI's "somebody else took the duplication, or the mode changed". Start again.</summary>
    private const int AccessLost = unchecked((int)0x887A0026);

    /// <summary>
    /// DXGI's "this screen cannot be handed over at all". A machine where the screen VRChat is on
    /// belongs to a graphics card that will not duplicate it.
    /// </summary>
    private const int CannotBeHandedOver = unchecked((int)0x887A0004);

    /// <summary>How often Windows is asked again for VRChat's window while there is not one.</summary>
    private static readonly TimeSpan LookAgainEvery = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long Windows is given to hand over a picture while there has not been one yet.
    /// </summary>
    /// <remarks>
    /// Normally the answer is wanted straight away: nothing has changed on the screen since the
    /// last frame is an ordinary answer and the last picture is written again. But the first
    /// picture is not ordinary — nothing can be saved until it lands, and asking with no patience
    /// at all fifteen times a second is how a recorder spends its first seconds finding nothing.
    /// So it waits, but only until it has one.
    /// </remarks>
    private const int WaitForTheFirstPictureMs = 250;

    /// <summary>How often the log is told how the recording is actually going.</summary>
    private static readonly TimeSpan SayHowItIsGoingEvery = TimeSpan.FromMinutes(1);

    private static bool _mediaFoundationStarted;

    /// <summary>
    /// The earliest frame at which the recorder may follow VRChat onto another monitor. Touched
    /// only by the recording thread.
    /// </summary>
    /// <remarks>
    /// A window that is genuinely on no monitor — dragged off the edge and left there — would
    /// otherwise cost a duplication taken and thrown away fifteen times a second for as long as it
    /// stayed there. Once a second is fast enough to follow a window somebody just dragged.
    /// </remarks>
    private long _followTheWindowAtFrame;

    /// <summary>
    /// How the recording is actually going, so a bug report can be read rather than guessed at.
    /// Touched only by the recording thread, and written to the client's own log file.
    /// </summary>
    /// <remarks>
    /// A clip that turns out black is the one failure a moderator cannot see happening, and the
    /// difference between its causes — VRChat never in front, Windows handing over nothing, the
    /// window on a screen that is not being read — is invisible in the file itself. These are what
    /// tells them apart afterwards.
    /// </remarks>
    /// <summary>
    /// The earliest frame at which a change in how VRChat's picture sits in the frame may be
    /// written to the log. Touched only by the recording thread.
    /// </summary>
    /// <remarks>
    /// Somebody dragging a window by its corner changes it fifteen times a second for as long as
    /// they hold the mouse down, and a log line each would bury everything else. Once a second is
    /// enough to read what happened afterwards.
    /// </remarks>
    private long _sayTheFitAtFrame;

    private long _picturesCopied;
    private long _framesHeld;
    private long _framesWritten;
    private long _nothingChanged;
    private long _screenTakenAway;
    private long _windowOffThisScreen;

    /// <summary>
    /// Samples a second in the clip's sound, or zero when this clip is a silent picture.
    /// </summary>
    /// <remarks>
    /// Settled once, when the sound is opened, and read by every rolling file made afterwards: a
    /// video file's sound cannot change rate part way through any more than its picture can change
    /// size. Zero — no sound could be opened, or this machine cannot hand over one program's sound
    /// — means no sound track is put in the file at all, which is a silent clip rather than a
    /// broken one. Touched only by the recording thread.
    /// </remarks>
    private int _soundRate;

    private readonly IModbotClock _clock;
    private readonly string _temporaryFolder;
    private readonly Action<string, Exception?> _log;

    private Thread? _thread;
    private volatile bool _stopping;

    /// <summary>Where the next saved clip should be written. Set by the UI thread, taken by ours.</summary>
    private string? _saveTo;

    private readonly Lock _gate = new();

    /// <summary>The file name of the clip saved most recently, for the settings screen.</summary>
    public string? LastSaved { get; private set; }

    /// <summary>What went wrong most recently, in one sentence, or null.</summary>
    public string? LastProblem { get; private set; }

    /// <summary>Whether the thread is up and frames are going into a file.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>
    /// Whether Windows has handed over VRChat's window. False while VRChat is starting, and while
    /// it is running without one; the settings screen and the overlay say so rather than offering a
    /// Save that would keep nothing.
    /// </summary>
    public bool WindowFound { get; private set; }

    /// <summary>
    /// Whether one picture of VRChat's window has ever been captured this run.
    /// </summary>
    /// <remarks>
    /// <para>This is the difference between a clip and a black file. Having a window, a graphics
    /// device and an encoder does not mean a picture has been copied: VRChat may not have been the
    /// window in front since recording started, in which case the rule is to write the last
    /// picture of VRChat again — and there is no last picture, so what would be written is an
    /// empty buffer, fifteen times a second, for as long as it went on. The file that came out of
    /// that would open, would run for the right number of minutes, and would be black.</para>
    /// <para>So nothing is written at all until this is true, no encoder is even opened until this
    /// is true, and a save asked for while it is false is refused and says so. A moderator finding
    /// out at the moment they need the clip is the outcome the whole feature exists to avoid.</para>
    /// </remarks>
    public bool AnyPictureTaken { get; private set; }

    /// <summary>Whether this machine could record at all. Windows only; Linux has no path here.</summary>
    public static bool Supported => OperatingSystem.IsWindows();

    /// <param name="temporaryFolder">Where the two rolling files live. Emptied when recording stops.</param>
    public ScreenRecording(IModbotClock clock, string temporaryFolder, Action<string, Exception?> log)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _temporaryFolder = temporaryFolder;
        _log = log;
    }

    /// <summary>
    /// Starts keeping the last <paramref name="length"/> of VRChat's window and its sound. Returns
    /// false, having changed nothing, when this machine cannot do it; the reason is in
    /// <see cref="LastProblem"/>.
    /// </summary>
    /// <param name="discordSound">
    /// Whether Discord's sound goes into the clip beside VRChat's. Nothing else the machine is
    /// playing is ever recorded, whatever this says.
    /// </param>
    public bool Start(TimeSpan length, bool discordSound = false)
    {
        if (_thread is not null)
            return true;

        if (!Supported)
        {
            LastProblem = "Keeping the last few minutes only works on Windows.";
            return false;
        }

        _stopping = false;
        LastProblem = null;

        _thread = new Thread(() => Run(length, discordSound))
        {
            IsBackground = true,
            Name = "Modbot clips",

            // Below everything the game and the client's own loops do. A moderator's frame rate is
            // the thing this feature must not cost.
            Priority = ThreadPriority.BelowNormal,
        };

        _thread.Start();
        return true;
    }

    /// <summary>Stops recording and deletes the two rolling files. Safe when nothing is running.</summary>
    public void Stop()
    {
        if (_thread is not { } thread)
            return;

        _stopping = true;
        thread.Join(TimeSpan.FromSeconds(5));
        _thread = null;
        IsRecording = false;
        WindowFound = false;
        AnyPictureTaken = false;
        _soundRate = 0;
        EmptyTemporaryFolder();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Asks for the last few minutes to be written to <paramref name="destination"/>. The recording
    /// thread does it on its next turn; the outcome shows up in <see cref="LastSaved"/> or
    /// <see cref="LastProblem"/> within a moment.
    /// </summary>
    public void AskToSave(string destination)
    {
        lock (_gate)
            _saveTo = destination;
    }

    private string? TakeSaveRequest()
    {
        lock (_gate)
        {
            var wanted = _saveTo;
            _saveTo = null;
            return wanted;
        }
    }

    /// <summary>One rolling file, and the writer filling it.</summary>
    private sealed class Leg
    {
        public IMFSinkWriter? Writer;
        public int Stream;

        /// <summary>Where the sound goes, or -1 when this clip is a silent picture.</summary>
        public int SoundStream = -1;

        /// <summary>
        /// How much sound has gone into this file. What places the next stretch of it: the sound is
        /// positioned by how much of it there is, exactly as the picture is positioned by how many
        /// frames there have been, so the two describe the same moment however long the file runs.
        /// </summary>
        public long SoundSamples;

        public string Path = string.Empty;
        public long StartedAtFrame;
        public long RotateAtFrame;
        public int Serial;
        public int Index;
    }

    /// <summary>One monitor's place on the desktop, so a window can be found inside its picture.</summary>
    private readonly record struct Monitor(int Left, int Top, int Width, int Height);

    /// <summary>What one turn of the loop produced.</summary>
    private enum Grabbed
    {
        /// <summary>A fresh picture of VRChat's window is in hand.</summary>
        Picture,

        /// <summary>Nothing new. The picture already in hand stands, if there is one.</summary>
        Held,

        /// <summary>The duplication had to be taken again; this turn produced nothing at all.</summary>
        Nothing,
    }

    private void Run(TimeSpan length, bool discordSound)
    {
        if (!OperatingSystem.IsWindows())
            return;

        ClipSound? sound = null;
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGIOutputDuplication? duplication = null;
        ID3D11Texture2D? windowCopy = null;
        ID3D11ShaderResourceView? windowView = null;
        ID3D11Texture2D? staging = null;

        // Null until the first picture of VRChat's window lands. No encoder is opened and no file
        // is created before then, so a rolling file can never begin with frames of nothing.
        Leg[]? legs = null;

        try
        {
            StartMediaFoundationOnce();
            Directory.CreateDirectory(_temporaryFolder);

            // Nothing is recorded until Windows has handed over VRChat's window, because a clip is
            // that window and its size is what the encoder has to be told once and for good.
            var found = WaitForTheGameWindow();
            if (_stopping || found is not { } start)
                return;

            var (width, height) = ClipWindowRule.RecordedSize(start.Window.Width, start.Window.Height);

            // VRChat's own sound, and Discord's when that was switched on. Nothing else the machine
            // is playing, ever. A program that will not hand its sound over costs the clip that
            // program's sound and nothing else — the picture is recorded exactly as before.
            sound = ClipSound.Start(start.Owner, discordSound, _clock.UtcNow, _log);
            _soundRate = sound?.SampleRate ?? 0;

            // The graphics card that drives the screen VRChat is on, rather than whichever card
            // Windows lists first. On a laptop with two of them those are routinely not the same
            // card, and a duplication asked of the wrong one gives nothing that can be recorded.
            var (adapter, screen) = ChooseTheScreen(start.CentreX, start.CentreY);

            using (adapter)
            {
                D3D11.D3D11CreateDevice(
                    adapter,
                    adapter is null ? DriverType.Hardware : DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                    out device,
                    out _,
                    out context).CheckError();
            }

            _log(
                $"Recording on {screen.AdapterName ?? "the graphics card Windows chose"}, "
                + $"screen {screen.OutputName ?? "whichever that card lists first"}. "
                + $"VRChat's window is {start.Window.Width}×{start.Window.Height} at ({start.Left}, {start.Top})",
                null);

            var monitor = screen.Area;
            duplication = Duplicate(device!, ref monitor, start.CentreX, start.CentreY);

            var framesPerLeg = Math.Max(2, (long)(length.TotalSeconds * FramesPerSecond));

            IsRecording = true;
            _log($"Keeping the last {length.TotalMinutes:0} minutes of VRChat's window at {width}×{height}, {FramesPerSecond} frames a second", null);

            var pixels = new byte[width * height * 4];

            // Where VRChat's picture last landed in the frame. When it moves, the frame is painted
            // black once rather than on every turn.
            var drawnAt = default(ClipFit?);

            // One buffer for the sound that goes beside one picture, made once at the most any
            // picture can call for. Empty when this machine has no sound to record.
            var soundBytes = _soundRate == 0
                ? Array.Empty<byte>()
                : new byte[ClipSoundRule.MostSamplesInAFrame(_soundRate, FramesPerSecond) * ClipSoundRule.BytesPerSample];
            var clock = Stopwatch.StartNew();
            long frame = 0;
            var lookAgainAt = TimeSpan.Zero;
            var sayHowItIsGoingAt = SayHowItIsGoingEvery;
            var window = start;

            while (!_stopping)
            {
                // Paced off a monotonic timer rather than the machine's clock, so a clock change
                // part-way through an evening cannot make a clip run backwards.
                var dueAt = TimeSpan.FromTicks(frame * TimeSpan.TicksPerSecond / FramesPerSecond);
                var wait = dueAt - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                    Thread.Sleep(wait < TimeSpan.FromMilliseconds(2) ? 1 : (int)wait.TotalMilliseconds);

                // Where VRChat's window is now: moved, resized, minimised, behind something else,
                // or gone. Asked again only every half second while there is none, so a VRChat that
                // has closed does not cost a search fifteen times a second.
                if (window.Window.Found || clock.Elapsed >= lookAgainAt)
                {
                    window = VRChatWindow.Look(window.Handle);
                    if (!window.Window.Found)
                        lookAgainAt = clock.Elapsed + LookAgainEvery;
                }

                WindowFound = window.Window.Found;

                var grabbed = Grab(
                    duplication!,
                    context!,
                    device!,
                    ref monitor,
                    window,
                    frame,
                    width,
                    height,
                    pixels,
                    ref windowCopy,
                    ref windowView,
                    ref staging,
                    ref drawnAt,
                    ref duplication);

                if (grabbed is Grabbed.Picture && !AnyPictureTaken)
                {
                    AnyPictureTaken = true;
                    _log($"The first picture of VRChat's window landed after {clock.Elapsed.TotalSeconds:0.0} seconds", null);
                }

                if (clock.Elapsed >= sayHowItIsGoingAt)
                {
                    sayHowItIsGoingAt = clock.Elapsed + SayHowItIsGoingEvery;
                    SayHowItIsGoing(window);
                }

                var wanted = TakeSaveRequest();

                // Answered now rather than left waiting. A save that sat in hand until a picture
                // finally arrived would leave the panel saying nothing had happened and then put
                // a file on the disk minutes later.
                if (wanted is not null && !AnyPictureTaken)
                {
                    LastProblem = "Nothing has been recorded from VRChat's window yet.";
                    _log("A clip was asked for before any picture of VRChat's window had been captured", null);
                    wanted = null;
                }

                if (grabbed is Grabbed.Nothing)
                {
                    // This turn produced nothing to save from. The ask goes back rather than being
                    // dropped; the next turn is a fifteenth of a second away.
                    if (wanted is not null)
                        AskToSave(wanted);

                    frame++;
                    continue;
                }

                // Nothing is written until there is something real to write. Until the first
                // picture lands, what would go into the file is an empty buffer — a clip that
                // opens, runs for the right number of minutes, and is black.
                if (!AnyPictureTaken)
                {
                    frame++;
                    continue;
                }

                // The two rolling files start at the first picture rather than at the first turn
                // of the loop.
                if (legs is null)
                {
                    legs =
                    [
                        // The two are staggered by half the chosen length, so whichever has been
                        // running longer always holds between half and all of it.
                        OpenLeg(0, frame, frame + (framesPerLeg / 2), width, height),
                        OpenLeg(1, frame, frame + framesPerLeg, width, height),
                    ];

                    // Sound has been gathering since VRChat's window was found, which can be
                    // seconds before the first picture. Putting that in front of the first picture
                    // would be a clip whose sound runs ahead of it, so it is thrown away here and
                    // the two start level.
                    sound?.Forget();
                }

                // How much sound belongs with this one picture, worked out from running totals so
                // that it comes out exact however long the recording runs. One count serves both
                // rolling files, which is right because each keeps its own total of what it has
                // been given and the two rates a clip can be written at — 48,000 and 44,100 — both
                // divide evenly by fifteen pictures a second. Each file therefore starts its sound
                // at zero when it is started again, and stays level with its own picture.
                var soundForThisFrame = sound is null
                    ? 0
                    : ClipSoundRule.SamplesForFrame(frame, _soundRate, FramesPerSecond) * ClipSoundRule.BytesPerSample;

                sound?.Take(soundBytes, soundForThisFrame, _clock.UtcNow);

                foreach (var leg in legs)
                {
                    WriteFrame(leg, frame, pixels);
                    WriteSound(leg, soundBytes, soundForThisFrame, _soundRate);
                }

                _framesWritten++;

                if (wanted is not null)
                    Harvest(legs, frame, wanted, width, height, framesPerLeg);

                foreach (var leg in legs)
                {
                    if (frame < leg.RotateAtFrame)
                        continue;

                    leg.RotateAtFrame += framesPerLeg;
                    Finish(leg, discard: true);
                    Recycle(leg, width, height, frame);
                }

                frame++;
            }
        }
        catch (Exception ex)
        {
            // Every way this can fail — no Direct3D, no encoder, a monitor unplugged, a full disk —
            // ends here, as one line the settings screen shows. Presence reporting is untouched.
            LastProblem = Plainly(ex);
            _log("Keeping the last few minutes stopped", ex);
        }
        finally
        {
            IsRecording = false;
            WindowFound = false;
            AnyPictureTaken = false;

            _log(
                $"Recording ended: {_picturesCopied} pictures copied, {_framesHeld} frames held, "
                + $"{_framesWritten} frames written, {_nothingChanged} turns with nothing changed on screen, "
                + $"{_screenTakenAway} times the screen was taken away, "
                + $"{_windowOffThisScreen} turns with VRChat's window off this screen",
                null);

            if (legs is not null)
            {
                foreach (var leg in legs)
                    Finish(leg, discard: true);
            }

            sound?.Dispose();
            staging?.Dispose();
            windowView?.Dispose();
            windowCopy?.Dispose();
            duplication?.Dispose();
            context?.Dispose();
            device?.Dispose();
        }
    }

    /// <summary>
    /// Waits for VRChat's window to appear, giving up only when the recorder is being stopped.
    /// </summary>
    /// <remarks>
    /// VRChat's log starts moving a moment before, or a moment after, its window exists, and
    /// neither order is worth treating as a fault. Nothing is recorded in the meantime.
    /// </remarks>
    private GameWindowLook? WaitForTheGameWindow()
    {
        while (!_stopping)
        {
            var look = VRChatWindow.Look(0);
            WindowFound = look.Window.Found;

            if (look.Window.HasPicture)
                return look;

            Thread.Sleep(LookAgainEvery);
        }

        return null;
    }

    /// <summary>
    /// Media Foundation's encoders, started once for the life of the process. The light version,
    /// which is the one that does not bring up Media Foundation's networking.
    /// </summary>
    private static void StartMediaFoundationOnce()
    {
        if (_mediaFoundationStarted)
            return;

        MediaFactory.MFStartup(true).CheckError();
        _mediaFoundationStarted = true;
    }

    /// <summary>Which graphics card and which screen VRChat's window is on, and where it sits.</summary>
    /// <param name="AdapterName">The graphics card's own name, for the log file.</param>
    /// <param name="OutputName">Windows' own name for the screen, for the log file.</param>
    /// <param name="Area">Where that screen sits on the whole desktop.</param>
    private readonly record struct ScreenChoice(string AdapterName, string OutputName, Monitor Area);

    /// <summary>
    /// The graphics card driving the screen VRChat's window is on, and where that screen sits.
    /// </summary>
    /// <remarks>
    /// <para><strong>Why the card is chosen rather than taken.</strong> A screen can only be handed
    /// over by the card it is plugged into. Modbot used to make a graphics device with no card
    /// named — Windows picks one, usually the first — and then look for VRChat's screen among that
    /// card's screens. On a machine with one card those are the same thing. On a laptop with two,
    /// which is most gaming laptops and therefore a lot of moderators, they are routinely not:
    /// asking the wrong card gives either no screens at all or one Windows refuses to duplicate,
    /// and what came out was a failure at best and an empty picture at worst.</para>
    /// <para>So every card's screens are looked at, the one holding the middle of VRChat's window
    /// wins, and the graphics device is made on the card that owns it. A window whose middle is on
    /// no screen — dragged off the edge and left there — falls back to the first screen there is.
    /// Nothing here asks for a list of anybody's windows or of what is running; screens are not
    /// programs.</para>
    /// <para>The caller owns the card that comes back and must dispose it. Null means the cards
    /// could not be listed at all, and then the device is made the old way and Windows chooses.</para>
    /// </remarks>
    private (IDXGIAdapter1? Adapter, ScreenChoice Screen) ChooseTheScreen(int pointX, int pointY)
    {
        var fallback = default((uint Adapter, ScreenChoice Screen)?);

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint index = 0; factory.EnumAdapters1(index, out var card).Success && card is not null; index++)
            {
                using (card)
                {
                    var cardName = card.Description1.Description;

                    for (uint at = 0; card.EnumOutputs(at, out var output).Success && output is not null; at++)
                    {
                        using (output)
                        {
                            var told = output.Description;
                            if (!told.AttachedToDesktop)
                                continue;

                            var area = told.DesktopCoordinates;
                            var screen = new ScreenChoice(
                                cardName,
                                told.DeviceName,
                                new Monitor(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top));

                            _log($"Found screen {told.DeviceName} on {cardName} at ({area.Left}, {area.Top}) {area.Right - area.Left}×{area.Bottom - area.Top}", null);

                            fallback ??= (index, screen);

                            if (pointX >= area.Left && pointX < area.Right
                                && pointY >= area.Top && pointY < area.Bottom)
                            {
                                fallback = (index, screen);
                                return (Card(factory, index), screen);
                            }
                        }
                    }
                }
            }

            if (fallback is { } only)
            {
                _log($"VRChat's window is not on any screen Modbot can see; falling back to {only.Screen.OutputName}", null);
                return (Card(factory, only.Adapter), only.Screen);
            }

            throw new InvalidOperationException("This machine has no screen Modbot can record.");
        }
        catch (SharpGenException ex)
        {
            // Listing the cards is not itself the recording, so a machine that will not do it is
            // given the old answer — Windows picks a card — rather than being stopped here.
            _log("Windows would not list this machine's graphics cards", ex);
            return (null, default);
        }

        static IDXGIAdapter1? Card(IDXGIFactory1 factory, uint index)
            => factory.EnumAdapters1(index, out var card).Success ? card : null;
    }

    /// <summary>
    /// The duplication of the monitor VRChat's window is on. Windows hands over a copy of what the
    /// desktop compositor already drew; nothing here draws, and nothing asks for a list of
    /// anybody's windows.
    /// </summary>
    /// <remarks>
    /// The monitor is chosen by which one holds the middle of VRChat's window, among the monitors
    /// the graphics device's own card drives — which is the card that screen is plugged into,
    /// because <see cref="ChooseTheScreen"/> made the device on it. A window whose middle is on
    /// none of them falls back to the first.
    /// </remarks>
    private IDXGIOutputDuplication Duplicate(
        ID3D11Device device,
        ref Monitor monitor,
        int pointX,
        int pointY)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();

        var monitors = new List<IDXGIOutput>();

        for (uint index = 0; adapter.EnumOutputs(index, out var output).Success && output is not null; index++)
            monitors.Add(output);

        if (monitors.Count == 0)
            throw new InvalidOperationException("This machine has no monitor Modbot can record.");

        try
        {
            var chosen = monitors.FirstOrDefault(Holds) ?? monitors[0];
            var area = chosen.Description.DesktopCoordinates;

            monitor = new Monitor(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top);

            using var output1 = chosen.QueryInterface<IDXGIOutput1>();

            try
            {
                return output1.DuplicateOutput(device);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == CannotBeHandedOver)
            {
                // Windows refuses this screen outright. A graphics card that does not own it, or a
                // game holding the screen in the old exclusive full-screen way, both end here.
                throw new InvalidOperationException(
                    "Windows would not hand Modbot a picture of the screen VRChat is on.", ex);
            }
        }
        finally
        {
            foreach (var one in monitors)
                one.Dispose();
        }

        bool Holds(IDXGIOutput output)
        {
            var area = output.Description.DesktopCoordinates;
            return pointX >= area.Left && pointX < area.Right
                && pointY >= area.Top && pointY < area.Bottom;
        }
    }

    /// <summary>
    /// What the recorder is actually doing, once a minute, in the client's own log file.
    /// </summary>
    /// <remarks>
    /// A clip that turns out black looks exactly like a clip that turned out fine until somebody
    /// opens it, and the file itself says nothing about why. These counts do: no pictures copied
    /// with VRChat never in front is one cause, no pictures copied with VRChat in front the whole
    /// time is a different one, and pictures copied with the window off this screen is a third.
    /// </remarks>
    private void SayHowItIsGoing(GameWindowLook window)
        => _log(
            $"Keeping the last few minutes: {_picturesCopied} pictures copied, {_framesHeld} held, "
            + $"{_framesWritten} written. VRChat's window "
            + (window.Window.Found ? "is there" : "is not there")
            + (window.Window.InFront ? ", in front" : ", not in front")
            + (window.Window.Minimised ? ", minimised" : string.Empty)
            + $", {window.Window.Width}×{window.Window.Height} at ({window.Left}, {window.Top})",
            null);

    /// <summary>One rolling file, opened and started.</summary>
    private Leg OpenLeg(int index, long frame, long rotateAtFrame, int width, int height)
    {
        var leg = new Leg { Index = index, RotateAtFrame = rotateAtFrame };
        Recycle(leg, width, height, frame);
        return leg;
    }

    /// <summary>
    /// The texture the processor reads the shrunk picture out of, remade when the size the
    /// graphics card hands over changes.
    /// </summary>
    /// <remarks>
    /// It is the size of the card's own smaller copy rather than the size of the frame, because
    /// the card can only halve and the last step down to the frame is the processor's. In the
    /// ordinary case — a window that has not changed size — those are the same number and nothing
    /// extra is read back at all.
    /// </remarks>
    private static void MakeStaging(ID3D11Device device, int width, int height, ref ID3D11Texture2D? texture)
    {
        if (texture is not null && texture.Description.Width == (uint)width && texture.Description.Height == (uint)height)
            return;

        texture?.Dispose();

        texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    /// <summary>
    /// A texture the size of VRChat's window, with a chain of smaller copies the graphics card
    /// makes itself. Remade when the window's size changes, which is the cheap half of a resize —
    /// the encoder is never disturbed, so the clip carries on being one file.
    /// </summary>
    private static void MakeWindowCopy(
        ID3D11Device device,
        int width,
        int height,
        ref ID3D11Texture2D? texture,
        ref ID3D11ShaderResourceView? view)
    {
        if (texture is not null && texture.Description.Width == (uint)width && texture.Description.Height == (uint)height)
            return;

        view?.Dispose();
        texture?.Dispose();

        texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 0,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });

        view = device.CreateShaderResourceView(texture);
    }

    /// <summary>
    /// One frame: ask Windows for the desktop's own picture, take the part of it VRChat is drawn
    /// in, let the graphics card shrink that, and copy the small result into
    /// <paramref name="pixels"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Grabbed.Picture"/> means a fresh picture of VRChat is in
    /// <paramref name="pixels"/>. <see cref="Grabbed.Held"/> means there was nothing new to copy —
    /// VRChat is not the window in front, or nothing moved on the screen — so the last picture
    /// stands. <see cref="Grabbed.Nothing"/> means the duplication had to be taken again and this
    /// turn produced nothing at all, which is ordinary.
    /// </remarks>
    private Grabbed Grab(
        IDXGIOutputDuplication duplication,
        ID3D11DeviceContext context,
        ID3D11Device device,
        ref Monitor monitor,
        GameWindowLook window,
        long frame,
        int width,
        int height,
        byte[] pixels,
        ref ID3D11Texture2D? windowCopy,
        ref ID3D11ShaderResourceView? windowView,
        ref ID3D11Texture2D? staging,
        ref ClipFit? drawnAt,
        ref IDXGIOutputDuplication? live)
    {
        // VRChat minimised, closed, or behind whatever the moderator has alt-tabbed to. The last
        // picture of VRChat is written again: the clip keeps its steady rate and holds nothing of
        // what they moved to. Until there has been a first picture there is nothing to write
        // again, which is what the caller checks before writing anything at all.
        if (ClipWindowRule.Decide(window.Window) is ClipFrame.HoldLastPicture)
        {
            _framesHeld++;
            return Grabbed.Held;
        }

        var box = ClipWindowRule.BoxOnMonitor(
            window.Left, window.Top, window.Window.Width, window.Window.Height,
            monitor.Left, monitor.Top, monitor.Width, monitor.Height);

        if (box.Width < 2 || box.Height < 2)
        {
            _windowOffThisScreen++;

            // None of VRChat's window is on the monitor being read — it has been dragged to
            // another screen. Follow it, at most once a second, so a window that is genuinely on
            // no monitor does not cost a duplication fifteen times a second.
            if (frame >= _followTheWindowAtFrame)
            {
                _followTheWindowAtFrame = frame + FramesPerSecond;

                _log(
                    $"VRChat's window is {window.Window.Width}×{window.Window.Height} at "
                    + $"({window.Left}, {window.Top}), off the screen being recorded at "
                    + $"({monitor.Left}, {monitor.Top}) {monitor.Width}×{monitor.Height}; following it",
                    null);

                // Let go of the old one before asking for the new one, so a machine that refuses
                // the second ask ends with nothing held rather than with something disposed.
                live = null;
                duplication.Dispose();
                live = Duplicate(device, ref monitor, window.CentreX, window.CentreY);
            }

            _framesHeld++;
            return Grabbed.Held;
        }

        // No patience at all once a picture has landed: nothing having changed on screen is an
        // ordinary answer and the last picture is written again. Patience until then, because the
        // first picture is the one nothing can be saved without.
        var result = duplication.AcquireNextFrame(
            AnyPictureTaken ? 0u : WaitForTheFirstPictureMs,
            out _,
            out var desktop);

        if (result.Code == WaitTimeout)
        {
            // Nothing moved on the screen. The frame already in hand is written again by the
            // caller's schedule, so the clip keeps running at a steady rate.
            _nothingChanged++;
            _framesHeld++;
            return Grabbed.Held;
        }

        if (result.Code == AccessLost)
        {
            // A full-screen game starting, a resolution change, or another program taking the
            // duplication. Ask again; this is ordinary and happens several times an evening. The
            // monitor is worked out again with it, because a resolution or arrangement change is
            // one of the things that causes this.
            _screenTakenAway++;
            live = null;
            duplication.Dispose();
            live = Duplicate(device, ref monitor, window.CentreX, window.CentreY);
            return Grabbed.Nothing;
        }

        result.CheckError();

        try
        {
            using var desktopPicture = desktop.QueryInterface<ID3D11Texture2D>();

            MakeWindowCopy(device, box.Width, box.Height, ref windowCopy, ref windowView);

            context.CopySubresourceRegion(
                windowCopy!, 0, 0, 0, 0, desktopPicture, 0,
                new Vortice.Mathematics.Box(box.X, box.Y, 0, box.X + box.Width, box.Y + box.Height, 1));

            context.GenerateMips(windowView!);

            // Where VRChat's picture goes in the frame, and which of the card's ready-made smaller
            // copies to read it from. The card halves as far as it can for free; the last step —
            // less than a halving, and none at all when the window has not changed size — is the
            // processor's, in ClipPicture.
            var fit = ClipWindowRule.Fit(box.Width, box.Height, width, height);

            MakeStaging(device, fit.SourceWidth, fit.SourceHeight, ref staging);

            context.CopySubresourceRegion(
                staging!, 0, 0, 0, 0, windowCopy!, (uint)fit.Level,
                new Vortice.Mathematics.Box(0, 0, 0, fit.SourceWidth, fit.SourceHeight, 1));

            // A window whose shape no longer matches the frame's leaves a strip over. It is
            // painted black once, when it moves, rather than on every turn.
            if (drawnAt != fit)
            {
                Array.Clear(pixels);
                drawnAt = fit;

                // Once a second at most: a window being dragged by its corner changes this fifteen
                // times a second and a line each would bury everything else in the file.
                if (frame >= _sayTheFitAtFrame)
                {
                    _sayTheFitAtFrame = frame + FramesPerSecond;

                    _log(
                        $"VRChat's window is {box.Width}×{box.Height}; drawing it {fit.Width}×{fit.Height} "
                        + $"at ({fit.Left}, {fit.Top}) in a {width}×{height} frame, "
                        + $"from a {fit.SourceWidth}×{fit.SourceHeight} copy",
                        null);
                }
            }

            var mapped = context.Map(staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe
                {
                    var copy = new ReadOnlySpan<byte>(
                        (void*)mapped.DataPointer, (int)mapped.RowPitch * fit.SourceHeight);

                    ClipPicture.DrawInto(
                        copy,
                        (int)mapped.RowPitch,
                        fit.SourceWidth,
                        fit.SourceHeight,
                        pixels,
                        width * 4,
                        fit.Left,
                        fit.Top,
                        fit.Width,
                        fit.Height);
                }
            }
            finally
            {
                context.Unmap(staging!, 0);
            }
        }
        finally
        {
            desktop.Dispose();
            duplication.ReleaseFrame();
        }

        // The first time this line is reached, a picture of VRChat's window exists. Before it,
        // nothing may be written: there is nothing to write except an empty buffer.
        _picturesCopied++;

        if (_picturesCopied == 1)
        {
            _log(
                $"Copying VRChat's window from ({box.X}, {box.Y}) {box.Width}×{box.Height} "
                + $"on the screen at ({monitor.Left}, {monitor.Top}) {monitor.Width}×{monitor.Height}",
                null);
        }

        return Grabbed.Picture;
    }

    /// <summary>Puts one frame into one rolling file.</summary>
    private static void WriteFrame(Leg leg, long frame, byte[] pixels)
    {
        if (leg.Writer is not { } writer)
            return;

        using var buffer = MediaFactory.MFCreateMemoryBuffer(pixels.Length);
        buffer.Lock(out var target, out _, out _);
        Marshal.Copy(pixels, 0, target, pixels.Length);
        buffer.Unlock();
        buffer.CurrentLength = pixels.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = (frame - leg.StartedAtFrame) * FrameDuration;
        sample.SampleDuration = FrameDuration;

        writer.WriteSample(leg.Stream, sample);
    }

    /// <summary>Puts the sound that belongs with one picture into one rolling file.</summary>
    /// <remarks>
    /// <para>Where it goes in the file is worked out from how much sound has already gone in,
    /// never from a clock — so the sound is placed by how much of it there is, exactly as the
    /// picture is placed by how many frames there have been, and the two cannot drift apart
    /// (<see cref="ClipSoundRule"/>).</para>
    /// <para>A file with no sound track — this machine could not hand over one program's sound, or
    /// nothing could be opened — passes straight through here, and what comes out is a silent clip
    /// rather than a failed one.</para>
    /// </remarks>
    private static void WriteSound(Leg leg, byte[] bytes, int count, int sampleRate)
    {
        if (leg.Writer is not { } writer || leg.SoundStream < 0 || count <= 0 || sampleRate <= 0)
            return;

        var samples = count / ClipSoundRule.BytesPerSample;

        using var buffer = MediaFactory.MFCreateMemoryBuffer(count);
        buffer.Lock(out var target, out _, out _);
        Marshal.Copy(bytes, 0, target, count);
        buffer.Unlock();
        buffer.CurrentLength = count;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = ClipSoundRule.TimeFor(leg.SoundSamples, sampleRate);
        sample.SampleDuration = ClipSoundRule.TimeFor(samples, sampleRate);

        writer.WriteSample(leg.SoundStream, sample);

        leg.SoundSamples += samples;
    }

    /// <summary>
    /// Saves the last few minutes: the rolling file that has been running longer is closed, moved
    /// into the moderator's clips folder, and started again.
    /// </summary>
    /// <remarks>
    /// The longer-running of the two is the one that holds the most history — between half and all
    /// of the chosen length. Saving twice within a minute gives a shorter second clip, because the
    /// first save is what started that file.
    /// </remarks>
    private void Harvest(Leg[] legs, long frame, string destination, int width, int height, long framesPerLeg)
    {
        var oldest = legs[0].StartedAtFrame <= legs[1].StartedAtFrame ? legs[0] : legs[1];

        if (oldest.Writer is null || oldest.Path.Length == 0)
        {
            // Asked for before anything was being kept. Nothing to move, and saying so beats
            // moving a file that is not there.
            LastProblem = "Nothing has been recorded yet.";
            return;
        }

        if (!AnyPictureTaken)
        {
            // Belt and braces: the caller already refuses a save with no picture behind it, and
            // this is the second place that has to be true before a file with nothing in it can
            // be handed to somebody as a clip.
            LastProblem = "Nothing has been recorded from VRChat's window yet.";
            return;
        }

        try
        {
            var from = oldest.Path;
            _log($"Saving a clip of {frame - oldest.StartedAtFrame + 1} frames", null);
            Finish(oldest, discard: false);

            if (Path.GetDirectoryName(destination) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);

            File.Move(from, destination, overwrite: true);

            LastSaved = Path.GetFileName(destination);
            LastProblem = null;
            _log($"Saved a clip of the last few minutes to {destination}", null);
        }
        catch (Exception ex)
        {
            LastProblem = Plainly(ex);
            _log("A clip could not be saved", ex);
        }
        finally
        {
            // Whether or not the move worked, this file is gone and the leg needs a fresh one, or
            // nothing is being kept from here on.
            if (oldest.RotateAtFrame - frame < framesPerLeg / 4)
                oldest.RotateAtFrame += framesPerLeg;

            Recycle(oldest, width, height, frame);
        }
    }

    /// <summary>Closes a rolling file, and deletes it unless it is about to be saved.</summary>
    private void Finish(Leg leg, bool discard)
    {
        if (leg.Writer is { } writer)
        {
            try
            {
                writer.Finalize();
            }
            catch (Exception ex)
            {
                _log("A rolling recording could not be closed cleanly", ex);
            }

            writer.Dispose();
            leg.Writer = null;
        }

        if (!discard || leg.Path.Length == 0)
            return;

        try
        {
            File.Delete(leg.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It will be deleted when recording stops and the folder is emptied.
        }

        leg.Path = string.Empty;
    }

    /// <summary>Starts a fresh rolling file for one leg.</summary>
    private void Recycle(Leg leg, int width, int height, long frame)
    {
        leg.Serial++;
        leg.StartedAtFrame = frame;
        leg.SoundSamples = 0;
        leg.SoundStream = -1;
        leg.Path = Path.Combine(_temporaryFolder, $"rolling-{leg.Index}-{leg.Serial}{ClipLibrary.ClipExtension}");

        using var attributes = MediaFactory.MFCreateAttributes(4);

        // Use the graphics card's own encoder when there is one. That is the difference between
        // this costing a percent of a core and costing several.
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteDisableConverters, 0u);

        var writer = MediaFactory.MFCreateSinkWriterFromURL(leg.Path, null, attributes);

        using var output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)Bitrate);
        output.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        output.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
        output.Set(MediaTypeAttributeKeys.FrameRate, Pack(FramesPerSecond, 1));
        output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        output.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)(FramesPerSecond * KeyFrameEverySeconds));

        leg.Stream = writer.AddStream(output);

        using var input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        input.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
        input.Set(MediaTypeAttributeKeys.FrameRate, Pack(FramesPerSecond, 1));
        input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));

        // Positive, because Direct3D hands over rows from the top down and Media Foundation would
        // otherwise assume the bottom-up order a Windows bitmap uses and write the clip upside down.
        input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4));

        writer.SetInputMediaType(leg.Stream, input, null);

        // The sound, beside the picture, in the same file. Windows' own encoder again — nothing is
        // shipped for this and no other program is started. A clip with no sound to put in it gets
        // no sound track at all, which plays everywhere as a silent video rather than as a broken
        // one.
        if (_soundRate > 0)
        {
            try
            {
                AddSound(writer, leg, _soundRate);
            }
            catch (Exception ex)
            {
                // A machine whose encoder will not take the sound gets a clip of the picture
                // rather than no clip. Said once: the rate goes to zero, so the rolling files made
                // after this one are silent too rather than trying and failing every few minutes.
                leg.SoundStream = -1;
                _soundRate = 0;
                _log("The clip's sound could not be set up on this machine, so clips are silent.", ex);
            }
        }

        writer.BeginWriting();

        leg.Writer = writer;
    }

    /// <summary>Adds the sound track to one rolling file, and says where to write it.</summary>
    /// <remarks>
    /// <para>What goes in is plain sound as Windows handed it over — two channels, sixteen bits, at
    /// the rate the programs were opened at. What comes out is AAC, which is what an <c>.mp4</c>
    /// carries and what every player and every browser can open, at 128 kilobits a second: about a
    /// megabyte a minute, beside the picture's eleven.</para>
    /// <para>The sound is added after the picture, so the picture is the first track in the file.
    /// Both are added before writing starts, because a video file cannot grow a track part way
    /// through.</para>
    /// </remarks>
    private static void AddSound(IMFSinkWriter writer, Leg leg, int sampleRate)
    {
        // 16,000 bytes a second — 128 kilobits — which is one of the handful of rates Windows'
        // own AAC encoder accepts. A rate it does not accept is not a worse clip; it is no clip.
        const int BytesASecond = 16_000;

        // AAC Profile L2, which is what Windows' encoder produces for two channels at these rates.
        const uint ProfileLevel = 0x29;

        using var output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)(ClipSoundRule.BytesPerChannel * 8));
        output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
        output.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)ClipSoundRule.Channels);
        output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)BytesASecond);
        output.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
        output.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, ProfileLevel);

        leg.SoundStream = writer.AddStream(output);

        using var input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
        input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)(ClipSoundRule.BytesPerChannel * 8));
        input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
        input.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)ClipSoundRule.Channels);
        input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)ClipSoundRule.BytesPerSample);
        input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(sampleRate * ClipSoundRule.BytesPerSample));

        writer.SetInputMediaType(leg.SoundStream, input, null);
    }

    /// <summary>Two numbers in the one value Media Foundation stores a size or a rate as.</summary>
    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    private void EmptyTemporaryFolder()
    {
        try
        {
            if (!Directory.Exists(_temporaryFolder))
                return;

            foreach (var path in Directory.EnumerateFiles(_temporaryFolder))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Next time.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Next time.
        }
    }

    /// <summary>One sentence a moderator can act on, rather than a stack trace.</summary>
    private static string Plainly(Exception ex) => ex switch
    {
        DllNotFoundException => "This machine is missing the Windows parts Modbot records with.",
        SharpGenException => "Windows would not start a recording on this machine: " + ex.Message,
        UnauthorizedAccessException => "Modbot is not allowed to write where clips are kept.",
        IOException => "Writing the recording failed: " + ex.Message,
        _ => ex.Message,
    };

    /// <summary>The moment a clip was saved, for whoever wants to name it. Never the system clock.</summary>
    public DateTimeOffset Now => _clock.UtcNow;

    /// <summary>VRChat's window as Windows last described it, and where it is on the desktop.</summary>
    /// <param name="Handle">What Windows calls that window, or zero when there is none.</param>
    /// <param name="Window">Whether it is there, in front, minimised, and how big its picture is.</param>
    /// <param name="Left">Where its picture starts across the whole desktop.</param>
    /// <param name="Top">Where its picture starts down the whole desktop.</param>
    /// <param name="Owner">
    /// Which process drew that window, or zero when Windows would not say. It is asked of the one
    /// window that was already found by name, never of a list, and it is the whole of how the sound
    /// in a clip can be VRChat's rather than the machine's.
    /// </param>
    internal readonly record struct GameWindowLook(
        nint Handle,
        GameWindow Window,
        int Left,
        int Top,
        uint Owner = 0)
    {
        public int CentreX => Left + (Window.Width / 2);

        public int CentreY => Top + (Window.Height / 2);
    }

    /// <summary>
    /// The one place in Modbot's client that asks Windows about another program's window.
    /// </summary>
    /// <remarks>
    /// <para>It asks for <em>one named window</em> — VRChat's — and never for a list. The client
    /// does not enumerate windows and does not enumerate processes, and
    /// <c>CompanionSourceGuardTests</c> fails the build if any other file the client ships learns
    /// any of these calls.</para>
    /// <para>What comes back is where VRChat is drawn and whether it is the window in front. That
    /// is what keeps a clip to VRChat: the recorder copies that rectangle and nothing else, and
    /// writes the last picture of VRChat again while the moderator is working somewhere else.</para>
    /// </remarks>
    private static class VRChatWindow
    {
        /// <summary>The window class every Unity game's main window has.</summary>
        private const string UnityWindowClass = "UnityWndClass";

        /// <summary>VRChat's own window title.</summary>
        private const string Title = "VRChat";

        /// <summary>Where VRChat's window is right now. A handle of zero asks Windows for it afresh.</summary>
        public static GameWindowLook Look(nint known)
        {
            if (!OperatingSystem.IsWindows())
                return new GameWindowLook(0, GameWindow.Missing, 0, 0);

            var handle = known != 0 && IsWindow(known) ? known : Find();

            if (handle == 0)
                return new GameWindowLook(0, GameWindow.Missing, 0, 0);

            if (!GetClientRect(handle, out var client))
                return new GameWindowLook(handle, GameWindow.Missing with { Found = true }, 0, 0);

            var corner = new Point { X = client.Left, Y = client.Top };
            if (!ClientToScreen(handle, ref corner))
                return new GameWindowLook(handle, GameWindow.Missing with { Found = true }, 0, 0);

            // Which process drew this one window. One more named ask about the window already
            // found by name, and never a walk over what else is running: it is what lets the sound
            // in a clip be VRChat's own rather than everything the speakers are playing.
            _ = GetWindowThreadProcessId(handle, out var owner);

            return new GameWindowLook(
                handle,
                new GameWindow(
                    Found: true,
                    InFront: GetForegroundWindow() == handle,
                    Minimised: IsIconic(handle),
                    Width: client.Right - client.Left,
                    Height: client.Bottom - client.Top),
                corner.X,
                corner.Y,
                owner);
        }

        /// <summary>
        /// VRChat's window, by class and title, and then by title alone. Two named asks, never a
        /// walk over what else is open.
        /// </summary>
        private static nint Find()
        {
            var handle = FindWindowW(UnityWindowClass, Title);
            return handle != 0 ? handle : FindWindowW(null, Title);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint FindWindowW(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(nint hWnd);

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(nint hWnd, out Rect lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(nint hWnd, ref Point lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    }
}
