using Modbot.Companion.Sounds;

namespace Modbot.Companion.Tests.Sounds;

/// <summary>
/// The bleep's samples: two tones, made from a formula, starting and ending at silence so neither
/// end is a click.
/// </summary>
public class BleepTests
{
    private static int Samples(TimeSpan length, int sampleRate)
        => (int)Math.Round(length.TotalSeconds * sampleRate);

    [Fact]
    public void IsTwoTonesLongAtTheRateItWasAskedFor()
    {
        var clip = Bleep.Make();

        Assert.Equal(Bleep.SampleRate, clip.SampleRate);
        Assert.Equal(
            Samples(Bleep.FirstLength, Bleep.SampleRate) + Samples(Bleep.SecondLength, Bleep.SampleRate),
            clip.Samples.Length);

        Assert.Equal(Bleep.Length.TotalMilliseconds, clip.Duration.TotalMilliseconds, 1);
    }

    [Theory]
    [InlineData(16_000)]
    [InlineData(44_100)]
    [InlineData(48_000)]
    public void AnyRateGivesTheSameSoundForTheSameLength(int sampleRate)
    {
        var clip = Bleep.Make(sampleRate);

        Assert.Equal(sampleRate, clip.SampleRate);
        Assert.Equal(Bleep.Length.TotalMilliseconds, clip.Duration.TotalMilliseconds, 1);
    }

    [Fact]
    public void BothTonesStartAndEndAtSilence()
    {
        var clip = Bleep.Make();
        var first = Samples(Bleep.FirstLength, Bleep.SampleRate);

        // The start of each tone, and the end of the whole sound. A sine that begins at full height
        // begins with a click, which sounds like a fault rather than a notification.
        Assert.Equal(0f, clip.Samples[0]);
        Assert.Equal(0f, clip.Samples[first]);
        Assert.True(Math.Abs(clip.Samples[^1]) < 0.01f);
        Assert.True(Math.Abs(clip.Samples[first - 1]) < 0.01f);
    }

    [Fact]
    public void IsNeverLouderThanItsOwnHeight()
    {
        var clip = Bleep.Make();

        Assert.All(clip.Samples, s => Assert.InRange(s, -Bleep.Height - 0.0001f, Bleep.Height + 0.0001f));
        Assert.DoesNotContain(clip.Samples, float.IsNaN);
    }

    [Fact]
    public void IsActuallyASoundAndNotSilence()
    {
        var clip = Bleep.Make();

        Assert.True(clip.Samples.Max(Math.Abs) > Bleep.Height * 0.9f);
    }

    [Fact]
    public void TheSecondToneIsTheHigherOne()
    {
        var clip = Bleep.Make();
        var first = Samples(Bleep.FirstLength, Bleep.SampleRate);

        // Counted by how often each half crosses zero, which is twice its pitch per second: the
        // cheapest way to say "this half is higher than that one" without a transform.
        var low = Crossings(clip.Samples.AsSpan(0, first));
        var high = Crossings(clip.Samples.AsSpan(first));

        Assert.True(high / Bleep.SecondLength.TotalSeconds > low / Bleep.FirstLength.TotalSeconds);

        // And each is the pitch it was asked for, within a crossing or two of rounding.
        Assert.Equal(2 * Bleep.FirstTone * Bleep.FirstLength.TotalSeconds, low, 1.0);
        Assert.Equal(2 * Bleep.SecondTone * Bleep.SecondLength.TotalSeconds, high, 1.0);
    }

    [Fact]
    public void TheVolumeIsTheSameSoundQuieter()
    {
        var full = Bleep.Make();
        var half = full.WithGain(0.5f);

        Assert.Equal(full.Samples.Length, half.Samples.Length);
        Assert.Equal(full.Samples.Max(Math.Abs) / 2, half.Samples.Max(Math.Abs), 3);
        Assert.Equal(0f, full.WithGain(0f).Samples.Max(Math.Abs));
    }

    private static int Crossings(ReadOnlySpan<float> samples)
    {
        var crossings = 0;
        for (var i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] <= 0 && samples[i] > 0)
                crossings++;
            else if (samples[i - 1] >= 0 && samples[i] < 0)
                crossings++;
        }

        return crossings;
    }
}
