namespace Modbot.Companion.Clips;

/// <summary>
/// The arithmetic that keeps a clip's sound level with its picture.
/// </summary>
/// <remarks>
/// <para><strong>It reads nothing and sends nothing.</strong> Every method here is a sum. There is
/// no path from this file to the disk, to a microphone, to a graphics card or to the network; it
/// exists so that the part of the recorder that <em>is</em> all of those things has no arithmetic
/// in it worth arguing about, and so that the arithmetic can be checked on a machine with no sound
/// hardware at all.</para>
/// <para><strong>What the sums are for.</strong> A clip is written one picture at a time, fifteen
/// times a second, and every picture carries the sound that belongs beside it. How many samples
/// that is has to come out exactly right every time: a sample too few each picture is a clip whose
/// sound has slid a second behind the picture by the time anybody watches it, and the mistake is
/// invisible until then. So the count for one picture is worked out as the difference between two
/// running totals rather than as a rounded share, which cannot drift however long the recording
/// runs and whatever the rates are.</para>
/// <para>The same totals give the moment each stretch of sound belongs at, which is what the
/// encoder is told, and it is worked out from the samples already written rather than from a clock
/// — so the sound is placed by how much of it there is, exactly as the picture is placed by how
/// many frames there have been.</para>
/// </remarks>
public static class ClipSoundRule
{
    /// <summary>
    /// Samples a second in a clip's sound. Forty-eight thousand, which is what a PC's sound system
    /// almost always runs at, so nothing has to be stretched on the way in.
    /// </summary>
    public const int PreferredSampleRate = 48_000;

    /// <summary>
    /// The rate tried when a program will not hand its sound over at the preferred one.
    /// </summary>
    /// <remarks>
    /// Both of these are rates the encoder in Windows will take. Nothing else is tried: a rate the
    /// encoder refuses would mean a clip with no sound track at all, which is worse than a clip
    /// whose sound came from the other rate.
    /// </remarks>
    public const int FallbackSampleRate = 44_100;

    /// <summary>Left and right. Everything in a clip's sound is two channels.</summary>
    public const int Channels = 2;

    /// <summary>How many bytes one sample of one channel takes. Sixteen bits.</summary>
    public const int BytesPerChannel = 2;

    /// <summary>How many bytes one moment of sound takes, both channels together.</summary>
    public const int BytesPerSample = Channels * BytesPerChannel;

    /// <summary>How many samples' worth of sound is held while the picture catches up.</summary>
    /// <remarks>
    /// Half a second. Sound arrives from Windows on its own schedule and pictures are written on
    /// the recorder's, and the two never line up perfectly; a little has to be held so that a
    /// picture always has sound to put beside it. Half a second is enough for that and short enough
    /// that a machine which falls badly behind loses a moment of sound rather than growing a buffer
    /// for the rest of the evening. What is dropped when it fills is the oldest, because the oldest
    /// is the part no longer level with any picture that has not been written.
    /// </remarks>
    public static int MostSamplesHeld(int sampleRate) => Math.Max(1, sampleRate / 2);

    /// <summary>
    /// How many samples of sound belong with every picture up to, but not including,
    /// <paramref name="frames"/>.
    /// </summary>
    public static long SamplesUpTo(long frames, int sampleRate, int framesPerSecond)
    {
        if (frames <= 0 || sampleRate <= 0 || framesPerSecond <= 0)
            return 0;

        return frames * sampleRate / framesPerSecond;
    }

    /// <summary>
    /// How many samples of sound belong with one picture — the one numbered
    /// <paramref name="frame"/>, counting from the first picture in the file.
    /// </summary>
    /// <remarks>
    /// The difference between two running totals, never a rounded share of a second. At fifteen
    /// pictures a second and forty-eight thousand samples a second this is three thousand two
    /// hundred every time; at rates that do not divide evenly it is one more on some pictures than
    /// on others, and the totals still come out exact after any number of them. That is the whole
    /// point: a share rounded the same way every time is a clip whose sound slides.
    /// </remarks>
    public static int SamplesForFrame(long frame, int sampleRate, int framesPerSecond)
    {
        if (frame < 0)
            return 0;

        var before = SamplesUpTo(frame, sampleRate, framesPerSecond);
        var after = SamplesUpTo(frame + 1, sampleRate, framesPerSecond);

        return (int)(after - before);
    }

    /// <summary>
    /// The most samples any one picture can call for, so the buffer handed about can be made once.
    /// </summary>
    public static int MostSamplesInAFrame(int sampleRate, int framesPerSecond)
    {
        if (sampleRate <= 0 || framesPerSecond <= 0)
            return 0;

        // One more than the even share covers every picture: the totals are exact, so no picture
        // can ever call for two more than its neighbour.
        return (sampleRate / framesPerSecond) + 1;
    }

    /// <summary>
    /// Where a stretch of sound belongs in the file, in the hundred-nanosecond units the encoder
    /// counts in, given how many samples went in before it.
    /// </summary>
    /// <remarks>
    /// Worked out from the samples already written rather than from any clock, so the sound is
    /// placed by how much of it there is — exactly the way the picture is placed by how many frames
    /// there have been. The two therefore describe the same moment for as long as the file runs.
    /// </remarks>
    public static long TimeFor(long samples, int sampleRate)
    {
        if (samples <= 0 || sampleRate <= 0)
            return 0;

        return samples * 10_000_000L / sampleRate;
    }

    /// <summary>Two programs' sound added together, held inside what one number can say.</summary>
    /// <remarks>
    /// Adding is what mixing is. Two loud sounds added can come out louder than the loudest a
    /// sample can describe, and a number that rolls over sounds like a gunshot, so the sum is held
    /// at the ends instead. Two programs at ordinary levels never reach them.
    /// </remarks>
    public static short Mix(short one, short other)
    {
        var total = one + other;

        return total switch
        {
            > short.MaxValue => short.MaxValue,
            < short.MinValue => short.MinValue,
            _ => (short)total,
        };
    }

    /// <summary>The nearest whole moment of sound at or below <paramref name="bytes"/>.</summary>
    /// <remarks>
    /// Sound is handed about as bytes, and half of one moment of it is a left channel with no right
    /// channel — which, once it is in the file, makes every moment after it the wrong way round.
    /// Anywhere bytes are dropped or counted, they are rounded to whole moments first.
    /// </remarks>
    public static int WholeSamples(int bytes) => bytes <= 0 ? 0 : bytes / BytesPerSample * BytesPerSample;
}
