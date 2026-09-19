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
/// <para><strong>What it records.</strong> The picture on one monitor — the one chosen on the
/// settings screen — shrunk to at most 1280 pixels wide, at 15 frames a second. No sound: the ban on
/// every recording API for microphones, line-in and loopback stands untouched, so voice chat stays in
/// the never-recorded column. No keyboard, no clipboard, no other program's window list, no file
/// anywhere else on the disk.</para>
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

    /// <summary>The widest the recorded picture may be. Anything wider is halved until it fits.</summary>
    private const int MaxWidth = 1280;

    /// <summary>Bits a second given to the encoder. About 11 MB a minute.</summary>
    private const int Bitrate = 1_500_000;

    /// <summary>A full frame at least this often, so a saved clip can start near where it was cut.</summary>
    private const int KeyFrameEverySeconds = 2;

    /// <summary>DXGI's "nothing has changed on screen yet".</summary>
    private const int WaitTimeout = unchecked((int)0x887A0027);

    /// <summary>DXGI's "somebody else took the duplication, or the mode changed". Start again.</summary>
    private const int AccessLost = unchecked((int)0x887A0026);

    private static bool _mediaFoundationStarted;

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
    /// Starts keeping the last <paramref name="length"/> of the chosen monitor. Returns false, having
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

    private void Run(TimeSpan length)
    {
        if (!OperatingSystem.IsWindows())
            return;

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGIOutputDuplication? duplication = null;
        ID3D11Texture2D? mips = null;
        ID3D11ShaderResourceView? mipView = null;
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

            duplication = Duplicate(device!);

            var screen = duplication.Description.ModeDescription;
            var (width, height, mipLevel) = Shrink((int)screen.Width, (int)screen.Height);

            (mips, mipView, staging) = MakeTextures(device!, (int)screen.Width, (int)screen.Height, width, height);

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
            _log($"Keeping the last {length.TotalMinutes:0} minutes of {width}×{height} at {FramesPerSecond} frames a second", null);

            var pixels = new byte[width * height * 4];
            var clock = Stopwatch.StartNew();
            long frame = 0;

            while (!_stopping)
            {
                // Paced off a monotonic timer rather than the machine's clock, so a clock change
                // part-way through an evening cannot make a clip run backwards.
                var dueAt = TimeSpan.FromTicks(frame * TimeSpan.TicksPerSecond / FramesPerSecond);
                var wait = dueAt - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                    Thread.Sleep(wait < TimeSpan.FromMilliseconds(2) ? 1 : (int)wait.TotalMilliseconds);

                if (!Grab(duplication!, context!, device!, mips!, mipView!, staging!, mipLevel, width, height, pixels, ref duplication))
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

            foreach (var leg in legs)
            {
                if (leg is not null)
                    Finish(leg, discard: true);
            }

            staging?.Dispose();
            mipView?.Dispose();
            mips?.Dispose();
            duplication?.Dispose();
            context?.Dispose();
            device?.Dispose();
        }
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
    /// The duplication of the first monitor on the adapter this device is on. Windows hands over a
    /// copy of what the desktop compositor already drew; nothing here draws, reads or asks about
    /// another program's window.
    /// </summary>
    private static IDXGIOutputDuplication Duplicate(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();

        adapter.EnumOutputs(0, out var output).CheckError();

        using (output)
        {
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            return output1.DuplicateOutput(device);
        }
    }

    /// <summary>
    /// How big the recorded picture is: the monitor halved until it is no wider than
    /// <see cref="MaxWidth"/>, and then trimmed to even numbers, because H.264 will not take odd ones.
    /// </summary>
    private static (int Width, int Height, int MipLevel) Shrink(int screenWidth, int screenHeight)
    {
        var level = 0;
        while (level < 6 && (screenWidth >> level) > MaxWidth)
            level++;

        var width = Math.Max(2, (screenWidth >> level) & ~1);
        var height = Math.Max(2, (screenHeight >> level) & ~1);
        return (width, height, level);
    }

    /// <summary>
    /// The two textures the shrinking uses: one with a chain of smaller copies the graphics card
    /// makes itself, and one the processor can read the finished size out of.
    /// </summary>
    private static (ID3D11Texture2D Mips, ID3D11ShaderResourceView View, ID3D11Texture2D Staging) MakeTextures(
        ID3D11Device device,
        int screenWidth,
        int screenHeight,
        int width,
        int height)
    {
        var mips = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)screenWidth,
            Height = (uint)screenHeight,
            MipLevels = 0,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });

        var view = device.CreateShaderResourceView(mips);

        var staging = device.CreateTexture2D(new Texture2DDescription
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

        return (mips, view, staging);
    }

    /// <summary>
    /// One frame: ask Windows for the desktop's own picture, let the graphics card shrink it, and
    /// copy the small result into <paramref name="pixels"/>. False means there was nothing new, which
    /// is ordinary — a still screen produces no frames at all.
    /// </summary>
    private bool Grab(
        IDXGIOutputDuplication duplication,
        ID3D11DeviceContext context,
        ID3D11Device device,
        ID3D11Texture2D mips,
        ID3D11ShaderResourceView mipView,
        ID3D11Texture2D staging,
        int mipLevel,
        int width,
        int height,
        byte[] pixels,
        ref IDXGIOutputDuplication? live)
    {
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
            // duplication. Ask again; this is ordinary and happens several times an evening.
            duplication.Dispose();
            live = Duplicate(device);
            return false;
        }

        result.CheckError();

        try
        {
            using var frame = desktop.QueryInterface<ID3D11Texture2D>();

            context.CopySubresourceRegion(mips, 0, 0, 0, 0, frame, 0);
            context.GenerateMips(mipView);
            context.CopySubresourceRegion(
                staging, 0, 0, 0, 0, mips, (uint)mipLevel,
                new Vortice.Mathematics.Box(0, 0, 0, width, height, 1));

            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var row = width * 4;
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(
                        mapped.DataPointer + (y * (int)mapped.RowPitch),
                        pixels,
                        y * row,
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
}
