using Modbot.Companion.Clips;

namespace Modbot.Companion.Tests.Clips;

/// <summary>
/// The arithmetic that keeps a clip's sound level with its picture.
/// </summary>
/// <remarks>
/// This is the half of the sound in a clip that can be checked on a machine with no sound hardware,
/// and it is the half where a mistake is invisible until somebody watches a clip and finds the
/// voices a second behind the mouths. The capture itself needs a PC playing something and a
/// moderator to listen; none of that is here.
/// </remarks>
public class ClipSoundRuleTests
{
    [Fact]
    public void FifteenPicturesASecondAtTheUsualRateIsAWholeNumberEveryTime()
    {
        // 48,000 divided by 15. The ordinary case, and the one where nothing can go wrong.
        for (var frame = 0; frame < 100; frame++)
            Assert.Equal(3_200, ClipSoundRule.SamplesForFrame(frame, 48_000, 15));
    }

    [Fact]
    public void TheOtherRateComesOutWholeToo()
        => Assert.Equal(2_940, ClipSoundRule.SamplesForFrame(7, 44_100, 15));

    [Fact]
    public void ARateThatDoesNotDivideEvenlyStillAddsUpExactly()
    {
        // The case the whole design is for. 48,000 samples over 7 pictures is 6,857 and a seventh,
        // so some pictures have to carry one more than others — and the running total after any
        // number of them has to be exactly right, because a sample lost every second is a clip
        // whose sound has slid a second behind the picture after a quarter of an hour.
        var total = 0L;

        for (var frame = 0; frame < 7 * 60; frame++)
        {
            total += ClipSoundRule.SamplesForFrame(frame, 48_000, 7);

            Assert.Equal(
                ClipSoundRule.SamplesUpTo(frame + 1, 48_000, 7),
                total);
        }

        // A minute of pictures is a minute of sound, to the sample.
        Assert.Equal(48_000L * 60, total);
    }

    [Fact]
    public void NoPictureEverCarriesMoreThanTheBufferHolds()
    {
        // The buffer handed between the sound and the recorder is made once, at this size.
        foreach (var (rate, fps) in new[] { (48_000, 15), (44_100, 15), (48_000, 7), (44_100, 30) })
        {
            var most = ClipSoundRule.MostSamplesInAFrame(rate, fps);

            for (var frame = 0; frame < 200; frame++)
                Assert.True(ClipSoundRule.SamplesForFrame(frame, rate, fps) <= most);
        }
    }

    [Fact]
    public void WhereAStretchOfSoundBelongsIsWorkedOutFromHowMuchCameBefore()
    {
        // Never from a clock. A second of sound is a second into the file, whatever the machine was
        // doing while it was recorded.
        Assert.Equal(0, ClipSoundRule.TimeFor(0, 48_000));
        Assert.Equal(10_000_000L, ClipSoundRule.TimeFor(48_000, 48_000));
        Assert.Equal(5_000_000L, ClipSoundRule.TimeFor(24_000, 48_000));
        Assert.Equal(10_000_000L, ClipSoundRule.TimeFor(44_100, 44_100));
    }

    [Fact]
    public void SoundAndPictureStillDescribeTheSameMomentAfterTheLongestClip()
    {
        // The two are placed separately — the picture by how many frames there have been, the sound
        // by how much sound there has been — so this is the check that they still agree at the end
        // of the longest clip anybody can ask for.
        //
        // They do not agree to the tick, and the reason is the picture's, not the sound's: one
        // frame at fifteen a second is 666,666 and two thirds of a hundred-nanosecond unit, and a
        // frame can only be told a whole number of them. The picture therefore loses two thirds of
        // a unit a frame. Over five minutes that is three tenths of a millisecond, which no ear
        // can hear and no player can show; what it must not be allowed to become is the sound
        // drifting as well, which is what the running totals above prevent.
        const int Rate = 48_000;
        const int Fps = 15;
        const long Frames = 5 * 60 * Fps;

        var samples = ClipSoundRule.SamplesUpTo(Frames, Rate, Fps);

        var pictureEndsAt = Frames * (10_000_000L / Fps);
        var soundEndsAt = ClipSoundRule.TimeFor(samples, Rate);

        // A millisecond, against five minutes.
        Assert.True(
            Math.Abs(soundEndsAt - pictureEndsAt) < 10_000,
            $"The sound ended {soundEndsAt - pictureEndsAt} ticks away from the picture.");

        // And the sound itself is exactly five minutes, to the sample.
        Assert.Equal(5 * 60 * 10_000_000L, soundEndsAt);
    }

    [Fact]
    public void NothingIsAskedForBeforeTheFirstPicture()
    {
        Assert.Equal(0, ClipSoundRule.SamplesForFrame(-1, 48_000, 15));
        Assert.Equal(0, ClipSoundRule.SamplesUpTo(0, 48_000, 15));
        Assert.Equal(0, ClipSoundRule.TimeFor(-5, 48_000));
    }

    [Fact]
    public void ARateOfNothingAsksForNothingRatherThanFalling()
    {
        // A machine that could not open any sound at all leaves the rate at zero, and the recorder
        // writes a silent clip. Nothing here may divide by it.
        Assert.Equal(0, ClipSoundRule.SamplesForFrame(3, 0, 15));
        Assert.Equal(0, ClipSoundRule.SamplesForFrame(3, 48_000, 0));
        Assert.Equal(0, ClipSoundRule.MostSamplesInAFrame(0, 15));
        Assert.Equal(0, ClipSoundRule.TimeFor(100, 0));
    }

    [Fact]
    public void TwoProgramsAddedTogetherStayInsideWhatOneNumberCanSay()
    {
        Assert.Equal(0, ClipSoundRule.Mix(0, 0));
        Assert.Equal(300, ClipSoundRule.Mix(100, 200));
        Assert.Equal(-300, ClipSoundRule.Mix(-100, -200));

        // Two loud programs at once. The sum is held at the end rather than rolling over, because
        // a number that rolls over sounds like a gunshot in the middle of a clip.
        Assert.Equal(short.MaxValue, ClipSoundRule.Mix(short.MaxValue, short.MaxValue));
        Assert.Equal(short.MinValue, ClipSoundRule.Mix(short.MinValue, short.MinValue));
        Assert.Equal(short.MaxValue, ClipSoundRule.Mix(short.MaxValue, 1));
    }

    [Fact]
    public void AddingSilenceChangesNothing()
    {
        // What a program that is closed, silent or not being recorded contributes.
        foreach (var value in new short[] { short.MinValue, -1, 0, 1, short.MaxValue })
            Assert.Equal(value, ClipSoundRule.Mix(value, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 4)]
    [InlineData(7, 4)]
    [InlineData(8, 8)]
    [InlineData(-9, 0)]
    public void BytesAreRoundedDownToWholeMomentsOfSound(int bytes, int expected)
    {
        // Half a moment is a left channel with no right channel, and once one of those is in the
        // file every moment after it comes out the wrong way round.
        Assert.Equal(expected, ClipSoundRule.WholeSamples(bytes));
    }

    [Fact]
    public void HalfASecondIsHeldWhileThePictureCatchesUp()
    {
        Assert.Equal(24_000, ClipSoundRule.MostSamplesHeld(48_000));
        Assert.Equal(22_050, ClipSoundRule.MostSamplesHeld(44_100));

        // Never nothing, whatever it is handed, because a buffer of no size cannot be written into.
        Assert.True(ClipSoundRule.MostSamplesHeld(0) > 0);
    }

    [Fact]
    public void BothRatesTheSoundCanOpenAtAreOnesTheEncoderTakes()
    {
        // The two Windows' own encoder accepts for the sound in an mp4. A third would mean a clip
        // with no sound track at all rather than a clip at a different rate.
        Assert.Equal(48_000, ClipSoundRule.PreferredSampleRate);
        Assert.Equal(44_100, ClipSoundRule.FallbackSampleRate);
    }

    [Fact]
    public void OneMomentOfSoundIsTwoChannelsOfTwoBytes()
    {
        Assert.Equal(2, ClipSoundRule.Channels);
        Assert.Equal(2, ClipSoundRule.BytesPerChannel);
        Assert.Equal(4, ClipSoundRule.BytesPerSample);
    }
}
