using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using NodaTime;

namespace Modbot.Core.Posts;

/// <summary>
/// The calendar events a post can list (posts design §5.3): published, and not cancelled.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Published</strong> is the calendar's own word for an event that is out of draft:
/// scheduled, open, or finished. A draft is left out, and so is a cancelled event, and a date of a
/// repeating event that was cancelled on its own (<see cref="CalendarRepeat.Between"/> already
/// skips it).
/// </para>
/// <para>
/// <strong>One link per date.</strong> The event's Discord link when Discord has made the event for
/// that very date (a repeating event has one Discord event at a time), otherwise Modbot's own join
/// link, which only an event anyone can join has. VRChat's calendar has no address Modbot can build
/// for one event, so it is not offered. An event with neither has no link.
/// </para>
/// </remarks>
public static class PostEventLists
{
    /// <summary>The most events "Add event" offers.</summary>
    public const int MaxChoices = 100;

    private static readonly string[] Published = [CalendarEventStates.Scheduled, CalendarEventStates.Open, CalendarEventStates.Finished];

    /// <summary>The first and last moment of the days <paramref name="from"/> to <paramref name="to"/>, both counted, in a zone.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Range(LocalDate from, LocalDate to, DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return (
            from.AtStartOfDayInZone(zone).ToDateTimeOffset(),
            to.PlusDays(1).AtStartOfDayInZone(zone).ToDateTimeOffset());
    }

    /// <summary>Every date that starts from <paramref name="start"/> up to but not including <paramref name="end"/>, earliest first.</summary>
    public static Task<List<PostEventLine>> InRangeAsync(ModbotContext db, DateTimeOffset start, DateTimeOffset end, CancellationToken ct) =>
        LinesAsync(db, null, e => CalendarRepeat.Between(e, start, end).Where(o => o.StartsAt >= start && o.StartsAt < end), ct);

    /// <summary>The next date of every event that has one, soonest first: what "Add event" offers.</summary>
    public static async Task<List<PostEventLine>> UpcomingAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        var lines = await LinesAsync(db, null, e => CalendarRepeat.Between(e, now).Take(1), ct);
        return [.. lines.Take(MaxChoices)];
    }

    /// <summary>The next date of one event, or null when it is not published, is cancelled, or has no date left.</summary>
    public static async Task<PostEventLine?> NextOfAsync(ModbotContext db, Guid eventId, DateTimeOffset now, CancellationToken ct) =>
        (await LinesAsync(db, eventId, e => CalendarRepeat.Between(e, now).Take(1), ct)).FirstOrDefault();

    private static async Task<List<PostEventLine>> LinesAsync(
        ModbotContext db, Guid? only, Func<CalendarEvent, IEnumerable<CalendarOccurrence>> dates, CancellationToken ct)
    {
        var query = db.CalendarEvents.AsNoTracking().Where(e => e.DeletedAt == null && Published.Contains(e.State));

        if (only is { } id)
            query = query.Where(e => e.Id == id);

        var events = await query.ToListAsync(ct);

        var made = events.Select(e => (Event: e, Dates: dates(e).ToList())).Where(x => x.Dates.Count > 0).ToList();

        if (made.Count == 0)
            return [];

        var ids = made.Select(x => x.Event.Id).ToList();

        var discordEvents = await db.CalendarEventPlaces.AsNoTracking()
            .Where(p => ids.Contains(p.EventId)
                && p.Place == CalendarPlaces.DiscordEvent
                && p.State == CalendarPlaceStates.Published
                && p.ExternalId != null)
            .ToListAsync(ct);

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var guildId = settings?.DiscordGuildId?.Trim();

        return [.. made
            .SelectMany(x => x.Dates.Select(date =>
            {
                var discord = guildId is { Length: > 0 }
                    ? discordEvents.FirstOrDefault(p => p.EventId == x.Event.Id && p.OccurrenceStartsAt == date.PlannedStartsAt)
                    : null;

                var link = discord?.ExternalId is { Length: > 0 } discordId
                    ? $"https://discord.com/events/{Uri.EscapeDataString(guildId!)}/{Uri.EscapeDataString(discordId)}"
                    : CalendarFeedWriter.JoinLink(settings?.PublicAddress, x.Event);

                return new PostEventLine(x.Event.Id, CalendarRepeat.TitleOf(x.Event, date), date.StartsAt, x.Event.TimeZone, link);
            }))
            .OrderBy(l => l.StartsAt)
            .ThenBy(l => l.Title, StringComparer.Ordinal)];
    }
}
