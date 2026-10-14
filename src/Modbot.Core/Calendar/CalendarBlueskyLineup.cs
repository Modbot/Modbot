using System.Globalization;
using NodaTime;
using Modbot.Core.Bluesky;
using Modbot.Core.Data.Entities;
using Modbot.Core.Google;
using Modbot.Core.Posts;

namespace Modbot.Core.Calendar;

/// <summary>One date in the week's line-up: when it starts and what it is called.</summary>
public sealed record LineupDate(DateTimeOffset StartsAt, string Title);

/// <summary>
/// The week's line-up post on Bluesky (Settings → Bluesky, Weekly line-up): which dates go in it and
/// what it says. One place, used by the pass that makes the post and by its tests.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Which dates.</strong> Every date of the next seven days of an event that everyone can see
/// (<see cref="IsEveryones"/>: visible to Everyone, no role needed), whatever places it goes to and
/// whether or not its Bluesky switch is on. Members-only events never go in. A
/// cancelled date, a cancelled event and a deleted one are left out, and a date moved on its own is
/// where it was moved to (<see cref="CalendarRepeat.Between"/>).
/// </para>
/// <para>
/// <strong>What it says.</strong> A first line, one line per date (<c>Mon 20:00 Karaoke</c>, the time
/// in the line-up's zone on a 24-hour clock), and the calendar page's address last when the group has
/// a public one, which gives the post its link card. Bluesky stops at 300 graphemes
/// (<see cref="BlueskyText.Fits"/>): as many dates as fit are listed, then <c>+N more</c>. A title
/// is cut only when not even the first date fits with it whole.
/// </para>
/// </remarks>
public static class CalendarBlueskyLineup
{
    /// <summary>How far ahead the line-up looks.</summary>
    public static readonly TimeSpan Ahead = TimeSpan.FromDays(7);

    /// <summary>The post's first line when the group has no name Modbot knows.</summary>
    public const string HeadingWithoutName = "This week's events";

    /// <summary>
    /// Whether everyone can see the event: its visibility is Everyone and no role is needed. Unlike
    /// <see cref="CalendarFeedWriter.IsPublic"/> it does not ask for VRChat's calendar, so a Discord-only
    /// event counts. The form hides <em>Visible to</em> unless VRChat or Google is on, so an event never
    /// shown it holds the form's own default, <c>group</c>, and stays out.
    /// </summary>
    public static bool IsEveryones(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return calendarEvent.Visibility == "public" && calendarEvent.VRChatRoleIds is not { Count: > 0 };
    }

    /// <summary>The dates of the line-up from <paramref name="events"/>: starting at or after <paramref name="from"/> and within <see cref="Ahead"/>, earliest first.</summary>
    /// <param name="events">Events the caller loaded; any that are not live or not public are dropped here too.</param>
    public static IReadOnlyList<LineupDate> DatesOf(IEnumerable<CalendarEvent> events, DateTimeOffset from)
    {
        ArgumentNullException.ThrowIfNull(events);

        var to = from + Ahead;
        var dates = new List<LineupDate>();

        foreach (var calendarEvent in events)
        {
            if (calendarEvent.DeletedAt is not null
                || !CalendarEventStates.IsLive(calendarEvent.State)
                || !IsEveryones(calendarEvent))
            {
                continue;
            }

            foreach (var occurrence in CalendarRepeat.Between(calendarEvent, from, to))
            {
                // Between also gives a date still going on at the start; the line-up lists what is coming.
                if (occurrence.StartsAt < from || occurrence.StartsAt >= to)
                    continue;

                var title = PostTexts.TidyTitle(CalendarRepeat.TitleOf(calendarEvent, occurrence, CalendarSite.Bluesky))
                    ?? PostTexts.TidyTitle(calendarEvent.Title)
                    ?? string.Empty;

                dates.Add(new LineupDate(occurrence.StartsAt, title));
            }
        }

        return [.. dates.OrderBy(d => d.StartsAt).ThenBy(d => d.Title, StringComparer.Ordinal)];
    }

    /// <summary>The post's first line.</summary>
    public static string Heading(string? groupName) =>
        PostTexts.TidyTitle(groupName) is { } name ? "This week at " + name : HeadingWithoutName;

    /// <summary>One date's line: <c>Mon 20:00 Karaoke</c>, the time in <paramref name="zone"/>.</summary>
    public static string LineOf(LineupDate date, DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(date);
        ArgumentNullException.ThrowIfNull(zone);

        var local = Instant.FromDateTimeOffset(date.StartsAt).InZone(zone).LocalDateTime;
        var when = local.ToString("ddd HH:mm", CultureInfo.InvariantCulture);

        return date.Title.Length == 0 ? when : when + " " + date.Title;
    }

    /// <summary>
    /// The post's text for <paramref name="dates"/> (earliest first, at least one). The heading, as
    /// many lines as fit, <c>+N more</c> for the rest, and <paramref name="link"/> last.
    /// </summary>
    public static string TextOf(string? groupName, IReadOnlyList<LineupDate> dates, DateTimeZone zone, string? link)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(zone);

        var heading = Heading(groupName);
        var lines = dates.Select(d => LineOf(d, zone)).ToList();

        for (var shown = lines.Count; shown >= 1; shown--)
        {
            var text = Compose(heading, lines.Take(shown), lines.Count - shown, link);
            if (BlueskyText.Fits(text))
                return text;
        }

        // Not even the first date fits whole: its title gives way, a grapheme at a time.
        var first = dates[0];
        var info = new StringInfo(first.Title);

        for (var keep = info.LengthInTextElements - 1; keep >= 0; keep--)
        {
            var cut = keep == 0 ? string.Empty : info.SubstringByTextElements(0, keep).TrimEnd() + "…";
            var text = Compose(heading, [LineOf(first with { Title = cut }, zone)], lines.Count - 1, link);

            if (BlueskyText.Fits(text))
                return text;
        }

        return Compose(heading, [LineOf(first with { Title = string.Empty }, zone)], lines.Count - 1, link);
    }

    /// <summary>
    /// The address of the group's public calendar page, or null when it has none: the Google calendar
    /// is set up and Check found it public.
    /// </summary>
    public static string? PageLinkOf(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return GoogleCalendarIds.IsUsable(settings.GoogleCalendarId) && settings.GooglePublic == GooglePublic.All
            ? GoogleCalendarIds.PublicPageLink(settings.GoogleCalendarId!, settings.GoogleCalendarTimeZone)
            : null;
    }

    private static string Compose(string heading, IEnumerable<string> lines, int more, string? link)
    {
        var all = new List<string> { heading };
        all.AddRange(lines);

        if (more > 0)
            all.Add("+" + more.ToString(CultureInfo.InvariantCulture) + " more");

        if (link is not null)
            all.Add(link);

        return string.Join('\n', all);
    }
}
