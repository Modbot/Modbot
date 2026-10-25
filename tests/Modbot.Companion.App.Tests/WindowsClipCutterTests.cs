using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Modbot.Companion.App.Tests;

/// <summary>
/// Cutting a shorter copy out of a saved recording, on a recording made here the way the recorder
/// makes one: H.264 picture with a keyframe every two seconds, and AAC sound beside it.
/// </summary>
/// <remarks>
/// These need Windows' own video library and an encoder to make the source file, and not a screen,
/// so they run on a Windows machine and skip anywhere else.
/// </remarks>
public sealed class WindowsClipCutterTests : IDisposable
{
    private const int Width = 320;
    private const int Height = 240;
    private const int FramesPerSecond = 30;
    private const int Seconds = 12;
    private const int KeyframeEverySeconds = 2;
    private const int SoundRate = 44_100;
    private const long Ticks = TimeSpan.TicksPerSecond;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "modbot-cutter-" + Guid.NewGuid().ToString("N"));

    public WindowsClipCutterTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ACutStartsOnAKeyframeAtOrBeforeTheStartAndPlays()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs Windows' video library.");

        var source = MakeRecording();
        var destination = Path.Combine(_folder, "cut.mp4");

        var cut = await new WindowsClipCutter().CutAsync(
            source, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), 50_000_000, destination, CancellationToken.None);

        Assert.True(cut.Succeeded, cut.Failure);
        Assert.Null(cut.Failure);
        Assert.True(cut.Start <= TimeSpan.FromSeconds(5), $"Started at {cut.Start}, after the start asked for.");
        Assert.True(cut.Start >= TimeSpan.FromSeconds(5 - KeyframeEverySeconds - 1), $"Started at {cut.Start}, long before.");
        Assert.Equal(new FileInfo(destination).Length, cut.Bytes);

        // From where it really started to the end asked for, give or take the last frame or sound block.
        var expected = TimeSpan.FromSeconds(9) - cut.Start;
        Assert.InRange(cut.Length.TotalSeconds, expected.TotalSeconds - 0.5, expected.TotalSeconds + 0.5);

        // Picture and sound both came across, still in the stored form.
        Assert.Equal(new[] { MediaTypeGuids.Video, MediaTypeGuids.Audio }.Order(), StreamKinds(destination).Order());    }

    private static Guid[] StreamKinds(string path)
    {
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
            var kinds = new List<Guid>();
            for (var i = 0; ; i++)
            {
                try
                {
                    using var type = reader.GetNativeMediaType(i, 0);
                    kinds.Add(type.MajorType);
                }
                catch (SharpGen.Runtime.SharpGenException)
                {
                    return [.. kinds];
                }
            }
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    [Fact]
    public async Task ACutThatWouldPassTheSizeLimitFailsAndLeavesNoFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs Windows' video library.");

        var source = MakeRecording();
        var destination = Path.Combine(_folder, "toobig.mp4");

        var cut = await new WindowsClipCutter().CutAsync(
            source, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(11), 20_000, destination, CancellationToken.None);

        Assert.False(cut.Succeeded);
        Assert.False(string.IsNullOrEmpty(cut.Failure));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task AStartPastTheEndOfTheRecordingFails()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs Windows' video library.");

        var source = MakeRecording();
        var destination = Path.Combine(_folder, "late.mp4");

        var cut = await new WindowsClipCutter().CutAsync(
            source, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90), 50_000_000, destination, CancellationToken.None);

        Assert.False(cut.Succeeded);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task AMissingRecordingOrAnEmptyStretchFailsWithoutThrowing()
    {
        var cutter = new WindowsClipCutter();
        var destination = Path.Combine(_folder, "never.mp4");

        var missing = await cutter.CutAsync(
            Path.Combine(_folder, "nothing.mp4"), TimeSpan.Zero, TimeSpan.FromSeconds(5), 1_000_000, destination, CancellationToken.None);
        var backwards = await cutter.CutAsync(
            Path.Combine(_folder, "nothing.mp4"), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 1_000_000, destination, CancellationToken.None);

        Assert.False(missing.Succeeded);
        Assert.False(backwards.Succeeded);
        Assert.False(File.Exists(destination));
    }

    /// <summary>Writes twelve seconds of noisy picture and a steady tone, the way the recorder does.</summary>
    private string MakeRecording()
    {
        var path = Path.Combine(_folder, "source.mp4");

        MediaFactory.MFStartup(true).CheckError();
        try
        {
            using var writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, null);

            using var videoOut = MediaFactory.MFCreateMediaType();
            videoOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            videoOut.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            videoOut.Set(MediaTypeAttributeKeys.AvgBitrate, 2_000_000u);
            videoOut.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            videoOut.Set(MediaTypeAttributeKeys.FrameSize, Pack(Width, Height));
            videoOut.Set(MediaTypeAttributeKeys.FrameRate, Pack(FramesPerSecond, 1));
            videoOut.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            videoOut.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)(FramesPerSecond * KeyframeEverySeconds));
            var video = writer.AddStream(videoOut);

            using var videoIn = MediaFactory.MFCreateMediaType();
            videoIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            videoIn.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            videoIn.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            videoIn.Set(MediaTypeAttributeKeys.FrameSize, Pack(Width, Height));
            videoIn.Set(MediaTypeAttributeKeys.FrameRate, Pack(FramesPerSecond, 1));
            videoIn.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            videoIn.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(Width * 4));
            writer.SetInputMediaType(video, videoIn, null);

            using var soundOut = MediaFactory.MFCreateMediaType();
            soundOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            soundOut.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            soundOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            soundOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)SoundRate);
            soundOut.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
            soundOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 16_000u);
            soundOut.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
            soundOut.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);
            var sound = writer.AddStream(soundOut);

            using var soundIn = MediaFactory.MFCreateMediaType();
            soundIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            soundIn.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            soundIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            soundIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)SoundRate);
            soundIn.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
            soundIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u);
            soundIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(SoundRate * 4));
            writer.SetInputMediaType(sound, soundIn, null);

            writer.BeginWriting();

            var random = new Random(7);
            var picture = new byte[Width * Height * 4];
            var soundPerFrame = SoundRate / FramesPerSecond;
            var tone = new byte[soundPerFrame * 4];
            long soundSamples = 0;

            for (var frame = 0; frame < FramesPerSecond * Seconds; frame++)
            {
                random.NextBytes(picture);
                Put(writer, video, picture, frame * Ticks / FramesPerSecond, Ticks / FramesPerSecond);

                for (var i = 0; i < soundPerFrame; i++)
                {
                    var value = (short)(Math.Sin((soundSamples + i) * 2 * Math.PI * 440 / SoundRate) * 8000);
                    BitConverter.TryWriteBytes(tone.AsSpan(i * 4), value);
                    BitConverter.TryWriteBytes(tone.AsSpan(i * 4 + 2), value);
                }

                Put(writer, sound, tone, soundSamples * Ticks / SoundRate, soundPerFrame * Ticks / SoundRate);
                soundSamples += soundPerFrame;
            }

            writer.Finalize();
        }
        finally
        {
            MediaFactory.MFShutdown();
        }

        return path;
    }

    private static void Put(IMFSinkWriter writer, int stream, byte[] bytes, long time, long duration)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes.Length);
        buffer.Lock(out var target, out _, out _);
        Marshal.Copy(bytes, 0, target, bytes.Length);
        buffer.Unlock();
        buffer.CurrentLength = bytes.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        writer.WriteSample(stream, sample);
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}
