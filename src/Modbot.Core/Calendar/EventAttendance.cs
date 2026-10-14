using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Live;

namespace Modbot.Core.Calendar;

/// <summary>Who came to one date of an event, and the most people there at once.</summary>
/// <param name="EventId">The event.</param>
/// <param name="OccurrenceStartsAt">The date, as <see cref="CalendarOpening.OccurrenceStartsAt"/> names it.</param>
/// <param name="People">VRChat user ids, each once, in id order.</param>
/// <param name="PeakHeadCount">The most people the group instance poll counted at once; null when it never counted.</param>
public sealed record EventTurnout(
    Guid EventId,
    DateTimeOffset OccurrenceStartsAt,
    IReadOnlyList<string> People,
    int? PeakHeadCount);

/// <summary>
/// Who came to an event: the people seen joining the instance each of its dates opened.
/// </summary>
/// <remarks>
/// <para>
/// "Came" is one of the two arrival facts, as the Regulars page counts a visit: a person seen
/// arriving, or already there when a moderator's app got there. So only time a moderator's Modbot
/// app stood in the instance is seen; somebody who came to an instance nobody from the team
/// attended is not here, and the head count is the only number that covers them.
/// </para>
/// <para>
/// Reads Modbot's own tables and nothing else. Written for the event recap; the milestones count
/// the dates each person came to from the same answer.
/// </para>
/// </remarks>
public sealed class EventAttendance
{
    private readonly ModbotContext _db;

    public EventAttendance(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>The turnout of every date of one event that opened an instance, earliest first.</summary>
    public async Task<IReadOnlyList<EventTurnout>> ForEventAsync(Guid eventId, CancellationToken ct = default)
    {
        var openings = await _db.CalendarOpenings.AsNoTracking()
            .Where(o => o.EventId == eventId && o.InstanceId != null)
            .ToListAsync(ct).ConfigureAwait(false);

        return await ForOpeningsAsync(openings, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The turnout of these dates, earliest first. A date whose opening has no instance, or whose
    /// instance Modbot has no number for, comes back with nobody and no head count.
    /// </summary>
    public async Task<IReadOnlyList<EventTurnout>> ForOpeningsAsync(
        IReadOnlyCollection<CalendarOpening> openings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(openings);

        var ids = openings.Where(o => o.InstanceId is not null).Select(o => o.InstanceId!.Value).Distinct().ToList();

        var instances = ids.Count == 0
            ? new Dictionary<Guid, VRChatInstance>()
            : await _db.VRChatInstances.AsNoTracking()
                .Where(i => ids.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, ct).ConfigureAwait(false);

        var result = new List<EventTurnout>(openings.Count);

        foreach (var opening in openings.OrderBy(o => o.OccurrenceStartsAt).ThenBy(o => o.EventId))
        {
            if (opening.InstanceId is not { } id || !instances.TryGetValue(id, out var instance))
            {
                result.Add(new EventTurnout(opening.EventId, opening.OccurrenceStartsAt, [], null));
                continue;
            }

            result.Add(new EventTurnout(
                opening.EventId,
                opening.OccurrenceStartsAt,
                await PeopleAsync(instance, ct).ConfigureAwait(false),
                instance.PeakUserCount));
        }

        return result;
    }

    /// <summary>The people seen arriving in one instance, from the moment it opened to the moment it closed.</summary>
    private async Task<IReadOnlyList<string>> PeopleAsync(VRChatInstance instance, CancellationToken ct)
    {
        if (instance.VRChatInstanceId is not { Length: > 0 } number)
            return [];

        var from = instance.OpenedAt - InstancePeopleReader.BeforeOpening;
        var worldId = instance.WorldId;

        var facts = _db.Events.AsNoTracking()
            .Where(e => e.InstanceId == number
                && e.WorldId == worldId
                && e.OccurredAt >= from
                && (e.Type == FactType.InstanceJoined || e.Type == FactType.InstancePresenceObserved));

        if (instance.ClosedAt is { } closedAt)
            facts = facts.Where(e => e.OccurredAt <= closedAt);

        var people = await facts.Select(e => e.SubjectId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        people.Sort(StringComparer.Ordinal);
        return people;
    }
}
