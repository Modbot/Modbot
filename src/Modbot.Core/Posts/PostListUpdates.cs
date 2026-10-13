using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using NodaTime;
using NodaTime.Text;

namespace Modbot.Core.Posts;

/// <summary>One event list in a post's text, and what it says now: both with their marks, so one can replace the other.</summary>
public sealed record PostListChange(string Old, string New);

/// <summary>
/// Keep updated (posts design §8): finds what the event lists in a post would say now, and writes
/// that into the words a site already has, leaving everything around the lists alone.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Which lists.</strong> <see cref="PostEventText.ListsIn"/> finds each list and the id or
/// range its mark holds. A What's on list is built again over its range, until its last day has
/// passed. An Add event list is built again for the event's next date, until the date it shows has
/// started; an event that was cancelled, deleted or taken back to a draft says
/// <see cref="PostEventText.Nothing"/>.
/// </para>
/// <para>
/// <strong>Which words.</strong> A site is not sent the marks, so its text cannot be searched for
/// them. The list as that site was sent it, <see cref="PostEventText.ForSite"/> of the old list, is
/// found in the text it has and swapped for the list as it reads now. A person who changed the rest
/// of the text on the site, or the list itself, loses nothing; a list changed by hand is left as it
/// is, because it cannot be found.
/// </para>
/// </remarks>
public static partial class PostListUpdates
{
    private static readonly string[] Published = [CalendarEventStates.Scheduled, CalendarEventStates.Open, CalendarEventStates.Finished];

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}) to (\d{4}-\d{2}-\d{2}) (\S+)$", RegexOptions.CultureInvariant)]
    private static partial Regex WhatsOnArgument();

    [GeneratedRegex(@"\{\{time (\d{1,12}) ", RegexOptions.CultureInvariant)]
    private static partial Regex FirstTime();

    /// <summary>The lists in <paramref name="text"/> that read differently now, as they stood and as they read now.</summary>
    public static async Task<IReadOnlyList<PostListChange>> ChangesAsync(
        ModbotContext db, string? text, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var changes = new List<PostListChange>();

        foreach (var list in PostEventText.ListsIn(text))
        {
            var old = text![list.Start..(list.Start + list.Length)];
            var rebuilt = list.Kind == PostEventText.EventWord
                ? await EventListAsync(db, list.Argument, old, now, ct).ConfigureAwait(false)
                : await WhatsOnAsync(db, list.Argument, now, ct).ConfigureAwait(false);

            if (rebuilt is not null && !string.Equals(rebuilt, old, StringComparison.Ordinal)
                && !changes.Any(c => c.Old == old))
            {
                changes.Add(new PostListChange(old, rebuilt));
            }
        }

        return changes;
    }

    /// <summary>
    /// <paramref name="text"/> as one site was sent it, with each changed list swapped for what it
    /// reads now. The text itself when none of the old lists is in it.
    /// </summary>
    public static string ForSite(string network, string text, IReadOnlyList<PostListChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        foreach (var change in changes)
        {
            var was = PostEventText.ForSite(network, change.Old);

            if (was.Length > 0 && text.Contains(was, StringComparison.Ordinal))
                text = text.Replace(was, PostEventText.ForSite(network, change.New), StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>The post's own text with each changed list swapped, marks and all.</summary>
    public static string Swap(string text, IReadOnlyList<PostListChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        foreach (var change in changes)
            text = text.Replace(change.Old, change.New, StringComparison.Ordinal);

        return text;
    }

    /// <summary>
    /// A site's own text with each changed list swapped. Own text is kept with its marks, so the old
    /// list is found as it stands; a text saved before that holds the finished words instead, and is
    /// searched for those as <see cref="ForSite"/> does.
    /// </summary>
    public static string ForOwnText(string network, string text, IReadOnlyList<PostListChange> changes)
    {
        var swapped = Swap(text, changes);

        return string.Equals(swapped, text, StringComparison.Ordinal) ? ForSite(network, text, changes) : swapped;
    }

    // Null: leave the list as it is (its argument is unreadable, or its date has started).
    private static async Task<string?> EventListAsync(
        ModbotContext db, string argument, string old, DateTimeOffset now, CancellationToken ct)
    {
        if (!Guid.TryParse(argument, out var id))
            return null;

        // The date the list shows has started: it has done its job.
        if (FirstTime().Match(old) is { Success: true } shown
            && long.TryParse(shown.Groups[1].Value, CultureInfo.InvariantCulture, out var seconds)
            && DateTimeOffset.FromUnixTimeSeconds(seconds) <= now)
        {
            return null;
        }

        var calendarEvent = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.DeletedAt, e.State })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (calendarEvent is null || calendarEvent.DeletedAt is not null || !Published.Contains(calendarEvent.State))
            return PostEventText.EmptyEventList(id);

        var line = await PostEventLists.NextOfAsync(db, id, now, ct).ConfigureAwait(false);

        return line is null ? null : PostEventText.EventList(line);
    }

    private static async Task<string?> WhatsOnAsync(ModbotContext db, string argument, DateTimeOffset now, CancellationToken ct)
    {
        var parts = WhatsOnArgument().Match(argument);
        if (!parts.Success)
            return null;

        var from = LocalDatePattern.Iso.Parse(parts.Groups[1].Value);
        var to = LocalDatePattern.Iso.Parse(parts.Groups[2].Value);
        var zone = CalendarRepeat.FindZone(parts.Groups[3].Value);

        if (!from.Success || !to.Success || zone is null)
            return null;

        var (start, end) = PostEventLists.Range(from.Value, to.Value, zone);

        // Its last day has passed: nothing in it can change any more.
        if (end <= now)
            return null;

        var lines = await PostEventLists.InRangeAsync(db, start, end, ct).ConfigureAwait(false);

        return PostEventText.WhatsOnList(from.Value, to.Value, zone.Id, lines);
    }
}
