namespace Modbot.Companion.Listening;

/// <summary>
/// Brings a microphone's own rate — usually 48,000 samples a second — to the rate the phrase model
/// was trained at, by reading between the samples.
/// </summary>
/// <remarks>
/// <para>Straight-line interpolation, which is about fifteen lines and no library. It is not the
/// best resampler there is; what it costs is a little noise above 8,000 cycles a second, and
/// nothing in that band is part of telling one word from another.</para>
/// <para>Sound arrives a buffer at a time, so where it had got to is carried from one buffer to the
/// next, along with the last sample of the one before: there is no seam every fifth of a second. A
/// sample that falls exactly on the last one of a buffer is left for the next buffer, which reads
/// it as "between the last sample and the next one" with nothing of the next one in it. Reading it
/// here instead would need the sample after the end, which does not exist yet.</para>
/// <para>That case is not rare. A microphone already at the model's rate lands on every sample,
/// the last one included, so it met it on every buffer — and a Bluetooth headset in call mode runs
/// at exactly that rate. Until 2026-09-26 the loop read one past the end there and threw on
/// Windows' audio thread.</para>
/// <para>It holds two numbers. Nothing is read and nothing is sent. Not thread-safe: the owner
/// calls it from one thread, or under its own lock.</para>
/// </remarks>
public sealed class Resampler
{
    private readonly int _toRate;

    /// <summary>Where the next sample falls, counted from the start of the next buffer.</summary>
    private double _position;

    /// <summary>The last sample of the buffer before, which a position below zero reads from.</summary>
    private float _previous;

    /// <param name="toRate">The rate to bring sound to, in samples a second.</param>
    public Resampler(int toRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toRate);
        _toRate = toRate;
    }

    /// <summary>Forgets the buffer before, for a microphone that has just been opened.</summary>
    public void Reset()
    {
        _position = 0;
        _previous = 0;
    }

    /// <summary>
    /// Adds <paramref name="mono"/>, recorded at <paramref name="fromRate"/>, to
    /// <paramref name="into"/> at the rate this was made for.
    /// </summary>
    public void Add(ReadOnlySpan<float> mono, int fromRate, List<float> into)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromRate);
        ArgumentNullException.ThrowIfNull(into);

        if (mono.IsEmpty)
            return;

        var step = fromRate / (double)_toRate;
        var position = _position;
        var last = mono.Length - 1;

        // Strictly below the last sample: a position on it needs the one after, which belongs to
        // the next buffer. It is carried over as -1 and read there, with nothing lost.
        while (position < last)
        {
            var whole = (int)Math.Floor(position);
            var part = (float)(position - whole);

            var before = whole < 0 ? _previous : mono[whole];
            var after = mono[whole + 1];

            into.Add(before + ((after - before) * part));
            position += step;
        }

        _position = position - mono.Length;
        _previous = mono[last];
    }
}
