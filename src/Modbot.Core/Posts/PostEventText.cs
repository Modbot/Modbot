using System.Globalization;
using System.Text.RegularExpressions;
using Modbot.Core.Data.Entities;
using NodaTime;
using NodaTime.Text;

namespace Modbot.Core.Posts;

/// <summary>One date of one calendar event, as a post lists it.</summary>
/// <param name="Link">The event's Discord link, else Modbot's join link; null when neither can be built.</param>
public sealed record PostEventLine(Guid EventId, string Title, DateTimeOffset StartsAt, string TimeZone, string? Link);

/// <summary>An event list found in a post's text: where it starts, how long it is, and what it was built from.</summary>
/// <param name="Kind"><see cref="PostEventText.EventWord"/> or <see cref="PostEventText.WhatsOnWord"/>.</param>
/// <param name="Argument">An event's id, or a What's on list's "from to zone".</param>
public sealed record PostEventListInText(string Kind, string Argument, int Start, int Length);

/// <summary>
/// The event lists a person inserts into a post, and how each site is sent them (posts design §5.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ordinary text in the post's own text.</strong> The list is written into the text box as
/// words the writer can change. Two kinds of mark in it are not shown on any site:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>{{time 1792350000 Sat 18 Oct, 20:00 BST}}</c> is a date. Discord is sent <c>&lt;t:1792350000:F&gt;</c>,
/// which every reader sees in their own timezone; VRChat and Bluesky are sent the words after the
/// number, the time in the event's own zone. One text, written once, reads right on all three.
/// </description></item>
/// <item><description>
/// <c>{{event id}}</c> ... <c>{{/event}}</c> and <c>{{whats-on 2026-10-01 to 2026-10-31 Europe/London}}</c>
/// ... <c>{{/whats-on}}</c> wrap a list and say what it was built from. No site shows them. A later
/// step finds the list with <see cref="ListsIn"/> and writes it again when an event changes, leaving
/// the words around it alone.
/// </description></item>
/// </list>
/// <para>
/// The senders, the preview and the counters all go through <see cref="ForSite"/>, so the preview is
/// what goes out. The web app has the same rule for the counters (<c>postRules.ts</c>, <c>eventTextFor</c>).
/// </para>
/// </remarks>
public static class PostEventText
{
    public const string EventWord = "event";
    public const string WhatsOnWord = "whats-on";

    /// <summary>What a list with no events in it says.</summary>
    public const string Nothing = "Nothing is planned yet.";

    private static readonly Regex Time = new(@"\{\{time (\d{1,12}) ([^}\n]*)\}\}", RegexOptions.CultureInvariant);

    // A mark on a line of its own goes with its line end; one inside a line (the writer joined lines) just goes.
    private static readonly Regex MarkOnItsOwnLine = new(@"^[ \t]*\{\{/?(?:event|whats-on)\b[^}\n]*\}\}[ \t]*(?:\n|$)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex Mark = new(@"\{\{/?(?:event|whats-on)\b[^}\n]*\}\}", RegexOptions.CultureInvariant);
    private static readonly Regex WholeList = new(@"\{\{(event|whats-on) ([^}\n]*)\}\}.*?\{\{/\1\}\}", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly ZonedDateTimePattern Words =
        ZonedDateTimePattern.CreateWithInvariantCulture("ddd d MMM, HH:mm x", DateTimeZoneProviders.Tzdb);

    /// <summary>The text one site is sent: dates written for it, and the marks that wrap a list left out.</summary>
    /// <param name="network">One of <see cref="PostNetworks"/>.</param>
    public static string ForSite(string network, string? text)
    {
        var tidy = PostTexts.Tidy(text);

        if (!tidy.Contains("{{", StringComparison.Ordinal))
            return tidy;

        tidy = MarkOnItsOwnLine.Replace(tidy, string.Empty);
        tidy = Mark.Replace(tidy, string.Empty);

        tidy = Time.Replace(tidy, m => network == PostNetworks.Discord
            ? $"<t:{m.Groups[1].Value}:F>"
            : m.Groups[2].Value.Trim());

        return PostTexts.Tidy(tidy);
    }

    /// <summary>A date as the post's text holds it: for Discord a timestamp, for the others the event's own time and zone.</summary>
    public static string TimeMark(DateTimeOffset startsAt, string timeZone)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone) ?? DateTimeZone.Utc;
        var words = Words.Format(Instant.FromDateTimeOffset(startsAt).InZone(zone));
        return $"{{{{time {startsAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)} {words}}}}}";
    }

    /// <summary>One event: its name and date, and a link when it has one.</summary>
    public static string Lines(PostEventLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var title = PostTexts.TidyTitle(line.Title) ?? string.Empty;
        var head = $"{title} - {TimeMark(line.StartsAt, line.TimeZone)}";
        return line.Link is { Length: > 0 } link ? $"{head}\n{link}" : head;
    }

    /// <summary>The text for "Add event": one event, wrapped so a later step can find it by its id.</summary>
    public static string EventList(PostEventLine line) =>
        $"{{{{{EventWord} {line.EventId:D}}}}}\n{Lines(line)}\n{{{{/{EventWord}}}}}";

    /// <summary>The text for "What's on": every event in the range, a blank line between, wrapped with the range it was built from.</summary>
    public static string WhatsOnList(LocalDate from, LocalDate to, string timeZone, IReadOnlyList<PostEventLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var pattern = LocalDatePattern.Iso;
        var body = lines.Count == 0 ? Nothing : string.Join("\n\n", lines.Select(Lines));
        return $"{{{{{WhatsOnWord} {pattern.Format(from)} to {pattern.Format(to)} {timeZone}}}}}\n{body}\n{{{{/{WhatsOnWord}}}}}";
    }

    /// <summary>The event lists in a post's text, earliest first, for a step that writes them again.</summary>
    public static IReadOnlyList<PostEventListInText> ListsIn(string? text) =>
        [.. WholeList.Matches(text ?? string.Empty).Select(m => new PostEventListInText(m.Groups[1].Value, m.Groups[2].Value, m.Index, m.Length))];
}
