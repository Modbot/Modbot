using System.Globalization;
using Modbot.Core.Bluesky;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;

namespace Modbot.Core.Calendar;

/// <summary>
/// What an event's Bluesky post says for one date, and when it goes (calendar design, Bluesky step).
/// One place, used by the pass that makes the posts and by the form's Preview, so the preview shows
/// the words that go out.
/// </summary>
/// <remarks>
/// <para>
/// A post is the title, the time of the date in the event's own zone, the world's name when the
/// event has a world, the description and one link. The description gives way first: it is cut, at a
/// whole grapheme and with an ellipsis, so the post fits Bluesky's 300 graphemes. Title, time, world
/// and link are never cut. The link is Google Calendar's page for the event, else the VRChat group's
/// page; the Discord event is left out because its address is the current date's, not this one's
/// (<see cref="CalendarAutoLinks.For"/>). The event's cover goes with the post as a full picture whether
/// or not the post has a link, and the link stays clickable in the text.
/// </para>
/// <para>
/// An event's own Bluesky title and description (<see cref="CalendarEvent.BlueskyTitle"/>,
/// <see cref="CalendarEvent.BlueskyDescription"/>) replace the shared ones in that place; a date's
/// own title or description replaces both, as on every site (<see cref="CalendarSiteText"/>).
/// </para>
/// </remarks>
public static class CalendarBlueskyText
{
    /// <summary>How long before a date starts its post may go, in minutes: 1 hour, 1 day, 3 days, 1 week.</summary>
    public static readonly IReadOnlyList<int> BeforeChoices = [60, 1440, 4320, 10080];

    /// <summary>The start: one day before.</summary>
    public const int DefaultBeforeMinutes = 1440;

    /// <summary>The words for an own description over Bluesky's limit.</summary>
    public const string DescriptionTooLong = "The Bluesky description is longer than 300 characters.";

    /// <summary>The words for a post-before time that is not one of <see cref="BeforeChoices"/>.</summary>
    public const string BeforeNotAChoice = "Post it must be 1 hour, 1 day, 3 days or 1 week before.";

    /// <summary>The most pictures a Bluesky post carries (<c>app.bsky.embed.images</c>).</summary>
    public const int MostPictures = 4;

    /// <summary>The longest alt text of one picture, in characters: Bluesky's own limit.</summary>
    public const int AltMaxLength = 2000;

    /// <summary>The words for a post given more than <see cref="MostPictures"/> pictures.</summary>
    public const string TooManyPictures = "A Bluesky post has at most 4 pictures.";

    /// <summary>The words for an alt text over <see cref="AltMaxLength"/>.</summary>
    public const string AltTooLong = "A Bluesky picture's alt text is longer than 2000 characters.";

    public static bool IsChoice(int minutes) => BeforeChoices.Contains(minutes);

    /// <summary>When the post for <paramref name="occurrence"/> goes: its start, earlier by the event's choice.</summary>
    public static DateTimeOffset SendAt(CalendarEvent calendarEvent, CalendarOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return occurrence.StartsAt - TimeSpan.FromMinutes(calendarEvent.BlueskyPostBeforeMinutes);
    }

    /// <summary>
    /// The one link the post carries, or null when the event has neither a Google Calendar page nor a
    /// VRChat group page. Needs <see cref="CalendarEvent.AutoLinks"/> resolved.
    /// </summary>
    public static string? LinkOf(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var links = CalendarAutoLinks.For(calendarEvent, CalendarSite.Bluesky, ofADate: true);
        return links?.GoogleEvent ?? links?.VRChatGroupPage;
    }

    /// <summary>The post's title: the date's, else Bluesky's own, else the event's.</summary>
    public static string? TitleOf(CalendarEvent calendarEvent, CalendarOccurrence occurrence) =>
        PostTexts.TidyTitle(CalendarRepeat.TitleOf(calendarEvent, occurrence, CalendarSite.Bluesky));

    /// <summary>
    /// The whole text of the post for <paramref name="occurrence"/>.
    /// </summary>
    /// <param name="worldName">The world's name, or null when the event has none or Modbot has only its id.</param>
    /// <param name="tags">The tags the post ends with, most wanted first (<see cref="TagsOf"/>). Dropped from the end when they do not fit.</param>
    public static string For(CalendarEvent calendarEvent, CalendarOccurrence occurrence, string? worldName, IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var lines = new List<string>(4);

        if (TitleOf(calendarEvent, occurrence) is { } title)
            lines.Add(title);

        lines.Add(PostEventText.ForSite(PostNetworks.Bluesky, PostEventText.TimeMark(occurrence.StartsAt, calendarEvent.TimeZone)));

        if (PostTexts.TidyTitle(worldName) is { } world)
            lines.Add(world);

        var link = LinkOf(calendarEvent);
        var body = PostTexts.Tidy(CalendarRepeat.DescriptionOf(calendarEvent, occurrence, CalendarSite.Bluesky));

        string Join(string words)
        {
            var all = new List<string>(lines);
            if (words.Length > 0)
                all.Add(words);
            if (link is not null)
                all.Add(link);
            return string.Join('\n', all);
        }

        var text = Join(body);

        // The description gives way, a grapheme at a time, until the post fits.
        if (!BlueskyText.Fits(text) && body.Length > 0)
        {
            var info = new StringInfo(body);
            var keep = Math.Min(info.LengthInTextElements, BlueskyText.GraphemeLimit);

            while (keep > 0)
            {
                keep--;
                var cut = keep == 0 ? string.Empty : info.SubstringByTextElements(0, keep).TrimEnd() + "…";
                text = Join(cut);

                if (BlueskyText.Fits(text))
                    break;
            }
        }

        return tags is null ? text : BlueskyTags.Append(text, tags);
    }

    /// <summary>
    /// The tags an event's post ends with: its categories' tags in the order of the categories' names,
    /// then the ones set for every post.
    /// </summary>
    /// <param name="everyPost">Settings → Bluesky, "Tags on every post", as kept.</param>
    public static IReadOnlyList<string> TagsOf(CalendarEvent calendarEvent, string? everyPost)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return
        [
            .. calendarEvent.Categories
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => BlueskyTags.ForCategory(c.Name, c.BlueskyTag))
                .Where(t => t.Length > 0),
            .. BlueskyTags.ParseList(everyPost),
        ];
    }

    /// <summary>
    /// What the calendar wants the post to be, as one short string: compared with the one kept on a
    /// post that went out, it says whether the post still says what the event does.
    /// </summary>
    /// <param name="pictureId">The picture the post goes with, or its first one.</param>
    /// <param name="pictures">
    /// The post's own list of pictures, or null for the cover alone. Null gives the same fingerprint as
    /// before pictures had a list, so a post that went out is not taken down and made again for it.
    /// </param>
    public static string Fingerprint(string text, Guid? pictureId, IReadOnlyList<BlueskyPostPicture>? pictures = null) =>
        pictures is null
            ? CalendarFingerprint.Of(text, pictureId)
            : CalendarFingerprint.Of(text, pictureId, string.Join('\u001f', pictures.Select(p => $"{p.PictureId}\u001e{p.Alt}")));

    /// <summary>
    /// The pictures the post goes with when the event has a list of its own, each with the alt text it
    /// was given, or the post's title when it was given none. Null when the event goes with the cover alone.
    /// </summary>
    public static IReadOnlyList<BlueskyPostPicture>? PicturesOf(CalendarEvent calendarEvent, string? title)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return calendarEvent.BlueskyPictures is { } own
            ? [.. own.Take(MostPictures).Select(p => new BlueskyPostPicture(p.PictureId, string.IsNullOrWhiteSpace(p.Alt) ? title ?? string.Empty : p.Alt.Trim()))]
            : null;
    }
}
