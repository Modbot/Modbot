using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.VRChat.Calendar;

/// <summary>
/// Writes the facts the calendar's own loops record (calendar design §8): an occurrence opening,
/// an event finishing, an instance opened or not, a place failing. No actor -- Modbot did these.
/// </summary>
public sealed class CalendarFacts
{
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;

    public CalendarFacts(IFactWriter facts, EventPartitionMaintainer partitions, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);

        _facts = facts;
        _partitions = partitions;
        _clock = clock;
    }

    public async Task RecordAsync(
        string type,
        CalendarEvent calendarEvent,
        JsonObject? data = null,
        string? worldId = null,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        var payload = data ?? new JsonObject();
        payload.TryAdd("title", calendarEvent.Title);

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = calendarEvent.Id.ToString(),
                WorldId = worldId,
                InstanceId = instanceId,
                Source = FactSource.Modbot,
                Data = payload,
            },
            ct).ConfigureAwait(false);
    }
}
