using System.Globalization;
using System.Text;

namespace Modbot.Core.Google;

/// <summary>
/// The ids Modbot gives its events on Google Calendar (Google Calendar design §3.2): <c>mb</c>, the
/// event's Guid in base32hex (26 characters), and a turn number.
/// </summary>
/// <remarks>
/// <para>
/// Google takes an event id from the caller when it is lowercase <c>a</c>-<c>v</c> and <c>0</c>-<c>9</c>,
/// 5 to 1024 characters, and unique on the calendar. Modbot picks it and writes it down before the
/// first insert, so an insert whose answer was lost can be read back by its id rather than made
/// twice (§3.4).
/// </para>
/// <para>
/// The turn goes up each time the event has to be made again on Google: after it was taken down and
/// wanted again, or deleted on Google. A deleted Google event keeps its id as a cancelled entry, so a
/// new id is safer than bringing the old one back.
/// </para>
/// </remarks>
public static class GoogleEventIds
{
    private const string Prefix = "mb";

    /// <summary>The characters base32hex (RFC 4648 §7) writes, lowercase: Google's own alphabet for ids.</summary>
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuv";

    /// <summary>The length of the Guid part: 128 bits at five bits a character.</summary>
    private const int GuidLength = 26;

    /// <summary>The id of <paramref name="eventId"/>'s event on its <paramref name="turn"/>th making, from 0.</summary>
    public static string For(Guid eventId, int turn)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(turn);

        return Prefix + Base32Hex(eventId.ToByteArray(bigEndian: true)) + turn.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The turn an id was made on, or null for an id that is not one of Modbot's.</summary>
    public static int? TurnOf(string? id)
    {
        if (id is null || id.Length <= Prefix.Length + GuidLength || !id.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        return int.TryParse(id.AsSpan(Prefix.Length + GuidLength), NumberStyles.None, CultureInfo.InvariantCulture, out var turn)
            ? turn
            : null;
    }

    /// <summary>The id for the turn after <paramref name="id"/>'s, or turn 0 when there was none.</summary>
    public static string Next(Guid eventId, string? id) =>
        For(eventId, TurnOf(id) is { } turn ? turn + 1 : 0);

    private static string Base32Hex(byte[] bytes)
    {
        var text = new StringBuilder(GuidLength);
        var buffer = 0;
        var bits = 0;

        foreach (var b in bytes)
        {
            // Only the bits not yet written are kept: at most twelve.
            buffer = ((buffer << 8) | b) & 0xFFF;
            bits += 8;

            while (bits >= 5)
            {
                bits -= 5;
                text.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0)
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);

        return text.ToString();
    }
}
