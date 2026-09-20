using Modbot.Companion.Clips;

namespace Modbot.Companion.Tests.Clips;

/// <summary>
/// The sound waiting between Windows and the recorder: what happens when a program has nothing to
/// give, when it has less than a picture needs, and when it has far more.
/// </summary>
/// <remarks>
/// This is where "the clip still records if a program is not running" is actually decided, and it
/// is the part of the sound in a clip that can be checked with no sound hardware at all. Nothing
/// here opens anything.
/// </remarks>
public class HeldSoundTests
{
    [Fact]
    public void AProgramThatIsNotRunningLeavesEveryPictureSilent()
    {
        // The fallback the whole feature rests on. Discord closed, VRChat muted, a program Windows
        // refused to hand over — all of them end here, with nothing ever arriving, and what the
        // recorder gets is silence rather than a failure.
        var held = new HeldSound(4_000);

        // The caller hands over a stretch it has already silenced.
        var into = new byte[400];

        Assert.Equal(0, held.Take(into));
        Assert.All(into, b => Assert.Equal(0, b));
        Assert.Equal(0, held.Waiting);
    }

    [Fact]
    public void AProgramWithLessThanAPictureNeedsFillsWhatItHasAndLeavesTheRest()
    {
        // Not a shorter stretch: a stretch shorter than the picture it belongs to is exactly how
        // sound slides away from the picture over a few minutes.
        var held = new HeldSound(4_000);
        held.Put(Loud(40));

        var into = new byte[400];

        Assert.Equal(40, held.Take(into));
        Assert.All(into[..40], b => Assert.Equal(7, b));
        Assert.All(into[40..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void WhatGoesInComesOutInOrder()
    {
        var held = new HeldSound(4_000);
        held.Put(Counted(0, 40));
        held.Put(Counted(40, 40));

        var into = new byte[80];
        Assert.Equal(80, held.Take(into));
        Assert.Equal(Counted(0, 80), into);
    }

    [Fact]
    public void ItKeepsGoingRoundRatherThanFillingUp()
    {
        // The recorder takes a picture's worth fifteen times a second for as long as VRChat runs,
        // so what matters is that the buffer wraps cleanly rather than that it is big.
        var held = new HeldSound(400);
        var into = new byte[120];
        var next = 0;

        for (var turn = 0; turn < 100; turn++)
        {
            held.Put(Counted(next, 120));

            Assert.Equal(120, held.Take(into));
            Assert.Equal(Counted(next, 120), into);

            next += 120;
        }
    }

    [Fact]
    public void AMachineFallingBehindLosesTheOldestSoundRatherThanGrowing()
    {
        // The oldest is the part no picture still to be written is level with, so it is the part
        // that goes. The alternative is a buffer that grows for the rest of the evening.
        var held = new HeldSound(400);

        held.Put(Counted(0, 400));
        held.Put(Counted(400, 80));

        Assert.Equal(400, held.Room);
        Assert.Equal(400, held.Waiting);

        var into = new byte[400];
        Assert.Equal(400, held.Take(into));

        // The first 80 have gone; what is left runs from 80 to 480.
        Assert.Equal(Counted(80, 400), into);
    }

    [Fact]
    public void MoreThanTheBufferHoldsAtOnceKeepsTheNewestEnd()
    {
        var held = new HeldSound(400);
        held.Put(Counted(0, 1_200));

        var into = new byte[400];
        Assert.Equal(400, held.Take(into));
        Assert.Equal(Counted(800, 400), into);
    }

    [Fact]
    public void ForgettingTakesItBackToSilence()
    {
        // What happens when the first picture lands: sound has been gathering since VRChat's
        // window was found, and putting that in front of the first picture would be a clip whose
        // sound runs ahead of it.
        var held = new HeldSound(400);
        held.Put(Loud(400));
        Assert.Equal(400, held.Waiting);

        held.Forget();

        Assert.Equal(0, held.Waiting);

        var into = new byte[400];
        Assert.Equal(0, held.Take(into));
    }

    [Fact]
    public void OnlyWholeMomentsOfSoundEverMove()
    {
        // Half a moment is a left channel with no right channel, and once one is in the file every
        // moment after it comes out the wrong way round.
        var held = new HeldSound(402);

        Assert.Equal(400, held.Room);

        held.Put(new byte[6]);
        Assert.Equal(4, held.Waiting);

        held.Put(new byte[3]);
        Assert.Equal(4, held.Waiting);

        Assert.Equal(4, held.Take(new byte[7]));
    }

    [Fact]
    public void ABufferAskedForNothingStillHasRoomForOneMoment()
    {
        // Nothing may ever be written into an array of no size.
        Assert.Equal(ClipSoundRule.BytesPerSample, new HeldSound(0).Room);
        Assert.Equal(ClipSoundRule.BytesPerSample, new HeldSound(-100).Room);
    }

    [Fact]
    public void PuttingNothingInChangesNothing()
    {
        var held = new HeldSound(400);
        held.Put([]);

        Assert.Equal(0, held.Waiting);
    }

    private static byte[] Loud(int bytes) => [.. Enumerable.Repeat((byte)7, bytes)];

    private static byte[] Counted(int from, int bytes)
        => [.. Enumerable.Range(from, bytes).Select(n => (byte)(n % 251))];
}
