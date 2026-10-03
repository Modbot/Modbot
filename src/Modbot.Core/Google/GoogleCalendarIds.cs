using System.Text;

namespace Modbot.Core.Google;

/// <summary>
/// Calendar ids as people paste them, and the links Google gives a public calendar (Google Calendar
/// design §3.1, §3.9).
/// </summary>
public static class GoogleCalendarIds
{
    /// <summary>The longest calendar id kept.</summary>
    public const int MaxLength = 1024;

    /// <summary>
    /// The calendar id in what somebody pasted: the id itself, or the calendar's public address,
    /// embed link, iCal address or "add this calendar" link, from which the id is taken. Null when
    /// nothing usable is in it.
    /// </summary>
    /// <remarks>
    /// Only the shape of a link is read, and only links on <c>calendar.google.com</c> or
    /// <c>www.google.com</c> are taken apart; anything else is kept as the id it was typed as.
    /// The id's own format is not checked: Google decides what is a calendar, and Check asks it.
    /// </remarks>
    public static string? Parse(string? input)
    {
        var text = input?.Trim();

        if (string.IsNullOrEmpty(text))
            return null;

        if (Uri.TryCreate(text, UriKind.Absolute, out var link)
            && link.Scheme is "https" or "http" or "webcal"
            && link.Host is "calendar.google.com" or "www.google.com")
        {
            return Usable(FromLink(link));
        }

        return Usable(text);
    }

    /// <summary>
    /// Whether an id may go into a request path: not empty, no longer than <see cref="MaxLength"/>,
    /// no spaces or control characters, and not only dots (which a path would read as "here" or
    /// "up one").
    /// </summary>
    public static bool IsUsable(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength)
            return false;

        if (id.Trim('.').Length == 0)
            return false;

        foreach (var c in id)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return false;
        }

        return true;
    }

    /// <summary>Google's "add this calendar" link, for Google Calendar on a computer.</summary>
    public static string SubscribeLink(string calendarId) =>
        "https://calendar.google.com/calendar/r?cid=" + Uri.EscapeDataString(calendarId);

    /// <summary>The calendar's public page, in its own time zone when it has one.</summary>
    public static string PublicPageLink(string calendarId, string? timeZone) =>
        "https://calendar.google.com/calendar/embed?src=" + Uri.EscapeDataString(calendarId)
        + (string.IsNullOrWhiteSpace(timeZone) ? string.Empty : "&ctz=" + Uri.EscapeDataString(timeZone));

    /// <summary>The calendar's public address in iCal format. It works only while the calendar is public.</summary>
    public static string ICalLink(string calendarId) =>
        "https://calendar.google.com/calendar/ical/" + Uri.EscapeDataString(calendarId) + "/public/basic.ics";

    private static string? Usable(string? id)
    {
        var trimmed = id?.Trim();
        return IsUsable(trimmed) ? trimmed : null;
    }

    private static string? FromLink(Uri link)
    {
        // …/calendar/ical/{id}/public/basic.ics, and the same with /private-…/
        var segments = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var ical = Array.IndexOf(segments, "ical");

        if (ical >= 0 && ical + 1 < segments.Length)
            return Uri.UnescapeDataString(segments[ical + 1]);

        // …/calendar/embed?src={id}, …/calendar/r?cid={id}, …/calendar/u/0?cid={base64 id}
        var query = Query(link.Query);

        if (query.TryGetValue("src", out var src))
            return src;

        if (query.TryGetValue("cid", out var cid))
            return FromCid(cid);

        return null;
    }

    /// <summary>
    /// A <c>cid</c> is the id itself, or, in Google's own "shareable link", the id in base64.
    /// </summary>
    private static string FromCid(string cid)
    {
        if (cid.Contains('@', StringComparison.Ordinal))
            return cid;

        try
        {
            // A '+' in standard base64 arrives as a space once the query is read.
            var padded = cid.Replace(' ', '+').Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');

            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return decoded.Contains('@', StringComparison.Ordinal) ? decoded : cid;
        }
        catch (FormatException)
        {
            return cid;
        }
    }

    private static Dictionary<string, string> Query(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0)
                continue;

            var name = part[..equals];
            var value = Uri.UnescapeDataString(part[(equals + 1)..].Replace('+', ' '));

            values.TryAdd(name, value);
        }

        return values;
    }
}
