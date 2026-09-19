using Modbot.Companion.Voice;

namespace Modbot.Companion.Sounds;

/// <summary>
/// The short sound the client makes when it has something to tell the moderator: two tones, made
/// from a formula rather than read from a file.
/// </summary>
/// <remarks>
/// <para><strong>Nothing is read and nothing is sent.</strong> The samples are written into an
/// array by the arithmetic below, and played on this PC through the same output the voice uses.
/// There is no audio file in the client, so there is nothing whose licence or origin anybody has to
/// check, and nothing to go missing from an install.</para>
/// <para><strong>Two tones, not one.</strong> A single beep is a beep from anything — a laptop, a
/// microwave, a dozen other programs. A rising pair is recognisable as this one, and the whole thing
/// is still under a fifth of a second.</para>
/// <para>Each tone fades in and out over a few milliseconds. A sine that starts at full height
/// starts with a click, which is the difference between a sound and a fault.</para>
/// </remarks>
public static class Bleep
{
    /// <summary>The rate the samples are made at. The players resample if the device wants another.</summary>
    public const int SampleRate = 48_000;

    /// <summary>The lower tone: A above middle C, an octave up.</summary>
    public const double FirstTone = 880;

    public const double SecondTone = 1245;

    public static readonly TimeSpan FirstLength = TimeSpan.FromMilliseconds(80);

    public static readonly TimeSpan SecondLength = TimeSpan.FromMilliseconds(110);

    /// <summary>How long each tone takes to come up and to go away again.</summary>
    public static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(6);

    /// <summary>
    /// How loud the tones are made, before the moderator's own volume is applied. Short of the top
    /// so that a volume of 100 is still a clean sound rather than a clipped one.
    /// </summary>
    public const float Height = 0.7f;

    /// <summary>How long the whole sound lasts.</summary>
    public static TimeSpan Length => FirstLength + SecondLength;

    /// <summary>
    /// The sound, ready to play. Cheap enough to call whenever, and held by the one object that
    /// plays it rather than remade per bleep.
    /// </summary>
    public static VoiceClip Make(int sampleRate = SampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);

        var first = Samples(FirstLength, sampleRate);
        var second = Samples(SecondLength, sampleRate);
        var samples = new float[first + second];

        Write(samples.AsSpan(0, first), FirstTone, sampleRate);
        Write(samples.AsSpan(first, second), SecondTone, sampleRate);

        return new VoiceClip(samples, sampleRate);
    }

    private static int Samples(TimeSpan length, int sampleRate)
        => (int)Math.Round(length.TotalSeconds * sampleRate);

    /// <summary>One tone: a sine at that pitch, faded in at the start and out at the end.</summary>
    private static void Write(Span<float> into, double hertz, int sampleRate)
    {
        var fade = Math.Min(Samples(Fade, sampleRate), into.Length / 2);
        var step = 2 * Math.PI * hertz / sampleRate;

        for (var i = 0; i < into.Length; i++)
        {
            var level = Height;

            if (fade > 0)
            {
                if (i < fade)
                    level *= i / (float)fade;
                else if (i >= into.Length - fade)
                    level *= (into.Length - 1 - i) / (float)fade;
            }

            into[i] = level * (float)Math.Sin(step * i);
        }
    }
}
