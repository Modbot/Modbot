using Modbot.Companion.Voice;

namespace Modbot.Companion.Sounds;

/// <summary>
/// The short sound the client makes when it has something to tell the moderator: two struck notes,
/// made from a formula rather than read from a file.
/// </summary>
/// <remarks>
/// <para><strong>Nothing is read and nothing is sent.</strong> The samples are written into an
/// array by the arithmetic below, and played on this PC through the same output the voice uses.
/// There is no audio file in the client, so there is nothing whose licence or origin anybody has to
/// check, and nothing to go missing from an install. A moderator who wants a different sound points
/// the setting at a file of their own (<see cref="SoundFile"/>); the client still ships none.</para>
/// <para><strong>What changed on 2026-09-19, and why.</strong> This used to be two sine tones, 880
/// then 1245 hertz, each 6 milliseconds from silence to full height and 6 milliseconds back. That
/// is the recipe for a smoke alarm: high, bare, abrupt at both ends, and two pitches with no
/// musical relation to each other. A moderator hears this forty times in an evening and said so.
/// Four things are different now, and each is doing a job:</para>
/// <list type="bullet">
/// <item><description><strong>Lower.</strong> 440 hertz and 659, where it was 880 and 1245. The
/// old pair sat in the band the ear is most sensitive to and most quickly annoyed by.</description></item>
/// <item><description><strong>A musical interval.</strong> The second note is a perfect fifth above
/// the first — exactly three halves of its pitch — so the pair reads as two notes of one sound
/// rather than as two unrelated beeps.</description></item>
/// <item><description><strong>Struck, not switched on.</strong> Each note comes up over
/// <see cref="Attack"/> along a curve rather than a ramp, and then decays the way a struck thing
/// decays: fast at first, trailing off, with the second note beginning while the first is still
/// ringing.</description></item>
/// <item><description><strong>Overtones.</strong> A handful of quiet partials above each note, each
/// dying faster than the one below it, which is what separates the sound of something being hit
/// from the sound of a test tone.</description></item>
/// </list>
/// <para>It is still short — under half a second — and still made of arithmetic that anybody can
/// read.</para>
/// </remarks>
public static class Bleep
{
    /// <summary>The rate the samples are made at. The players resample if the device wants another.</summary>
    public const int SampleRate = 48_000;

    /// <summary>The first note: A above middle C.</summary>
    public const double FirstTone = 440;

    /// <summary>
    /// The second note: a perfect fifth above the first, which is three halves of its pitch. The
    /// simplest interval there is after the octave, and the reason the pair sounds like one thing.
    /// </summary>
    public const double SecondTone = FirstTone * 3 / 2;

    /// <summary>How long one note takes to fade to nothing.</summary>
    public static readonly TimeSpan NoteLength = TimeSpan.FromMilliseconds(340);

    /// <summary>
    /// When the second note is struck, measured from the first. Short enough that the first is
    /// still ringing under it, which is what makes them one sound rather than two.
    /// </summary>
    public static readonly TimeSpan SecondStartsAt = TimeSpan.FromMilliseconds(140);

    /// <summary>
    /// How long a note takes to reach its full height. Long enough that nothing starts with a
    /// click, short enough that the sound still reads as something struck rather than swelling.
    /// </summary>
    public static readonly TimeSpan Attack = TimeSpan.FromMilliseconds(18);

    /// <summary>
    /// How long a note takes to fall to about a third of its height, and then to a third of that,
    /// and so on. This is the number that makes it a decay with a tail rather than a fade.
    /// </summary>
    public static readonly TimeSpan Decay = TimeSpan.FromMilliseconds(90);

    /// <summary>
    /// The last stretch of a note, taken smoothly to exactly nothing. The decay above never quite
    /// reaches zero, and a buffer that ends on a number that is not zero ends with a click.
    /// </summary>
    public static readonly TimeSpan Release = TimeSpan.FromMilliseconds(45);

    /// <summary>
    /// How loud the sound is made, before the moderator's own volume is applied. Short of the top
    /// so that a volume of 100 is still a clean sound rather than a clipped one.
    /// </summary>
    public const float Height = 0.7f;

    /// <summary>
    /// The overtones above each note, as a fraction of the note's own height, starting with the
    /// second partial.
    /// </summary>
    /// <remarks>
    /// Quiet on purpose. The first of them is under half the height of the note below it, which is
    /// the point at which a partial colours a note rather than becoming a note of its own — it is
    /// also what keeps the sound's pitch measurable by counting how often it crosses zero, which is
    /// how <c>BleepTests</c> checks it without a transform.
    /// </remarks>
    private static readonly float[] Overtones = [0.30f, 0.13f, 0.06f];

    /// <summary>
    /// How much faster each partial above the first dies away. A struck thing loses its high
    /// overtones first, and a note whose partials all decayed together would sound like an organ.
    /// </summary>
    private const double OvertonesDieFaster = 0.55;

    /// <summary>How long the whole sound lasts.</summary>
    public static TimeSpan Length => SecondStartsAt + NoteLength;

    /// <summary>
    /// The sound, ready to play. Cheap enough to call whenever, and held by the one object that
    /// plays it rather than remade per sound.
    /// </summary>
    public static VoiceClip Make(int sampleRate = SampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);

        var samples = new float[Samples(Length, sampleRate)];

        Strike(samples, at: 0, FirstTone, sampleRate);
        Strike(samples, at: Samples(SecondStartsAt, sampleRate), SecondTone, sampleRate);

        // Made at whatever height the notes happened to add up to, then brought to the one height
        // the volume is applied to, so overlapping notes can never clip and the sound's loudness
        // does not depend on the numbers above.
        var loudest = 0f;
        foreach (var sample in samples)
            loudest = Math.Max(loudest, Math.Abs(sample));

        if (loudest > 0)
        {
            var scale = Height / loudest;
            for (var i = 0; i < samples.Length; i++)
                samples[i] *= scale;
        }

        return new VoiceClip(samples, sampleRate);
    }

    private static int Samples(TimeSpan length, int sampleRate)
        => (int)Math.Round(length.TotalSeconds * sampleRate);

    /// <summary>
    /// One note struck into the buffer, added to whatever is already there so a note that is still
    /// ringing is not cut off by the next one.
    /// </summary>
    private static void Strike(Span<float> into, int at, double hertz, int sampleRate)
    {
        var length = Math.Min(Samples(NoteLength, sampleRate), into.Length - at);
        if (length <= 0)
            return;

        var attack = Math.Max(1, Math.Min(Samples(Attack, sampleRate), length));
        var release = Math.Max(1, Math.Min(Samples(Release, sampleRate), length));
        var decay = Math.Max(1.0, Decay.TotalSeconds * sampleRate);

        for (var i = 0; i < length; i++)
        {
            // Up along the first quarter of a cosine rather than a straight line: a ramp still has
            // a corner at the top, and a corner is a click you can hear.
            var level = i < attack
                ? (float)(0.5 - (0.5 * Math.Cos(Math.PI * i / attack)))
                : 1f;

            // Down the way a struck thing goes down: quickly at first, then trailing.
            level *= (float)Math.Exp(-i / decay);

            // And taken to exactly nothing over the last stretch, because the line above never
            // quite gets there.
            var left = length - 1 - i;
            if (left < release)
                level *= (float)(0.5 - (0.5 * Math.Cos(Math.PI * left / release)));

            var value = (float)Math.Sin(2 * Math.PI * hertz * i / sampleRate);

            for (var overtone = 0; overtone < Overtones.Length; overtone++)
            {
                var partial = overtone + 2;
                var fades = Math.Exp(-i * OvertonesDieFaster * (partial - 1) / decay);
                value += (float)(Overtones[overtone] * fades * Math.Sin(2 * Math.PI * hertz * partial * i / sampleRate));
            }

            into[at + i] += level * value;
        }
    }
}
