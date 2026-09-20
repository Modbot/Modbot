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

    [Theory]
    [MemberData(nameof(EveryTune))]
    public void EverySoundIsMadeToItsOwnHeightAndNeverPastIt(Tune tune)
    {
        var clip = Bleep.Make(tune);
        var height = Bleep.HeightOf(tune);

        Assert.Equal(height, Loudest(clip.Samples), 3);
        Assert.All(clip.Samples, s => Assert.InRange(s, -height - 0.0001f, height + 0.0001f));
    }

    [Theory]
    [MemberData(nameof(EveryTune))]
    public void NeitherEndOfAnySoundIsAClick(Tune tune)
    {
        var clip = Bleep.Make(tune);

        Assert.Equal(0f, clip.Samples[0]);
        Assert.Equal(0f, clip.Samples[^1]);

        var twoMilliseconds = Samples(TimeSpan.FromMilliseconds(2), Bleep.SampleRate);
        Assert.True(
            Loudest(clip.Samples.AsSpan(0, twoMilliseconds)) < 0.4f * Bleep.HeightOf(tune),
            $"{tune} reaches most of its height within two milliseconds, which is a click.");
    }

    [Theory]
    [MemberData(nameof(EveryTune))]
    public void EverySoundIsActuallyASoundAndNotSilence(Tune tune)
    {
        var clip = Bleep.Make(tune);
        var rms = Math.Sqrt(clip.Samples.Sum(s => (double)s * s) / clip.Samples.Length);

        Assert.True(rms > 0.02, $"{tune} is nearly silent: its average height is {rms:F4}.");
        Assert.Equal(Bleep.LengthOf(tune).TotalMilliseconds, clip.Duration.TotalMilliseconds, 1);
    }

    [Fact]
    public void EverySoundIsShortAndTheDoubledOneIsTwiceTheAlert()
    {
        foreach (var tune in Tunes.All.Where(t => t is not Tune.AlertTwice))
            Assert.True(Bleep.LengthOf(tune) < TimeSpan.FromMilliseconds(600), $"{tune} is too long.");

        // The only long one, and long for the one reason a moderator would forgive: it is the
        // alert played twice with a gap in it.
        Assert.True(Bleep.LengthOf(Tune.AlertTwice) < TimeSpan.FromMilliseconds(1300));
        Assert.True(Bleep.LengthOf(Tune.AlertTwice) > 2 * Bleep.LengthOf(Tune.Alert));
    }

    [Fact]
    public void TheChimeIsTheQuietestAndTheAlertsAreTheLoudest()
    {
        // The chime is the one a moderator hears most, so it is the one that must never be the
        // reason they switch the sound off: half the height of the alerts, exactly.
        Assert.Equal(Bleep.HeightOf(Tune.Alert) / 2, Bleep.HeightOf(Tune.Chime), 3);

        Assert.True(Bleep.HeightOf(Tune.Chime) < Bleep.HeightOf(Tune.AllClear));
        Assert.True(Bleep.HeightOf(Tune.AllClear) < Bleep.HeightOf(Tune.Alert));

        // And the urgent one is not louder than the ordinary alert. Whatever makes it urgent, it
        // is not volume.
        Assert.Equal(Bleep.HeightOf(Tune.Alert), Bleep.HeightOf(Tune.Urgent), 3);
        Assert.Equal(Bleep.HeightOf(Tune.Alert), Bleep.HeightOf(Tune.AlertTwice), 3);
    }

    [Fact]
    public void TheSoundsAreOneNoteThenTwoThenFourThenThree()
    {
        Assert.Single(Bleep.NotesOf(Tune.Chime));
        Assert.Equal(2, Bleep.NotesOf(Tune.Alert).Count);
        Assert.Equal(4, Bleep.NotesOf(Tune.AlertTwice).Count);
        Assert.Equal(3, Bleep.NotesOf(Tune.Urgent).Count);
        Assert.Equal(2, Bleep.NotesOf(Tune.AllClear).Count);
    }

    [Fact]
    public void TheChimeIsOneNoteOfTheAlertOnItsOwn()
    {
        var clip = Bleep.Make(Tune.Chime);
        var rate = Bleep.SampleRate;

        // Counted off the zero crossings, the same way the alert's notes are.
        var from = Samples(Bleep.AttackOf(Tune.Chime), rate);
        var to = Samples(TimeSpan.FromMilliseconds(200), rate);
        var crossings = Crossings(clip.Samples.AsSpan(from, to - from));
        var seconds = (200 - Bleep.AttackOf(Tune.Chime).TotalMilliseconds) / 1000;

        Assert.Equal(2 * Bleep.SecondTone * seconds, crossings, 2.0);
    }

    [Fact]
    public void TheDoubledAlertIsTheAlertTwiceWithRealSilenceInTheMiddle()
    {
        var clip = Bleep.Make(Tune.AlertTwice);
        var rate = Bleep.SampleRate;
        var repeat = Samples(Bleep.RepeatStartsAt, rate);

        // Sample for sample the same sound, played again: not four notes run together.
        for (var i = 0; i < repeat && i + repeat < clip.Samples.Length; i++)
            Assert.Equal(clip.Samples[i], clip.Samples[i + repeat]);

        // With a real silence in between, longer than the gap between the two notes of one pair,
        // which is what the ear uses to tell "the same thing twice" from "four notes".
        var quietFrom = Samples(Bleep.SecondStartsAt + Bleep.NoteLength, rate);
        var quiet = clip.Samples.AsSpan(quietFrom, repeat - quietFrom);

        Assert.True(quiet.Length > 0);
        Assert.Equal(0f, Loudest(quiet));
        Assert.True(Bleep.RepeatStartsAt - (Bleep.SecondStartsAt + Bleep.NoteLength) > Bleep.SecondStartsAt);
    }

    [Fact]
    public void TheUrgentSoundClimbsAndIsStruckHarderRatherThanLouder()
    {
        var notes = Bleep.NotesOf(Tune.Urgent);

        // Three notes, each higher than the last, and all of them above the alert's lower note.
        Assert.Equal(3, notes.Count);
        Assert.True(notes[0].Hertz < notes[1].Hertz);
        Assert.True(notes[1].Hertz < notes[2].Hertz);
        Assert.True(notes[0].Hertz > Bleep.FirstTone);

        // Struck harder: it reaches half its height sooner than the alert does, and the alert
        // sooner than the two soft ones.
        var urgent = HalfWayUp(Tune.Urgent);
        var alert = HalfWayUp(Tune.Alert);

        Assert.True(urgent < alert, $"The urgent sound takes {urgent} samples to the alert's {alert}.");
        Assert.True(alert < HalfWayUp(Tune.Chime));
        Assert.True(alert < HalfWayUp(Tune.AllClear));

        // And brighter: more of the sound is up where the higher partials are, which is what
        // crossing zero more often per second means.
        Assert.True(CrossingsPerSecond(Tune.Urgent) > CrossingsPerSecond(Tune.Alert));
        Assert.True(CrossingsPerSecond(Tune.Urgent) > CrossingsPerSecond(Tune.Chime));
    }

    [Fact]
    public void TheAllClearFallsWhereTheAlertRises()
    {
        Assert.True(Bleep.NotesOf(Tune.AllClear)[0].Hertz > Bleep.NotesOf(Tune.AllClear)[1].Hertz);
        Assert.True(Bleep.NotesOf(Tune.Alert)[0].Hertz < Bleep.NotesOf(Tune.Alert)[1].Hertz);

        // And the samples say the same thing: the second half of the all-clear crosses zero less
        // often than its first half, and the alert's the other way round.
        Assert.True(SecondHalfCrossings(Tune.AllClear) < FirstHalfCrossings(Tune.AllClear));
        Assert.True(SecondHalfCrossings(Tune.Alert) > FirstHalfCrossings(Tune.Alert));
    }

    [Fact]
    public void NoTwoOfTheFiveAreTheSameSound()
    {
        var made = Tunes.All.ToDictionary(tune => tune, tune => Bleep.Make(tune).Samples);

        foreach (var one in Tunes.All)
        {
            foreach (var other in Tunes.All.Where(t => t != one))
            {
                var same = made[one].Length == made[other].Length
                    && made[one].SequenceEqual(made[other]);

                Assert.False(same, $"{one} and {other} are the same sound.");
            }
        }
    }

    public static TheoryData<Tune> EveryTune()
    {
        var every = new TheoryData<Tune>();
        foreach (var tune in Tunes.All)
            every.Add(tune);

        return every;
    }

    /// <summary>How many samples in a sound takes to reach half its height: how hard it is struck.</summary>
    private static int HalfWayUp(Tune tune)
    {
        var clip = Bleep.Make(tune);
        var half = Bleep.HeightOf(tune) / 2;

        for (var i = 0; i < clip.Samples.Length; i++)
        {
            if (Math.Abs(clip.Samples[i]) >= half)
                return i;
        }

        return clip.Samples.Length;
    }

    /// <summary>How often a whole sound crosses zero per second: how bright it is, roughly.</summary>
    private static double CrossingsPerSecond(Tune tune)
    {
        var clip = Bleep.Make(tune);
        return Crossings(clip.Samples) / Bleep.LengthOf(tune).TotalSeconds;
    }

    private static int FirstHalfCrossings(Tune tune)
    {
        var clip = Bleep.Make(tune);
        return Crossings(clip.Samples.AsSpan(0, clip.Samples.Length / 2));
    }

    private static int SecondHalfCrossings(Tune tune)
    {
        var clip = Bleep.Make(tune);
        return Crossings(clip.Samples.AsSpan(clip.Samples.Length / 2));
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
