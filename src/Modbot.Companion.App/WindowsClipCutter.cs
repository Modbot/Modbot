using SharpGen.Runtime;
using Modbot.Companion.Clips;
using Vortice.MediaFoundation;

namespace Modbot.Companion.App;

/// <summary>
/// Writes a shorter copy of a saved recording, using the video library Windows already has.
/// </summary>
/// <remarks>
/// <para>The recorder's files are already compressed, so this does not compress anything again. It
/// reads the picture and sound out of the saved file as they are stored, starting from the nearest
/// keyframe at or before the start asked for (a keyframe is a picture that stands on its own; the
/// pictures after it only say what changed), and writes them unchanged into a new file with their
/// times moved back so the copy starts at zero. Nothing is decoded, so a cut takes a moment and
/// the picture is exactly the one that was recorded.</para>
/// <para>It reads one file the moderator's own recorder wrote and writes one file the caller names.
/// It never looks at a screen or a window. The part that answers clip requests calls it.</para>
/// <para>If the part asked for does not fit in the size limit it fails and leaves no file; it does
/// not quietly cut a shorter part than was asked for.</para>
/// </remarks>
public sealed class WindowsClipCutter : IClipCutter
{
    /// <summary>Media Foundation's "no stream with that number", which is how the end of the stream list shows.</summary>
    private const int NoSuchStream = unchecked((int)0xC00D36B3);

    /// <summary>The mark Media Foundation puts on a picture that stands on its own (MFSampleExtension_CleanPoint).</summary>
    private static readonly Guid KeyframeMark = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    /// <summary>How far the written file's length may differ from what was copied, in 100-nanosecond ticks (one second).</summary>
    private const long LengthTolerance = TimeSpan.TicksPerSecond;

    public Task<ClipCut> CutAsync(
        string sourcePath,
        TimeSpan start,
        TimeSpan end,
        long maxBytes,
        string destinationPath,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(Failed("Cutting a clip only works on Windows."));

        if (start < TimeSpan.Zero || end <= start || maxBytes <= 0)
            return Task.FromResult(Failed("The part to cut is not a real stretch of the recording."));

        if (!File.Exists(sourcePath))
            return Task.FromResult(Failed("The recording is gone."));

        return Task.Run(() => Cut(sourcePath, start.Ticks, end.Ticks, maxBytes, destinationPath, ct), ct);
    }

    private static ClipCut Cut(
        string sourcePath, long startTicks, long endTicks, long maxBytes, string destinationPath, CancellationToken ct)
    {
        MediaFactory.MFStartup(true).CheckError();

        try
        {
            var made = TryCut(sourcePath, startTicks, endTicks, maxBytes, destinationPath, ct, out var copiedUpTo);
            if (!made.Succeeded)
            {
                DeleteQuietly(destinationPath);
                return made;
            }

            var check = ReadBack(destinationPath, made.Start.Ticks, copiedUpTo, maxBytes);
            if (!check.Succeeded)
                DeleteQuietly(destinationPath);

            return check;
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(destinationPath);
            throw;
        }
        catch (Exception ex) when (ex is SharpGenException or IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(destinationPath);
            return Failed("Windows could not cut the recording.");
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    /// <summary>Reads the stored pictures and sound and writes the ones from the start to the end.</summary>
    /// <param name="copiedUpTo">Where in the source the last copied picture or sound ended.</param>
    private static ClipCut TryCut(
        string sourcePath,
        long startTicks,
        long endTicks,
        long maxBytes,
        string destinationPath,
        CancellationToken ct,
        out long copiedUpTo)
    {
        copiedUpTo = 0;

        using var reader = MediaFactory.MFCreateSourceReaderFromURL(sourcePath, null);

        var (videoIn, audioIn) = FindStreams(reader);
        if (videoIn < 0)
            return Failed("The recording has no picture.");

        // Only the picture and the sound; anything else in the file stays behind.
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(videoIn, true);
        if (audioIn >= 0)
            reader.SetStreamSelection(audioIn, true);

        // No media type is set on the reader, so it hands over the samples as they are stored.
        // This asks for the keyframe at or before the start.
        reader.SetCurrentPosition(startTicks);

        using var attributes = MediaFactory.MFCreateAttributes(1);
        var writer = MediaFactory.MFCreateSinkWriterFromURL(destinationPath, null, attributes);

        try
        {
            // Each stream is added with the type it already has and given the same type as its
            // input, so the writer takes it as it is instead of putting an encoder in between.
            using var videoType = reader.GetNativeMediaType(videoIn, 0);
            var videoOut = writer.AddStream(videoType);
            writer.SetInputMediaType(videoOut, videoType, null);

            var audioOut = -1;
            if (audioIn >= 0)
            {
                using var audioType = reader.GetNativeMediaType(audioIn, 0);
                audioOut = writer.AddStream(audioType);
                writer.SetInputMediaType(audioOut, audioType, null);
            }

            writer.BeginWriting();

            var outcome = Copy(reader, writer, videoIn, videoOut, audioIn, audioOut, endTicks, maxBytes, ct);
            writer.Finalize();

            copiedUpTo = outcome.CopiedUpTo;
            return outcome.Cut;
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Copies samples until the end is reached, or fails when the size limit would be passed.</summary>
    private static (ClipCut Cut, long CopiedUpTo) Copy(
        IMFSourceReader reader,
        IMFSinkWriter writer,
        int videoIn,
        int videoOut,
        int audioIn,
        int audioOut,
        long endTicks,
        long maxBytes,
        CancellationToken ct)
    {
        long? origin = null;
        long payload = 0;
        long copiedUpTo = 0;
        var videoDone = false;
        var audioDone = audioIn < 0;

        // Sound that arrives before the first picture does: kept until we know where the copy starts.
        var waiting = new List<(IMFSample Sample, long Time, int Stream)>();

        try
        {
            while (!videoDone || !audioDone)
            {
                ct.ThrowIfCancellationRequested();

                var sample = reader.ReadSample(
                    (int)SourceReaderIndex.AnyStream, SourceReaderControlFlag.None,
                    out var streamIndex, out var flags, out var time);

                if (flags.HasFlag(SourceReaderFlag.Error))
                {
                    sample?.Dispose();
                    return (Failed("Windows could not read the recording."), 0);
                }

                if (flags.HasFlag(SourceReaderFlag.EndOfStream))
                {
                    sample?.Dispose();
                    if (streamIndex == videoIn) videoDone = true;
                    else if (streamIndex == audioIn) audioDone = true;
                    else videoDone = audioDone = true;
                    continue;
                }

                if (sample is null)
                    continue;

                var isVideo = streamIndex == videoIn;
                if (!isVideo && streamIndex != audioIn)
                {
                    sample.Dispose();
                    continue;
                }

                if (time >= endTicks)
                {
                    sample.Dispose();
                    if (isVideo) videoDone = true;
                    else audioDone = true;
                    continue;
                }

                if (origin is null)
                {
                    if (!isVideo)
                    {
                        waiting.Add((sample, time, streamIndex));
                        continue;
                    }

                    // The reader seeks to a keyframe, so the first picture should be one. If it is
                    // not, the copy would open on a smeared picture, so it fails instead.
                    if (!IsKeyframe(sample))
                    {
                        sample.Dispose();
                        return (Failed("The recording has no clean picture to start from."), 0);
                    }

                    origin = time;

                    // Taken out of the list first, so the finally below never disposes a sample
                    // that Write has already disposed.
                    var held = waiting.ToArray();
                    waiting.Clear();
                    for (var h = 0; h < held.Length; h++)
                    {
                        // Sound from before the keyframe would get a time below zero.
                        if (held[h].Time < origin.Value)
                        {
                            held[h].Sample.Dispose();
                            continue;
                        }

                        var heldResult = Write(writer, held[h].Sample, held[h].Time, origin.Value, audioOut, ref payload, maxBytes);
                        if (heldResult is not null)
                        {
                            for (var rest = h + 1; rest < held.Length; rest++)
                                held[rest].Sample.Dispose();
                            return (heldResult, 0);
                        }
                    }
                }

                if (time < origin.Value)
                {
                    sample.Dispose();
                    continue;
                }

                var failure = Write(writer, sample, time, origin.Value, isVideo ? videoOut : audioOut, ref payload, maxBytes);
                if (failure is not null)
                    return (failure, 0);

                copiedUpTo = Math.Max(copiedUpTo, time);
            }
        }
        finally
        {
            foreach (var held in waiting)
                held.Sample.Dispose();
        }

        if (origin is null)
            return (Failed("The part asked for is past the end of the recording."), 0);

        // The "success" here is only that everything was copied; ReadBack makes it a real result.
        return (new ClipCut(true, TimeSpan.FromTicks(origin.Value), TimeSpan.Zero, payload, null), copiedUpTo);
    }

    /// <summary>Writes one sample with its time moved back by the copy's start. Returns a failure, or null when it was written.</summary>
    private static ClipCut? Write(
        IMFSinkWriter writer, IMFSample sample, long time, long origin, int stream, ref long payload, long maxBytes)
    {
        using (sample)
        {
            payload += sample.TotalLength;

            // Only what is stored counts here; the file's own bookkeeping is added to the real size
            // that ReadBack checks. Stopping early saves copying a clip that cannot be sent.
            if (payload > maxBytes)
                return Failed("The part asked for is bigger than the size limit.");

            sample.SampleTime = time - origin;
            writer.WriteSample(stream, sample);
            return null;
        }
    }

    /// <summary>Opens the written file again, to report where it really starts, how long it really is and how big.</summary>
    private static ClipCut ReadBack(string destinationPath, long originTicks, long copiedUpTo, long maxBytes)
    {
        var bytes = new FileInfo(destinationPath).Length;
        if (bytes > maxBytes)
            return Failed("The part asked for is bigger than the size limit.");

        using var reader = MediaFactory.MFCreateSourceReaderFromURL(destinationPath, null);
        var length = (long)reader.GetPresentationAttribute<ulong>(
            SourceReaderIndex.MediaSource, new MediaAttributeKey<ulong>(PresentationDescriptionAttributeKeys.Duration));

        if (length <= 0)
            return Failed("The copy came out empty.");

        // The copy ends where the last picture or sound that was copied ends, give or take a moment.
        var expected = copiedUpTo - originTicks;
        if (Math.Abs(length - expected) > LengthTolerance)
            return Failed("The copy came out the wrong length.");

        return new ClipCut(true, TimeSpan.FromTicks(originTicks), TimeSpan.FromTicks(length), bytes, null);
    }

    /// <summary>Finds the first picture stream and the first sound stream, by their number; -1 when there is none.</summary>
    private static (int Video, int Audio) FindStreams(IMFSourceReader reader)
    {
        var video = -1;
        var audio = -1;

        for (var i = 0; video < 0 || audio < 0; i++)
        {
            Guid major;
            try
            {
                using var type = reader.GetNativeMediaType(i, 0);
                major = type.MajorType;
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == NoSuchStream)
            {
                break;
            }

            if (major == MediaTypeGuids.Video && video < 0)
                video = i;
            else if (major == MediaTypeGuids.Audio && audio < 0)
                audio = i;
        }

        return (video, audio);
    }

    /// <summary>Whether the sample carries the keyframe mark. A sample without the mark is not one.</summary>
    private static bool IsKeyframe(IMFSample sample)
    {
        try
        {
            return sample.GetUInt32(KeyframeMark) != 0;
        }
        catch (SharpGenException)
        {
            return false;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A half-written file left behind is the caller's temporary folder's to clear.
        }
    }

    private static ClipCut Failed(string why) => new(false, TimeSpan.Zero, TimeSpan.Zero, 0, why);
}
