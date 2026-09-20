namespace Modbot.Companion.Clips;

/// <summary>
/// The last half second of one program's sound, in a buffer that never grows.
/// </summary>
/// <remarks>
/// <para><strong>It reads nothing and sends nothing.</strong> It is a fixed array and two numbers.
/// Nothing here opens a microphone, asks Windows for anything or reaches the network; what fills it
/// is the one file allowed to record a program's sound, and what empties it is the recorder writing
/// the clip. It lives here, away from both of them, because it is the part that can be checked on a
/// machine with no sound hardware.</para>
/// <para><strong>Why anything is held at all.</strong> Sound arrives from Windows on Windows'
/// schedule and pictures are written on the recorder's, and the two never line up. A little has to
/// be waiting so that every picture has sound to put beside it.</para>
/// <para><strong>What happens at both ends, and why.</strong> Ask for more than there is and what
/// is missing stays silent, rather than a short stretch being handed over — a stretch shorter than
/// the picture it belongs to is exactly how sound slides away from the picture over a few minutes.
/// A program that is closed, muted or was never being recorded gives silence forever, which is a
/// clip with nothing in that part of its sound and never a broken one. Put in more than there is
/// room for and the <em>oldest</em> goes, because the oldest is the part no picture still to be
/// written is level with.</para>
/// <para>Both ends take the same lock and neither does anything else while it holds it: one end is
/// Windows' own audio thread, and holding that up is how a program makes everybody else's sound
/// stutter.</para>
/// </remarks>
public sealed class HeldSound
{
    private readonly byte[] _bytes;
    private readonly Lock _gate = new();
    private int _start;
    private int _count;

    /// <param name="bytes">
    /// How much is held. Rounded down to whole moments of sound, and never less than one: half a
    /// moment is a left channel with no right channel, and once one of those is in the file every
    /// moment after it comes out the wrong way round.
    /// </param>
    public HeldSound(int bytes)
        => _bytes = new byte[Math.Max(ClipSoundRule.BytesPerSample, ClipSoundRule.WholeSamples(bytes))];

    /// <summary>How much is held right now.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>How much can be held at once.</summary>
    public int Room => _bytes.Length;

    /// <summary>Puts sound in, dropping the oldest when there is no room left.</summary>
    public void Put(ReadOnlySpan<byte> sound)
    {
        var whole = ClipSoundRule.WholeSamples(sound.Length);
        if (whole == 0)
            return;

        lock (_gate)
        {
            // More than the buffer holds at once. Keep the newest end of it: the older end is
            // sound that no picture still to be written is level with.
            if (whole >= _bytes.Length)
            {
                sound = sound[(whole - _bytes.Length)..whole];
                whole = _bytes.Length;
                _start = 0;
                _count = 0;
            }
            else if (_count + whole > _bytes.Length)
            {
                var drop = ClipSoundRule.WholeSamples(
                    _count + whole - _bytes.Length + ClipSoundRule.BytesPerSample - 1);

                _start = (_start + drop) % _bytes.Length;
                _count -= drop;
            }

            var at = (_start + _count) % _bytes.Length;
            var first = Math.Min(whole, _bytes.Length - at);

            sound[..first].CopyTo(_bytes.AsSpan(at, first));

            if (first < whole)
                sound[first..whole].CopyTo(_bytes.AsSpan(0, whole - first));

            _count += whole;
        }
    }

    /// <summary>
    /// Takes what there is into <paramref name="into"/>, leaving whatever is missing untouched.
    /// </summary>
    /// <remarks>
    /// The caller hands over a stretch it has already made silent, so "leaving it untouched" is
    /// leaving it silent. Returns how much was really there, which is what the recorder's log
    /// counts; it never returns more than a whole number of moments of sound.
    /// </remarks>
    public int Take(Span<byte> into)
    {
        lock (_gate)
        {
            var wanted = Math.Min(ClipSoundRule.WholeSamples(into.Length), _count);
            if (wanted == 0)
                return 0;

            var first = Math.Min(wanted, _bytes.Length - _start);
            _bytes.AsSpan(_start, first).CopyTo(into);

            if (first < wanted)
                _bytes.AsSpan(0, wanted - first).CopyTo(into[first..]);

            _start = (_start + wanted) % _bytes.Length;
            _count -= wanted;

            return wanted;
        }
    }

    /// <summary>Throws away everything held, so a clip starts level with its picture.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _start = 0;
            _count = 0;
        }
    }
}
