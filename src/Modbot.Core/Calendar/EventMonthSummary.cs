using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;

namespace Modbot.Core.Calendar;

/// <summary>How one event did over one month.</summary>
/// <param name="Dates">How many of its dates have a count or an opened instance in the month.</param>
/// <param name="DiscordInterested">The average Discord count over the dates that have one; null when none does.</param>
/// <param name="VRChatInterested">The average VRChat count over the dates that have one; null when none does.</param>
/// <param name="Came">The average number of people seen over the dates that have one; null when none does.</param>
public sealed record EventMonthLine(
    Guid EventId,
    string Title,
    int Dates,
    decimal? DiscordInterested,
    decimal? VRChatInterested,
    decimal? Came);

/// <summary>
/// How every event did over one UTC month, for the monthly post: each event with a date that month,
/// most dates first, then by title.
/// </summary>
public sealed class EventMonthSummary
{
    private readonly ModbotContext _db;
    private readonly EventSeriesHistory _history;

    public EventMonthSummary(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
        _history = new EventSeriesHistory(db);
    }

    /// <summary>The first moment of a month, UTC.</summary>
    public static DateTimeOffset Start(DateOnly month) =>
        new(month.Year, month.Month, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The first day of the month a moment falls in, UTC.</summary>
    public static DateOnly MonthOf(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        return new DateOnly(utc.Year, utc.Month, 1);
    }

    public async Task<IReadOnlyList<EventMonthLine>> ForMonthAsync(DateOnly month, CancellationToken ct = default)
    {
        var from = Start(month);
        var to = Start(month.AddMonths(1));

        var withCounts = _db.CalendarInterests.AsNoTracking()
            .Where(i => i.OccurrenceStartsAt >= from && i.OccurrenceStartsAt < to)
            .Select(i => i.EventId);

        var withInstances = _db.CalendarOpenings.AsNoTracking()
            .Where(o => o.InstanceId != null && o.OccurrenceStartsAt >= from && o.OccurrenceStartsAt < to)
            .Select(o => o.EventId);

        var ids = await withCounts.Union(withInstances).ToListAsync(ct).ConfigureAwait(false);

        var events = await _db.CalendarEvents.AsNoTracking()
            .Where(e => ids.Contains(e.Id) && e.DeletedAt == null)
            .Select(e => new { e.Id, e.Title })
            .ToListAsync(ct).ConfigureAwait(false);

        var lines = new List<EventMonthLine>();

        foreach (var calendarEvent in events)
        {
            var dates = await _history.ForEventAsync(calendarEvent.Id, from, to, int.MaxValue, ct).ConfigureAwait(false);

            if (dates.Count == 0)
                continue;

            lines.Add(new EventMonthLine(
                calendarEvent.Id,
                calendarEvent.Title,
                dates.Count,
                EventSeriesHistory.Average(dates.Select(d => d.DiscordInterested)),
                EventSeriesHistory.Average(dates.Select(d => d.VRChatInterested)),
                EventSeriesHistory.Average(dates.Select(d => d.Came))));
        }

        return [.. lines.OrderByDescending(l => l.Dates).ThenBy(l => l.Title, StringComparer.OrdinalIgnoreCase)];
    }
}
