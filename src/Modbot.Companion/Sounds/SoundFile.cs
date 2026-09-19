using System.Buffers.Binary;
using Modbot.Companion.Voice;

namespace Modbot.Companion.Sounds;

/// <summary>A sound read from the moderator's own file, or the sentence saying why it was not.</summary>
/// <param name="Clip">The sound, or null when the file could not be used.</param>
/// <param name="Problem">One sentence for the settings screen, or null when it was read.</param>
public sealed record SoundFileResult(VoiceClip? Clip, string? Problem);

/// <summary>
/// Reads the sound file a moderator pointed the Notifications card at.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> One file: the exact path typed into the Notifications
/// card, and nothing else. It is opened for reading, shared, and never written to. No folder is
/// searched, nothing near it is looked at, and a path that is blank means no file is opened at all.
/// The client made this sound itself until somebody chose otherwise, and goes back to making it the
/// moment the box is emptied.</para>
/// <para><strong>What leaves the machine: nothing.</strong> The file is turned into samples and
/// played on this PC. No server is told that it exists, what it is called or where it came from,
/// and the file is never copied anywhere.</para>
/// <para><strong>Why only <c>.wav</c>.</strong> A WAV file is a header and the samples, which is
/// about sixty lines of reading and no library at all. Every other format a moderator might have is
/// a decoder — a dependency to ship, a licence to carry and a parser to be wrong in, for a sound
/// that lasts half a second. Any program on the machine will save a WAV.</para>
/// <para><strong>Every failure is a sentence, never a crash.</strong> A missing file, a folder, a
/// file that is not a WAV, one that is empty, one that is far too big, one this account may not
/// read: each comes back as <see cref="SoundFileResult.Problem"/>, the built-in sound is played
/// instead, and everything else in the client carries on.</para>
/// </remarks>
public static class SoundFile
{
    /// <summary>
    /// The largest file that will be opened. Generous for a notification and small enough that a
    /// path typed by mistake — a film, a disk image — is refused rather than read into memory.
    /// </summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    /// <summary>
    /// The most of a file that is played. A notification that runs on past this is a notification
    /// somebody would come to hate, and cutting it is kinder than refusing it.
    /// </summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reads the file, or says in one sentence why it could not be.
    /// </summary>
    /// <param name="path">The path from settings. Blank means there is no file, which is not a problem.</param>
    public static SoundFileResult Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new SoundFileResult(null, null);

        var file = path.Trim();

        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
                return new SoundFileResult(null, $"There is no file at {file}.");

            if (info.Length > MaxBytes)
                return new SoundFileResult(null, $"{Path.GetFileName(file)} is too big to use as a sound.");

            if (info.Length < 44)
                return new SoundFileResult(null, $"{Path.GetFileName(file)} is not a sound file.");

            var bytes = File.ReadAllBytes(file);
            return Parse(bytes, Path.GetFileName(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or PathTooLongException)
        {
            return new SoundFileResult(null, $"{Path.GetFileName(file)} could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Turns the bytes of a WAV file into mono samples. Its own method so the reading rules can be
    /// checked without a file on disk.
    /// </summary>
    public static SoundFileResult Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length < 12
            || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return new SoundFileResult(null, $"{name} is not a WAV file.");
        }

        int format = 0, channels = 0, rate = 0, bits = 0;
        var at = 12;

        while (at + 8 <= bytes.Length)
        {
            var chunk = bytes.Slice(at, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at + 4, 4));
            var body = at + 8;

            if (size > (uint)(bytes.Length - body))
                size = (uint)(bytes.Length - body);

            if (chunk.SequenceEqual("fmt "u8) && size >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 2, 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 14, 2));

                // The extended header says what it really is in a tag of its own; its first two
                // bytes are the plain format number the rest of this method understands.
                if (format == 0xFFFE && size >= 40)
                    format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 24, 2));
            }
            else if (chunk.SequenceEqual("data"u8))
            {
                return Samples(bytes.Slice(body, (int)size), name, format, channels, rate, bits);
            }

            // Chunks sit on even boundaries, and a file that says otherwise is not one to guess at.
            at = body + (int)size + ((int)size % 2);
        }

        return new SoundFileResult(null, $"{name} has no sound in it.");
    }

    private static SoundFileResult Samples(
        ReadOnlySpan<byte> data, string name, int format, int channels, int rate, int bits)
    {
        if (channels is < 1 or > 8 || rate is < 4_000 or > 384_000)
            return new SoundFileResult(null, $"{name} is a kind of WAV file Modbot cannot read.");

        var bytesPerSample = bits / 8;
        var readable = format switch
        {
            1 => bits is 8 or 16 or 24 or 32,
            3 => bits is 32,
            _ => false,
        };

        if (!readable)
            return new SoundFileResult(null, $"{name} is a kind of WAV file Modbot cannot read. Save it as a plain WAV.");

        var frames = data.Length / (bytesPerSample * channels);
        var most = (int)Math.Round(MaxLength.TotalSeconds * rate);
        frames = Math.Min(frames, most);

        if (frames <= 0)
            return new SoundFileResult(null, $"{name} has no sound in it.");

        var samples = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            var total = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var at = ((frame * channels) + channel) * bytesPerSample;
                total += One(data.Slice(at, bytesPerSample), format, bits);
            }

            // Every channel folded into one, because everything downstream of here — the gain, the
            // players, the device — works in mono.
            samples[frame] = Math.Clamp(total / channels, -1f, 1f);
        }

        return new SoundFileResult(new VoiceClip(samples, rate), null);
    }

    private static float One(ReadOnlySpan<byte> sample, int format, int bits)
    {
        if (format == 3)
            return BitConverter.ToSingle(sample);

        return bits switch
        {
            // Eight-bit WAV samples are counted from the middle rather than from zero.
            8 => (sample[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32_768f,
            24 => ((sample[2] << 24) | (sample[1] << 16) | (sample[0] << 8)) / 2_147_483_648f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2_147_483_648f,
        };
    }
}
