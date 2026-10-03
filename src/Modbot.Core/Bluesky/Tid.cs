using System.Security.Cryptography;
using Modbot.Core.Time;

namespace Modbot.Core.Bluesky;

/// <summary>
/// Bluesky's record keys for posts: a TID, thirteen characters that carry a time and a clock id
/// (atproto.com/specs/tid; Bluesky design §3.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why Modbot makes its own.</strong> A post's key is picked by the client, so Modbot makes
/// it once, writes it on the row before anything is sent, and sends every try under that same key
/// with <c>swapRecord: null</c>: a second try can never make a second post (§3.4).
/// </para>
/// <para>
/// The time is <see cref="IModbotClock"/>'s, never the system clock (foundation §4.4). The value is a
/// 64-bit number: the top bit 0, then 53 bits of microseconds since 1970, then a 10-bit clock id
/// picked at random once per process. It is written in base32 with the alphabet
/// <c>234567abcdefghijklmnopqrstuvwxyz</c>, so keys sort as their times do.
/// </para>
/// </remarks>
public static class Tid
{
    /// <summary>The base32 alphabet TIDs are written in, in sort order.</summary>
    public const string Alphabet = "234567abcdefghijklmnopqrstuvwxyz";

    /// <summary>A TID is always this long.</summary>
    public const int Length = 13;

    private static readonly long UnixEpochTicks = DateTimeOffset.UnixEpoch.UtcTicks;

    /// <summary>This process's clock id: 10 bits, picked once.</summary>
    private static readonly int ProcessClockId = RandomNumberGenerator.GetInt32(1024);

    private static readonly Lock Gate = new();
    private static long _lastMicros;

    /// <summary>
    /// A new TID from <paramref name="clock"/>'s time. Two made in the same microsecond, or with the
    /// clock set back, still come out in order: the later one is made one microsecond after the last.
    /// </summary>
    public static string Next(IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        long micros;

        lock (Gate)
        {
            micros = MicrosOf(clock.UtcNow);
            if (micros <= _lastMicros)
                micros = _lastMicros + 1;

            _lastMicros = micros;
        }

        return Of(micros, ProcessClockId);
    }

    /// <summary>The TID for <paramref name="at"/> with <paramref name="clockId"/> (0 to 1023).</summary>
    public static string At(DateTimeOffset at, int clockId) => Of(MicrosOf(at), clockId);

    /// <summary>Whether <paramref name="value"/> is a TID: thirteen characters of the alphabet, the first one of its first sixteen.</summary>
    public static bool IsTid(string? value)
    {
        if (value is not { Length: Length })
            return false;

        // The top bit is always 0, so the first character holds at most 4 bits.
        if (Alphabet.IndexOf(value[0], StringComparison.Ordinal) is < 0 or > 15)
            return false;

        foreach (var c in value)
        {
            if (Alphabet.IndexOf(c, StringComparison.Ordinal) < 0)
                return false;
        }

        return true;
    }

    /// <summary>When a TID was made, or null for one that is not a TID.</summary>
    public static DateTimeOffset? TimeOf(string? value)
    {
        if (!IsTid(value))
            return null;

        ulong number = 0;
        foreach (var c in value!)
            number = (number << 5) | (uint)Alphabet.IndexOf(c, StringComparison.Ordinal);

        var micros = (long)(number >> 10);
        return new DateTimeOffset(UnixEpochTicks + micros * 10, TimeSpan.Zero);
    }

    private static long MicrosOf(DateTimeOffset at) =>
        Math.Max(0, (at.UtcTicks - UnixEpochTicks) / 10);

    private static string Of(long micros, int clockId)
    {
        if (clockId is < 0 or > 1023)
            throw new ArgumentOutOfRangeException(nameof(clockId), "A clock id is 10 bits.");

        // 53 bits of time: enough until the year 2255.
        var number = (((ulong)micros & ((1UL << 53) - 1)) << 10) | (uint)clockId;

        Span<char> text = stackalloc char[Length];
        for (var i = Length - 1; i >= 0; i--)
        {
            text[i] = Alphabet[(int)(number & 31)];
            number >>= 5;
        }

        return new string(text);
    }
}
