using System.Text;
using System.Text.RegularExpressions;

namespace Modbot.Core.Calendar;

/// <summary>
/// Turns the text typed as an event's own Google description into the HTML Google reads, with
/// working links.
/// </summary>
/// <remarks>
/// <para>
/// Google reads a description as HTML, so everything typed is escaped and the only markup in the
/// result is the <c>&lt;a&gt;</c> tags made here. Two things become links, and only to
/// <c>http</c> and <c>https</c> addresses: <c>[words](https://address)</c>, shown as the words, and a
/// bare address, shown as itself. A user cannot write any other tag, attribute or address kind.
/// </para>
/// <para>
/// Only the Google description that was typed for Google gets this; the shared description is still
/// sent as escaped text (Google Calendar design §3.3).
/// </para>
/// </remarks>
public static partial class CalendarGoogleLinks
{
    /// <summary>The HTML for <paramref name="text"/>: escaped, with its links made.</summary>
    public static string Html(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var html = new StringBuilder(text.Length + 32);
        var from = 0;

        foreach (Match match in LinkPattern().Matches(text))
        {
            string address;
            string shown;
            var after = string.Empty;

            if (match.Groups["address"].Success)
            {
                address = match.Groups["address"].Value;
                shown = match.Groups["words"].Value;
            }
            else
            {
                // A bare address does not take the punctuation that ended the sentence with it.
                address = match.Groups["bare"].Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'');
                after = match.Groups["bare"].Value[address.Length..];
                shown = address;
            }

            if (!IsWebAddress(address))
                continue;

            html.Append(CalendarGoogleBody.Escape(text[from..match.Index]));
            html.Append("<a href=\"").Append(Attribute(address)).Append("\">").Append(CalendarGoogleBody.Escape(shown)).Append("</a>");
            html.Append(CalendarGoogleBody.Escape(after));
            from = match.Index + match.Length;
        }

        html.Append(CalendarGoogleBody.Escape(text[from..]));
        return html.ToString();
    }

    /// <summary>
    /// A description as Google shows it: the words of its links without the tags, and the escapes
    /// read back. For the preview, which draws what Google is sent.
    /// </summary>
    public static string Shown(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return System.Net.WebUtility.HtmlDecode(AnchorTag().Replace(html, string.Empty));
    }

    private static bool IsWebAddress(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);

    /// <summary>An address as an attribute's value: the characters that would end it or start markup are escaped.</summary>
    private static string Attribute(string address) =>
        CalendarGoogleBody.Escape(address).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);

    [GeneratedRegex("""\[(?<words>[^\]\r\n]{1,200})\]\((?<address>https?://[^\s)<>"]+)\)|(?<bare>https?://[^\s<>"]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LinkPattern();

    [GeneratedRegex("""<a href="[^"]*">|</a>""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AnchorTag();
}
