using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Api.Features.Places;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// What each time an event ran actually did: the instance it ran in and how busy that got, who a
/// moderator's client saw there, and how many joined the group or asked to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The numbers are the ones other screens already show, read the same way.</strong> The
/// instance's most at once and how long it ran are its <see cref="InstanceRows"/> row, as the
/// instance popup has them; who was seen is <see cref="PresenceCounts"/> over the instance's own
/// stretch, as the popup counts it; new members and join requests count the same facts the daily
/// totals <c>members.joined</c> and <c>requests.received</c> do. A second way of counting any of
/// them would be a second answer that could disagree with the first.
/// </para>
/// <para>
/// <strong>Which instance.</strong> The one Modbot opened for that time, from
/// <c>calendar_opening</c>, when it did. Otherwise the group's instance in the event's world that
/// was open for most of the stretch from when the event opened until it ended: an event somebody
/// opened by hand, or one that does not open its own instance. Nothing is matched for an event with
/// no world, or for a group instance in another world.
/// </para>
/// <para>
/// <strong>Which times.</strong> Those the event's rule gives that have started, and every time
/// Modbot opened an instance for it, so a time that ran before the event was moved still counts at
/// the time it ran. A cancelled event's times stop at the cancel; a draft never ran.
/// </para>
/// <para>
/// New members and join requests are counted from when the event opened until
/// <see cref="After"/> past its end, because people often join a group the day after an event they
/// liked. Two times of a daily event share some of that stretch, and each counts it.
/// </para>
/// </remarks>
public sealed class CalendarResults(ModbotContext db)
{
    /// <summary>How long after an event ends a join still counts towards it.</summary>
    public static readonly TimeSpan After = TimeSpan.FromHours(24);

    /// <summary>How many of an event's earlier times one time is set beside.</summary>
    public const int Earlier = 6;

    /// <summary>How far back Past events looks, in days, unless asked otherwise.</summary>
    public const int PastDays = 90;

    /// <summary>The longest stretch Past events may be asked for, in days.</summary>
    public const int MaxPastDays = 366;

    /// <summary>How many times Past events lists.</summary>
    public const int PastListed = 50;

    /// <summary>
    /// One time an event ran, with who was seen when <paramref name="canSeeWhoWasThere"/>, and the
    /// event's earlier times set beside it. Null when <paramref name="at"/> is not a time the event
    /// has run.
    /// </summary>
    public async Task<CalendarResultsView?> ForOccurrenceAsync(
        CalendarEvent calendarEvent,
        DateTimeOffset at,
        bool canSeeWhoWasThere,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var openings = await OpeningsAsync([calendarEvent.Id], ct);
        var times = Started(calendarEvent, openings.GetValueOrDefault(calendarEvent.Id) ?? [], DateTimeOffset.MinValue, now);

        var index = times.FindIndex(t => t.StartsAt == at);
        if (index < 0)
            return null;

        var earlier = times.Take(index).TakeLast(Earlier).ToList();
        var wanted = earlier.Append(times[index]).Select(t => (calendarEvent, t)).ToList();

        var (results, lives) = await ResultsAsync(wanted, openings, canSeeWhoWasThere, now, ct);
        var result = results[^1];

        IReadOnlyList<PersonSeen> people = [];

        if (canSeeWhoWasThere && result.Instance is { } instance && lives.TryGetValue(instance.Id, out var life))
            people = await WithNamesAsync(await new PresenceCounts(db).PeopleInInstanceAsync(life, ct), ct);

        return new CalendarResultsView(result, people, Usual(results.Take(results.Count - 1).ToList()), canSeeWhoWasThere, now);
    }

    /// <summary>
    /// Every time an event ran from <paramref name="from"/> until now, the most at once first, with
    /// no presence figures: one instance at a time would be a query each.
    /// </summary>
    public async Task<CalendarPastView> PastAsync(DateTimeOffset from, DateTimeOffset now, CancellationToken ct)
    {
        var events = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.DeletedAt == null && e.State != CalendarEventStates.Draft)
            .ToListAsync(ct);

        var openings = await OpeningsAsync(events.Select(e => e.Id).ToList(), ct);

        var wanted = events
            .SelectMany(e => Started(e, openings.GetValueOrDefault(e.Id) ?? [], from, now).Select(t => (e, t)))
            .ToList();

        var (results, _) = await ResultsAsync(wanted, openings, canSeeWhoWasThere: false, now, ct);

        var ranked = results
            .OrderByDescending(r => r.Instance?.PeakPeople ?? -1)
            .ThenByDescending(r => r.StartsAt)
            .ThenBy(r => r.EventId)
            .Take(PastListed)
            .ToList();

        return new CalendarPastView(ranked, from, now);
    }

    /// <summary>
    /// The times an event has started, from <paramref name="from"/> until <paramref name="now"/>,
    /// earliest first: the rule's, and every time Modbot opened an instance for it.
    /// </summary>
    internal static List<CalendarOccurrence> Started(
        CalendarEvent calendarEvent,
        IReadOnlyList<CalendarOpening> openings,
        DateTimeOffset from,
        DateTimeOffset now)
    {
        if (calendarEvent.State == CalendarEventStates.Draft)
            return [];

        // A cancelled event ran until the cancel and not after.
        var until = calendarEvent.State == CalendarEventStates.Cancelled && calendarEvent.CancelledAt is { } cancelled && cancelled < now
            ? cancelled
            : now;

        var length = calendarEvent.EndsAt - calendarEvent.StartsAt;
        if (length < TimeSpan.Zero)
            length = TimeSpan.Zero;

        var byStart = new SortedDictionary<DateTimeOffset, CalendarOccurrence>();

        foreach (var occurrence in CalendarRepeat.Between(calendarEvent, from, until))
            byStart[occurrence.StartsAt] = occurrence;

        foreach (var opening in openings)
        {
            var start = opening.OccurrenceStartsAt;
            if (start < until && start + length > from && !byStart.ContainsKey(start))
                byStart[start] = new CalendarOccurrence(start, start + length);
        }

        return [.. byStart.Values];
    }

    /// <summary>The time Modbot opened the instance for, or the start when it opens none.</summary>
    private static DateTimeOffset OpensAt(CalendarEvent calendarEvent, CalendarOccurrence occurrence) =>
        CalendarRepeat.OpensAt(calendarEvent, occurrence);

    private async Task<Dictionary<Guid, List<CalendarOpening>>> OpeningsAsync(IReadOnlyList<Guid> eventIds, CancellationToken ct)
    {
        var rows = await db.CalendarOpenings.AsNoTracking()
            .Where(o => eventIds.Contains(o.EventId))
            .ToListAsync(ct);

        return rows.GroupBy(o => o.EventId).ToDictionary(g => g.Key, g => g.ToList());
    }

    private async Task<(List<CalendarOccurrenceResult> Results, Dictionary<Guid, InstanceLife> Lives)> ResultsAsync(
        IReadOnlyList<(CalendarEvent Event, CalendarOccurrence Time)> wanted,
        IReadOnlyDictionary<Guid, List<CalendarOpening>> openings,
        bool canSeeWhoWasThere,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (wanted.Count == 0)
            return ([], []);

        var first = wanted.Min(w => OpensAt(w.Event, w.Time));
        var last = wanted.Max(w => w.Time.EndsAt);

        // The group's instances in the events' worlds that were open at some point in the stretch,
        // for the times Modbot did not open one itself.
        var groupId = (await db.GetSettingsAsync(ct)).ManagedGroupId;

        // An event that picks from a world list ran each date in that date's own world (world lists
        // design §5), not the one it has picked for the date it is on now.
        var listEventIds = wanted.Where(w => w.Event.WorldListId != null).Select(w => w.Event.Id).Distinct().ToList();
        var datePicks = listEventIds.Count == 0
            ? []
            : await db.WorldPicks.AsNoTracking()
                .Where(p => listEventIds.Contains(p.EventId) && p.Kind == WorldPickKinds.Date && p.PutBackAt == null)
                .ToDictionaryAsync(p => (p.EventId, p.OccurrenceStartsAt), p => p.WorldId, ct);

        string? WorldOf((CalendarEvent Event, CalendarOccurrence Time) w) =>
            datePicks.GetValueOrDefault((w.Event.Id, w.Time.StartsAt)) ?? w.Event.WorldId;

        var worldIds = wanted.Select(WorldOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

        var candidates = string.IsNullOrEmpty(groupId) || worldIds.Count == 0
            ? []
            : await db.VRChatInstances.AsNoTracking()
                .Where(i => i.GroupId == groupId
                    && worldIds.Contains(i.WorldId)
                    && i.OpenedAt < last
                    && (i.ClosedAt == null || i.ClosedAt > first))
                .Select(i => new Span(i.Id, i.WorldId, i.OpenedAt, i.ClosedAt))
                .ToListAsync(ct);

        var chosen = wanted
            .Select(w =>
            {
                var opened = openings.GetValueOrDefault(w.Event.Id)?
                    .FirstOrDefault(o => o.OccurrenceStartsAt == w.Time.StartsAt && o.InstanceId != null);

                return opened is not null
                    ? (Id: opened.InstanceId, ByModbot: true)
                    : (Id: Longest(candidates, WorldOf(w), OpensAt(w.Event, w.Time), w.Time.EndsAt, now), ByModbot: false);
            })
            .ToList();

        var ids = chosen.Where(c => c.Id != null).Select(c => c.Id!.Value).Distinct().ToList();
        var rows = ids.Count == 0
            ? new Dictionary<Guid, InstanceRow>()
            : (await InstanceRows.ReadAsync(db, db.VRChatInstances.AsNoTracking().Where(i => ids.Contains(i.Id)), now, ct))
                .ToDictionary(r => r.Id);

        // Bounded the way the instance popup bounds them: from when it opened until it closed, or
        // until it was last seen while it is still open. VRChat hands a number out again once an
        // instance closes, so another evening's people would otherwise count as this one's.
        var lives = ids.Count == 0
            ? []
            : (await db.VRChatInstances.AsNoTracking()
                .Where(i => ids.Contains(i.Id) && i.VRChatInstanceId != null && i.VRChatInstanceId != "")
                .Select(i => new { i.Id, i.WorldId, i.VRChatInstanceId, i.OpenedAt, EndsAt = i.ClosedAt ?? i.LastSeenAt })
                .ToListAsync(ct))
                .ToDictionary(i => i.Id, i => new InstanceLife(i.WorldId, i.VRChatInstanceId!, i.OpenedAt, i.EndsAt));

        var seen = new Dictionary<Guid, PlaceCounts>();
        if (canSeeWhoWasThere)
        {
            var presence = new PresenceCounts(db);

            foreach (var (id, life) in lives)
                seen[id] = await presence.ForInstanceAsync(life, ct);
        }

        var counted = await CountedAsync(first, last + After, ct);

        List<CalendarOccurrenceResult> results = [.. wanted.Select((w, i) =>
        {
            var instance = chosen[i].Id is { } id ? rows.GetValueOrDefault(id) : null;
            var from = OpensAt(w.Event, w.Time);
            var to = w.Time.EndsAt + After;

            return new CalendarOccurrenceResult(
                w.Event.Id,
                CalendarRepeat.TitleOf(w.Event, w.Time),
                w.Time.StartsAt,
                w.Time.EndsAt,
                instance,
                instance is not null && chosen[i].ByModbot,
                Count(counted.Joined, from, to),
                Count(counted.Requests, from, to),
                instance is not null && canSeeWhoWasThere ? seen.GetValueOrDefault(instance.Id) ?? PlaceCounts.Nothing : null);
        })];

        return (results, lives);
    }

    private readonly record struct Span(Guid Id, string WorldId, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt);

    /// <summary>
    /// The instance in <paramref name="worldId"/> open for most of the stretch, the earlier opened
    /// on a tie; null when none was open in it.
    /// </summary>
    private static Guid? Longest(IReadOnlyList<Span> candidates, string? worldId, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        if (worldId is null)
            return null;

        Guid? best = null;
        var bestOverlap = TimeSpan.Zero;
        var bestOpened = DateTimeOffset.MaxValue;

        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.WorldId, worldId, StringComparison.Ordinal))
                continue;

            var end = candidate.ClosedAt ?? now;
            var overlap = (end < to ? end : to) - (candidate.OpenedAt > from ? candidate.OpenedAt : from);

            if (overlap <= TimeSpan.Zero)
                continue;

            if (overlap > bestOverlap || (overlap == bestOverlap && candidate.OpenedAt < bestOpened))
            {
                best = candidate.Id;
                bestOverlap = overlap;
                bestOpened = candidate.OpenedAt;
            }
        }

        return best;
    }

    /// <summary>
    /// When each group join and each join request happened over the whole stretch, sorted, so each
    /// time's count is two searches rather than a query.
    /// </summary>
    private async Task<(DateTimeOffset[] Joined, DateTimeOffset[] Requests)> CountedAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var facts = await db.Events.AsNoTracking()
            .Where(e => (e.Type == FactType.MemberJoined || e.Type == FactType.JoinRequestCreated)
                && e.OccurredAt >= from
                && e.OccurredAt < to)
            .Select(e => new { e.Type, e.OccurredAt })
            .ToListAsync(ct);

        DateTimeOffset[] Of(string type) => [.. facts.Where(f => f.Type == type).Select(f => f.OccurredAt).Order()];

        return (Of(FactType.MemberJoined), Of(FactType.JoinRequestCreated));
    }

    /// <summary>How many of the sorted instants fall in [from, to).</summary>
    internal static int Count(DateTimeOffset[] sorted, DateTimeOffset from, DateTimeOffset to) =>
        to <= from ? 0 : LowerBound(sorted, to) - LowerBound(sorted, from);

    private static int LowerBound(DateTimeOffset[] sorted, DateTimeOffset value)
    {
        var low = 0;
        var high = sorted.Length;

        while (low < high)
        {
            var middle = (low + high) / 2;
            if (sorted[middle] < value)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    /// <summary>The middle value of each figure across the earlier times.</summary>
    internal static CalendarUsual? Usual(IReadOnlyList<CalendarOccurrenceResult> earlier)
    {
        if (earlier.Count == 0)
            return null;

        var peaks = earlier.Where(r => r.Instance?.PeakPeople != null).Select(r => (decimal)r.Instance!.PeakPeople!.Value).ToList();
        var open = earlier.Where(r => r.Instance != null).Select(r => r.Instance!.MinutesOpen).ToList();

        // A time with nobody seen had no moderator's client there, which is not the same as nobody coming.
        var watched = earlier.Where(r => r.Seen is { Visitors: > 0 }).Select(r => r.Seen!).ToList();

        return new CalendarUsual(
            earlier.Count,
            Whole(Middle(peaks)),
            Middle(open) is { } m ? Math.Round(m, 1) : null,
            Whole(Middle([.. earlier.Select(r => (decimal)r.NewMembers)])) ?? 0,
            Whole(Middle([.. earlier.Select(r => (decimal)r.JoinRequests)])) ?? 0,
            Whole(Middle([.. watched.Select(s => (decimal)s.Visitors)])),
            Middle([.. watched.Select(s => s.MinutesSeen)]) is { } seen ? Math.Round(seen, 1) : null);
    }

    private static decimal? Middle(List<decimal> values)
    {
        if (values.Count == 0)
            return null;

        values.Sort();
        var half = values.Count / 2;

        return values.Count % 2 == 1 ? values[half] : (values[half - 1] + values[half]) / 2;
    }

    private static int? Whole(decimal? value) =>
        value is { } v ? (int)Math.Round(v, MidpointRounding.AwayFromZero) : null;

    /// <summary>Puts stored display names to the people seen, in one lookup.</summary>
    private async Task<IReadOnlyList<PersonSeen>> WithNamesAsync(IReadOnlyList<PersonSeen> people, CancellationToken ct)
    {
        if (people.Count == 0)
            return people;

        var ids = people.Select(p => p.UserId).Distinct(StringComparer.Ordinal).ToList();

        var names = await db.VRChatUsers.AsNoTracking()
            .Where(u => ids.Contains(u.UserId) && u.DisplayName != null)
            .Select(u => new { u.UserId, u.DisplayName })
            .ToDictionaryAsync(u => u.UserId, u => u.DisplayName!, StringComparer.Ordinal, ct);

        return [.. people.Select(p => p with { DisplayName = names.GetValueOrDefault(p.UserId) })];
    }
}
