using System.Globalization;

namespace Modbot.Companion.LogReading;

/// <summary>
/// Splits one line of VRChat's output log into timestamp, level, tag and message.
/// </summary>
/// <remarks>
/// <para><strong>What this reads, and what it does not.</strong> This works on text that VRChat
/// itself wrote to its own diagnostic log — the same file you can open in Notepad right now. It
/// does nothing but cut a line into four pieces. It opens no files of its own, contacts nothing,
/// and remembers nothing.</para>
/// <para><strong>Nothing here leaves the machine.</strong> The raw line — the <c>Message</c> in
/// particular — is never transmitted and never stored by Modbot. Only a handful of fields parsed
/// out of a handful of recognised line shapes are ever sent, and which ones is visible in
/// <c>ClientEvent</c>. Foundation spec section 3.2.</para>
/// </remarks>
public static class VRChatLogLineParser
{
    /// <summary>
    /// <c>2026.09.03 20:27:14</c> — local time, and VRChat records no timezone or offset at all.
    /// That is a real hazard rather than a detail: two moderators in different timezones write
    /// identical-looking timestamps for different instants, and a DST transition shifts them by an
    /// hour mid-session. The conversion to a real instant happens once, deliberately, in
    /// <c>LogTimestampConverter</c>, never here.
    /// </summary>
    private const string TimestampFormat = "yyyy.MM.dd HH:mm:ss";

    private const int TimestampLength = 19;

    /// <summary>
    /// Attempts to read one line. Returns <c>false</c> for anything that is not the start of a
    /// record — blank lines, and the continuation lines of records that wrap (the startup
    /// <c>[SteamManager]</c> block does). A wrapped line is not corruption and must not be treated
    /// as a parser break; research doc section 5.3.
    /// </summary>
    public static bool TryParse(string? raw, out VRChatLogLine line)
    {
        line = default;

        if (raw is null || raw.Length < TimestampLength)
            return false;

        if (!DateTime.TryParseExact(
                raw.AsSpan(0, TimestampLength),
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp))
        {
            return false;
        }

        var rest = raw.AsSpan(TimestampLength).TrimStart();
        if (rest.IsEmpty)
            return false;

        // Level is a single word, space-padded out to a fixed column.
        var levelEnd = rest.IndexOf(' ');
        if (levelEnd < 0)
            return false;

        var level = rest[..levelEnd].ToString();
        var afterLevel = rest[levelEnd..].TrimStart();

        // The envelope separator is a lone hyphen followed by two spaces. Trimming the whole run
        // of spaces would eat leading whitespace that belongs to the message.
        if (afterLevel.IsEmpty || afterLevel[0] != '-')
            return false;

        afterLevel = afterLevel[1..];
        for (var stripped = 0; stripped < 2 && !afterLevel.IsEmpty && afterLevel[0] == ' '; stripped++)
            afterLevel = afterLevel[1..];

        var (tag, message) = SplitTag(afterLevel);
        line = new VRChatLogLine(timestamp, level, tag, message);
        return true;
    }

    /// <summary>
    /// Pulls a leading <c>[Tag]</c> off the message. Only a tag at the very start counts: plenty of
    /// untagged lines contain brackets further along (<c>uSpeak [2][-156…]</c>).
    /// </summary>
    private static (string? Tag, string Message) SplitTag(ReadOnlySpan<char> body)
    {
        if (body.IsEmpty || body[0] != '[')
            return (null, body.ToString());

        var close = body.IndexOf(']');
        if (close < 0)
            return (null, body.ToString());

        var tag = body[1..close].ToString();
        var message = body[(close + 1)..];
        if (!message.IsEmpty && message[0] == ' ')
            message = message[1..];

        return (tag, message.ToString());
    }
}
