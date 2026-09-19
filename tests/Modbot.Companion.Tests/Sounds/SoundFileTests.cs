using System.Buffers.Binary;
using Modbot.Companion.Sounds;

namespace Modbot.Companion.Tests.Sounds;

/// <summary>
/// Reading the sound file a moderator pointed the Notifications card at: a plain WAV becomes
/// samples, and everything else becomes one sentence rather than a crash.
/// </summary>
public class SoundFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-sound-file-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);

        GC.SuppressFinalize(this);
    }

    private string Write(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A plain 16-bit WAV of a rising ramp, which is all the reader has to understand.</summary>
    private static byte[] Wav(int sampleRate = 48_000, int channels = 1, int frames = 4_800)
    {
        var data = new byte[frames * channels * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (short)(frame % 2 == 0 ? 8_000 : -8_000);
            for (var channel = 0; channel < channels; channel++)
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(((frame * channels) + channel) * 2), value);
        }

        var file = new byte[44 + data.Length];
        var at = file.AsSpan();

        "RIFF"u8.CopyTo(at);
        BinaryPrimitives.WriteUInt32LittleEndian(at[4..], (uint)(36 + data.Length));
        "WAVE"u8.CopyTo(at[8..]);
        "fmt "u8.CopyTo(at[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(at[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(at[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(at[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(at[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(at[28..], (uint)(sampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(at[32..], (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(at[34..], 16);
        "data"u8.CopyTo(at[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(at[40..], (uint)data.Length);
        data.CopyTo(at[44..]);

        return file;
    }

    [Fact]
    public void NoFileChosenIsNotAProblem()
    {
        foreach (var nothing in new[] { null, "", "   " })
        {
            var read = SoundFile.Read(nothing);

            Assert.Null(read.Clip);
            Assert.Null(read.Problem);
        }
    }

    [Fact]
    public void ReadsAPlainWav()
    {
        var read = SoundFile.Read(Write("ping.wav", Wav()));

        Assert.Null(read.Problem);
        Assert.NotNull(read.Clip);
        Assert.Equal(48_000, read.Clip.SampleRate);
        Assert.Equal(4_800, read.Clip.Samples.Length);
        Assert.True(read.Clip.Samples.Max(Math.Abs) > 0.2f);
    }

    [Fact]
    public void TwoChannelsBecomeOne()
    {
        var read = SoundFile.Read(Write("stereo.wav", Wav(channels: 2, frames: 1_000)));

        Assert.Null(read.Problem);
        Assert.NotNull(read.Clip);
        Assert.Equal(1_000, read.Clip.Samples.Length);
    }

    [Fact]
    public void AFileThatIsNotThereIsASentence()
    {
        var read = SoundFile.Read(Path.Combine(_directory, "nowhere", "gone.wav"));

        Assert.Null(read.Clip);
        Assert.NotNull(read.Problem);
        Assert.Contains("no file", read.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileThatIsNotAWavIsASentence()
    {
        var read = SoundFile.Read(Write("notes.txt", "this is not a sound, it is a shopping list."u8.ToArray()));

        Assert.Null(read.Clip);
        Assert.NotNull(read.Problem);
    }

    [Fact]
    public void AWavWithNoSoundInItIsASentence()
    {
        var header = Wav(frames: 0);

        var read = SoundFile.Parse(header, "empty.wav");

        Assert.Null(read.Clip);
        Assert.NotNull(read.Problem);
    }

    [Fact]
    public void AFolderIsASentenceRatherThanACrash()
    {
        Directory.CreateDirectory(_directory);

        var read = SoundFile.Read(_directory);

        Assert.Null(read.Clip);
        Assert.NotNull(read.Problem);
    }

    [Fact]
    public void ACompressedWavIsRefusedRatherThanPlayedAsNoise()
    {
        var file = Wav(frames: 100);

        // Format 85 is MP3 inside a WAV container: a real thing somebody's file could be, and one
        // this reader must not try to read as raw samples.
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(20), 85);

        var read = SoundFile.Parse(file, "music.wav");

        Assert.Null(read.Clip);
        Assert.NotNull(read.Problem);
    }

    [Fact]
    public void AVeryLongSoundIsCutRatherThanRefused()
    {
        // Twenty seconds at 8 kHz, which is well past the most that will be played.
        var read = SoundFile.Parse(Wav(sampleRate: 8_000, frames: 160_000), "long.wav");

        Assert.Null(read.Problem);
        Assert.NotNull(read.Clip);
        Assert.Equal(SoundFile.MaxLength.TotalSeconds, read.Clip.Duration.TotalSeconds, 1);
    }

    [Fact]
    public void TheExtendedHeaderIsReadAsWhatItSaysItReallyIs()
    {
        var file = Wav(frames: 100);

        // 0xFFFE says "look at the tag further in for the real format". Written by plenty of
        // recording programs, and a reader that took the number at face value would refuse it.
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(20), 0xFFFE);

        var read = SoundFile.Parse(file, "extended.wav");

        // The header here is the short 16-byte one, so there is no tag to look at and the file is
        // refused with a sentence rather than read as something it might not be.
        Assert.Null(read.Clip);
        Assert.NotNull(read.Problem);
    }
}
