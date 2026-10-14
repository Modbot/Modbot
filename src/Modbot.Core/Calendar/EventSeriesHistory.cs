using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;

namespace Modbot.Core.Calendar;

/// <summary>One date of an event: how many were interested when it started, and who came.</summary>
/// <param name="StartsAt">The date's start, as the opening names it.</param>
/// <param name="DiscordInterested">Interested on Discord when it started. Null when it was not read.</param>
/// <param name="VRChatInterested">Interested on VRChat when it started. Null when it was not read.</param>
/// <param name="Came">
/// The people seen arriving at the date's instance. Null when Modbot opened none or saw nobody: nobody
/// seen is not the same as nobody came, so it is not a zero.
/// </param>
/// <param name="MostAtOnce">The most people the group instance poll counted at once. Null when it never counted.</param>
/// <param name="DiscordLate">The Discord count was read more than <see cref="EventSeriesHistory.LateAfter"/> after the date started (Modbot was not running at the start).</param>
/// <param name="VRChatLate">The VRChat count was read more than <see cref="EventSeriesHistory.LateAfter"/> after the date started.</param>
public sealed record EventSeriesDate(
    DateTimeOffset StartsAt,
    int? DiscordInterested,
    int? VRChatInterested,
    int? Came,
    int? MostAtOnce,
    bool DiscordLate = false,
    bool VRChatLate = false);

/// <summary>
/// How each date of one event did: the interest counts taken when it started (<see cref="CalendarInterest"/>)
/// beside the people who came (<see cref="EventAttendance"/>). One event is one series: all the dates
/// of its repeat.
/// </summary>
/// <remarks>
/// Reads Modbot's own tables and nothing else. "Came" counts only people seen in an instance a
/// moderator's Modbot app stood in, as the recap does.
/// </remarks>
public sealed class EventSeriesHistory
{
    /// <summary>The most dates one answer carries: the latest ones.</summary>
    public const int MostDates = 100;

    /// <summary>How long after the start a count may be read and still count as taken at the start.</summary>
    public static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(10);

    private readonly ModbotContext _db;
    private readonly EventAttendance _attendance;

    public EventSeriesHistory(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
        _attendance = new EventAttendance(db);
    }

    /// <summary>
    /// The dates of an event that have a count or an opened instance, earliest first, at most the
    /// latest <paramref name="limit"/> of them.
    /// </summary>
    /// <param name="from">Only dates starting at or after this time; null for all.</param>
    /// <param name="to">Only dates starting before this time; null for all.</param>
    public async Task<IReadOnlyList<EventSeriesDate>> ForEventAsync(
        Guid eventId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = MostDates,
        CancellationToken ct = default)
    {
        var interests = await _db.CalendarInterests.AsNoTracking()
            .Where(i => i.EventId == eventId
                && (from == null || i.OccurrenceStartsAt >= from)
                && (to == null || i.OccurrenceStartsAt < to))
            .ToListAsync(ct).ConfigureAwait(false);

        var openings = await _db.CalendarOpenings.AsNoTracking()
            .Where(o => o.EventId == eventId
                && o.InstanceId != null
                && (from == null || o.OccurrenceStartsAt >= from)
                && (to == null || o.OccurrenceStartsAt < to))
            .ToListAsync(ct).ConfigureAwait(false);

        var dates = interests.Select(i => i.OccurrenceStartsAt)
            .Concat(openings.Select(o => o.OccurrenceStartsAt))
            .Distinct()
            .OrderByDescending(d => d)
            .Take(limit)
            .ToHashSet();

        var turnouts = (await _attendance.ForOpeningsAsync(
                [.. openings.Where(o => dates.Contains(o.OccurrenceStartsAt))], ct).ConfigureAwait(false))
            .ToDictionary(t => t.OccurrenceStartsAt);

        var counts = interests.ToDictionary(i => i.OccurrenceStartsAt);

        return [.. dates.OrderBy(d => d).Select(date =>
        {
            counts.TryGetValue(date, out var interest);
            turnouts.TryGetValue(date, out var turnout);

            return new EventSeriesDate(
                date,
                interest?.DiscordInterested,
                interest?.VRChatInterested,
                turnout is { People.Count: > 0 } ? turnout.People.Count : null,
                turnout?.PeakHeadCount,
                interest is { DiscordInterested: not null, DiscordTakenAt: { } discordAt } && discordAt - date > LateAfter,
                interest is { VRChatInterested: not null, VRChatTakenAt: { } vrchatAt } && vrchatAt - date > LateAfter);
        })];
    }

    /// <summary>The average of the counts that are there, to one decimal place; null when none is.</summary>
    public static decimal? Average(IEnumerable<int?> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        var known = counts.Where(c => c is not null).Select(c => c!.Value).ToList();
        return known.Count == 0 ? null : Math.Round((decimal)known.Sum() / known.Count, 1, MidpointRounding.AwayFromZero);
    }
}
