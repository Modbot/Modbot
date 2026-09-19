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
/// <para><strong>What it records: VRChat's window.</strong> Not the monitor. This file asks Windows
/// for VRChat's own window by name, takes the part of the screen that window is drawn in, and
/// records that, shrunk to at most 1280 pixels wide, at 15 frames a second. It does not ask Windows
/// for a list of the programs that are running or a list of their windows, and it never has.</para>
/// <para><strong>What can still end up in a clip.</strong> Windows hands this file a copy of what
/// the desktop already drew, and what the desktop drew inside VRChat's rectangle is whatever is on
/// top of it — a chat program's in-game overlay, a notification, Modbot's own panel over the game.
/// Those are in the clip. What is not is everything outside that rectangle, and everything on the
/// screen while VRChat is behind another program: while a moderator is working in something else,
/// the last picture of VRChat is written again rather than what they have moved to. So a clip is
/// VRChat and what was drawn over VRChat, and it is never the rest of their screen.</para>
/// <para><strong>No sound.</strong> The ban on every recording API for microphones, line-in and
/// loopback stands untouched, so voice chat stays in the never-recorded column. No keyboard, no
/// clipboard, no file anywhere else on the disk.</para>
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

    /// <summary>How often Windows is asked again for VRChat's window while there is not one.</summary>
    private static readonly TimeSpan LookAgainEvery = TimeSpan.FromMilliseconds(500);

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
    /// Starts keeping the last <paramref name="length"/> of VRChat's window. Returns false, having
    /// changed nothing, when this machine cannot do it; the reason is in <see cref="LastProblem"/>.
    /// </summary>
    public bool Start(TimeSpan length)
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

        _thread = new Thread(() => Run(length))
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
        public string Path = string.Empty;
        public long StartedAtFrame;
        public long RotateAtFrame;
        public int Serial;
        public int Index;
    }

    /// <summary>One monitor's place on the desktop, so a window can be found inside its picture.</summary>
    private readonly record struct Monitor(int Left, int Top, int Width, int Height);

    private void Run(TimeSpan length)
    {
        if (!OperatingSystem.IsWindows())
            return;

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGIOutputDuplication? duplication = null;
        ID3D11Texture2D? windowCopy = null;
        ID3D11ShaderResourceView? windowView = null;
        ID3D11Texture2D? staging = null;
        var legs = new Leg[2];

        try
        {
            StartMediaFoundationOnce();
            Directory.CreateDirectory(_temporaryFolder);

            D3D11.D3D11CreateDevice(
                adapter: null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out device,
                out _,
                out context).CheckError();

            // Nothing is recorded until Windows has handed over VRChat's window, because a clip is
            // that window and its size is what the encoder has to be told once and for good.
            var found = WaitForTheGameWindow();
            if (_stopping || found is not { } start)
                return;

            var (width, height) = ClipWindowRule.RecordedSize(start.Window.Width, start.Window.Height);

            // The monitor VRChat is on, rather than whichever monitor happens to be first.
            Monitor monitor;
            (duplication, monitor) = Duplicate(device!, start.CentreX, start.CentreY);

            staging = MakeStaging(device!, width, height);

            var framesPerLeg = Math.Max(2, (long)(length.TotalSeconds * FramesPerSecond));

            for (var index = 0; index < legs.Length; index++)
            {
                legs[index] = new Leg
                {
                    Index = index,

                    // The two are staggered by half the chosen length, so whichever has been
                    // running longer always holds between half and all of it.
                    RotateAtFrame = index == 0 ? framesPerLeg / 2 : framesPerLeg,
                };

                Recycle(legs[index], width, height, 0);
            }

            IsRecording = true;
            _log($"Keeping the last {length.TotalMinutes:0} minutes of VRChat's window at {width}×{height}, {FramesPerSecond} frames a second", null);

            var pixels = new byte[width * height * 4];
            var filled = (Width: 0, Height: 0);
            var clock = Stopwatch.StartNew();
            long frame = 0;
            var lookAgainAt = TimeSpan.Zero;
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
                    staging!,
                    ref filled,
                    ref duplication);

                if (!grabbed)
                {
                    frame++;
                    continue;
                }

                foreach (var leg in legs)
                    WriteFrame(leg, frame, pixels);

                if (TakeSaveRequest() is { } destination)
                    Harvest(legs, frame, destination, width, height, framesPerLeg);

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

            foreach (var leg in legs)
            {
                if (leg is not null)
                    Finish(leg, discard: true);
            }

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

    /// <summary>
    /// The duplication of the monitor VRChat's window is on, and where that monitor sits on the
    /// desktop. Windows hands over a copy of what the desktop compositor already drew; nothing here
    /// draws, and nothing asks for a list of anybody's windows.
    /// </summary>
    /// <remarks>
    /// The monitor is chosen by which one holds the middle of VRChat's window, so a moderator with
    /// two screens gets the one the game is on rather than whichever the graphics card lists first.
    /// A window whose middle is on no monitor — dragged off the edge — falls back to the first.
    /// </remarks>
    private static (IDXGIOutputDuplication Duplication, Monitor Monitor) Duplicate(
        ID3D11Device device,
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

            using var output1 = chosen.QueryInterface<IDXGIOutput1>();

            return (
                output1.DuplicateOutput(device),
                new Monitor(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top));
        }
        finally
        {
            foreach (var monitor in monitors)
                monitor.Dispose();
        }

        bool Holds(IDXGIOutput output)
        {
            var area = output.Description.DesktopCoordinates;
            return pointX >= area.Left && pointX < area.Right
                && pointY >= area.Top && pointY < area.Bottom;
        }
    }

    /// <summary>The texture the processor reads the finished frame out of.</summary>
    private static ID3D11Texture2D MakeStaging(ID3D11Device device, int width, int height)
        => device.CreateTexture2D(new Texture2DDescription
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
    /// True means the caller should write a frame — either the one just copied, or the last one
    /// again when there was nothing new or VRChat is not the window in front. False means the
    /// duplication had to be taken again and this turn produced nothing, which is ordinary.
    /// </remarks>
    private bool Grab(
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
        ID3D11Texture2D staging,
        ref (int Width, int Height) filled,
        ref IDXGIOutputDuplication? live)
    {
        // VRChat minimised, closed, or behind whatever the moderator has alt-tabbed to. The last
        // picture of VRChat is written again: the clip keeps its steady rate and holds nothing of
        // what they moved to.
        if (ClipWindowRule.Decide(window.Window) is ClipFrame.HoldLastPicture)
            return true;

        var box = ClipWindowRule.BoxOnMonitor(
            window.Left, window.Top, window.Window.Width, window.Window.Height,
            monitor.Left, monitor.Top, monitor.Width, monitor.Height);

        if (box.Width < 2 || box.Height < 2)
        {
            // None of VRChat's window is on the monitor being read — it has been dragged to
            // another screen. Follow it, at most once a second, so a window that is genuinely on
            // no monitor does not cost a duplication fifteen times a second.
            if (frame >= _followTheWindowAtFrame)
            {
                _followTheWindowAtFrame = frame + FramesPerSecond;

                // Let go of the old one before asking for the new one, so a machine that refuses
                // the second ask ends with nothing held rather than with something disposed.
                live = null;
                duplication.Dispose();
                (live, monitor) = Duplicate(device, window.CentreX, window.CentreY);
            }

            return true;
        }

        var result = duplication.AcquireNextFrame(0, out _, out var desktop);

        if (result.Code == WaitTimeout)
        {
            // Nothing moved on the screen. The frame already in hand is written again by the
            // caller's schedule, so the clip keeps running at a steady rate.
            return true;
        }

        if (result.Code == AccessLost)
        {
            // A full-screen game starting, a resolution change, or another program taking the
            // duplication. Ask again; this is ordinary and happens several times an evening. The
            // monitor is worked out again with it, because a resolution or arrangement change is
            // one of the things that causes this.
            live = null;
            duplication.Dispose();
            (live, monitor) = Duplicate(device, window.CentreX, window.CentreY);
            return false;
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

            var level = ClipWindowRule.FitLevel(box.Width, box.Height, width, height);
            var (fitWidth, fitHeight) = ClipWindowRule.FittedSize(box.Width, box.Height, width, height, level);

            context.CopySubresourceRegion(
                staging, 0, 0, 0, 0, windowCopy!, (uint)level,
                new Vortice.Mathematics.Box(0, 0, 0, fitWidth, fitHeight, 1));

            // A window that is now smaller fills less of the frame. What is left is painted black
            // rather than left holding the edges of the picture that was there before.
            if (filled.Width != fitWidth || filled.Height != fitHeight)
            {
                Array.Clear(pixels);
                filled = (fitWidth, fitHeight);
            }

            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var row = fitWidth * 4;
                for (var y = 0; y < fitHeight; y++)
                {
                    Marshal.Copy(
                        mapped.DataPointer + (y * (int)mapped.RowPitch),
                        pixels,
                        y * width * 4,
                        row);
                }
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
        finally
        {
            desktop.Dispose();
            duplication.ReleaseFrame();
        }

        return true;
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

        try
        {
            var from = oldest.Path;
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
        writer.BeginWriting();

        leg.Writer = writer;
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
    internal readonly record struct GameWindowLook(
        nint Handle,
        GameWindow Window,
        int Left,
        int Top)
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

            return new GameWindowLook(
                handle,
                new GameWindow(
                    Found: true,
                    InFront: GetForegroundWindow() == handle,
                    Minimised: IsIconic(handle),
                    Width: client.Right - client.Left,
                    Height: client.Bottom - client.Top),
                corner.X,
                corner.Y);
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
    }
}
