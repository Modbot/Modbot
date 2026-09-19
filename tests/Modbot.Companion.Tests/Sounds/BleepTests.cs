using Modbot.Companion.Sounds;

namespace Modbot.Companion.Tests.Sounds;

/// <summary>
/// The notification sound's samples: two struck notes a fifth apart, made from a formula, coming
/// up softly, dying away with a tail, and ending at exact silence.
/// </summary>
/// <remarks>
/// <para>These tests were rewritten on 2026-09-19 with the sound itself. The old ones pinned two
/// sine tones laid end to end, which is a shape the sound no longer has: the second note now begins
/// while the first is still ringing, so "how long is the first half" is not a question with an
/// answer any more.</para>
/// <para>Nobody can hear a sound from a test, so what is checked here is everything about it that
/// can be stated as a number: how long it is, that it is not silence, that it never clips, that
/// neither end is a click, that each note is the pitch it was asked for, that the two are a perfect
/// fifth apart, and that the shape is a struck decay rather than a tone switched on and off.
/// </para>
/// </remarks>
public class BleepTests
{
    private static int Samples(TimeSpan length, int sampleRate)
        => (int)Math.Round(length.TotalSeconds * sampleRate);

    private static float Loudest(ReadOnlySpan<float> samples)
    {
        var loudest = 0f;
        foreach (var sample in samples)
            loudest = Math.Max(loudest, Math.Abs(sample));

        return loudest;
    }

    [Fact]
    public void LastsFromTheFirstNoteToTheEndOfTheSecond()
    {
        var clip = Bleep.Make();

        Assert.Equal(Bleep.SampleRate, clip.SampleRate);
        Assert.Equal(Bleep.SecondStartsAt + Bleep.NoteLength, Bleep.Length);
        Assert.Equal(Samples(Bleep.Length, Bleep.SampleRate), clip.Samples.Length);
        Assert.Equal(Bleep.Length.TotalMilliseconds, clip.Duration.TotalMilliseconds, 1);
    }

    [Fact]
    public void IsStillShortEnoughToHearFortyTimesAnEvening()
    {
        Assert.True(Bleep.Length < TimeSpan.FromMilliseconds(600));
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
        Assert.DoesNotContain(clip.Samples, float.IsNaN);
    }

    [Fact]
    public void IsActuallyASoundAndNotSilence()
    {
        var clip = Bleep.Make();

        // Loud enough to be a notification rather than a whisper somebody would miss over a game.
        var rms = Math.Sqrt(clip.Samples.Sum(s => (double)s * s) / clip.Samples.Length);

        Assert.True(rms > 0.05, $"The sound is nearly silent: its average height is {rms:F4}.");
        Assert.True(clip.Samples.Count(s => Math.Abs(s) > 0.05f) > clip.Samples.Length / 4);
    }

    [Fact]
    public void IsBroughtToItsOwnHeightAndNeverPastIt()
    {
        var clip = Bleep.Make();

        // Exactly its own height: the notes are added together and then scaled, so two notes
        // overlapping can never clip and the loudness does not depend on how many overtones there
        // happen to be.
        Assert.Equal(Bleep.Height, Loudest(clip.Samples), 3);
        Assert.All(clip.Samples, s => Assert.InRange(s, -Bleep.Height - 0.0001f, Bleep.Height + 0.0001f));
    }

    [Fact]
    public void NeitherEndIsAClick()
    {
        var clip = Bleep.Make();

        // Starts at exact silence and comes up over the attack rather than appearing at full
        // height: the first couple of milliseconds are a small fraction of the sound.
        Assert.Equal(0f, clip.Samples[0]);

        var twoMilliseconds = Samples(TimeSpan.FromMilliseconds(2), Bleep.SampleRate);
        Assert.True(
            Loudest(clip.Samples.AsSpan(0, twoMilliseconds)) < 0.2f * Bleep.Height,
            "The sound reaches most of its height within two milliseconds, which is a click.");

        // And ends at exact silence, because a buffer that stops on a number that is not zero
        // stops with a click too.
        Assert.Equal(0f, clip.Samples[^1]);
    }

    [Fact]
    public void EachNoteDecaysAndTheSoundHasARealTail()
    {
        var clip = Bleep.Make();
        var rate = Bleep.SampleRate;

        float Around(TimeSpan at)
        {
            var middle = Samples(at, rate);
            var from = Math.Max(0, middle - (rate / 200));
            var to = Math.Min(clip.Samples.Length, middle + (rate / 200));
            return Loudest(clip.Samples.AsSpan(from, to - from));
        }

        // The first note is loudest just after it is struck and is well down by the time the
        // second arrives: a decay, not a tone held at one height.
        var struck = Around(TimeSpan.FromMilliseconds(25));
        var fading = Around(Bleep.SecondStartsAt);
        Assert.True(struck > fading * 2, $"The first note does not decay: {struck:F3} then {fading:F3}.");

        // The second note is struck while the first is still going, which is what makes them one
        // sound rather than two beeps in a row.
        Assert.True(fading > 0.05f * Bleep.Height, "The first note is over before the second starts.");
        Assert.True(Around(Bleep.SecondStartsAt + TimeSpan.FromMilliseconds(60)) > fading);

        // And there is still something there four fifths of the way through: an exponential tail,
        // not a fade cut off at the end.
        Assert.True(
            Around(TimeSpan.FromMilliseconds(400)) > 0.005f,
            "The sound has no tail left at four fifths of its length.");
    }

    [Fact]
    public void TheTwoNotesArePitchedWhereTheyWereAskedToBe()
    {
        var clip = Bleep.Make();
        var rate = Bleep.SampleRate;

        // Counted by how often each stretch crosses zero, which is twice its pitch per second:
        // the cheapest way to measure a pitch without a transform. The overtones are all under
        // half the height of the note below them, so none of them adds a crossing of its own.
        var firstFrom = Samples(Bleep.Attack, rate);
        var firstTo = Samples(Bleep.SecondStartsAt, rate);
        var first = Crossings(clip.Samples.AsSpan(firstFrom, firstTo - firstFrom));
        Assert.Equal(2 * Bleep.FirstTone * (Bleep.SecondStartsAt - Bleep.Attack).TotalSeconds, first, 2.0);

        // The second note is measured once it is clearly louder than what is left of the first.
        var secondFrom = Samples(Bleep.SecondStartsAt + TimeSpan.FromMilliseconds(80), rate);
        var secondTo = Samples(Bleep.SecondStartsAt + TimeSpan.FromMilliseconds(200), rate);
        var second = Crossings(clip.Samples.AsSpan(secondFrom, secondTo - secondFrom));
        Assert.Equal(2 * Bleep.SecondTone * 0.120, second, 2.0);
    }

    [Fact]
    public void TheTwoNotesAreAPerfectFifthApartAndLowerThanTheOldOnes()
    {
        // Three halves of the first: the simplest interval after the octave, and the reason the
        // pair sounds like one thing rather than two unrelated beeps.
        Assert.Equal(Bleep.FirstTone * 1.5, Bleep.SecondTone, 6);

        // Both below where the old sound sat. 880 and 1245 hertz were in the band the ear is most
        // quickly annoyed by, and a moderator hearing it forty times an evening said so.
        Assert.True(Bleep.FirstTone < 880);
        Assert.True(Bleep.SecondTone < 1245);
    }

    [Fact]
    public void TheVolumeIsTheSameSoundQuieter()
    {
        var full = Bleep.Make();
        var half = full.WithGain(0.5f);

        Assert.Equal(full.Samples.Length, half.Samples.Length);
        Assert.Equal(Loudest(full.Samples) / 2, Loudest(half.Samples), 3);
        Assert.Equal(0f, Loudest(full.WithGain(0f).Samples));
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
