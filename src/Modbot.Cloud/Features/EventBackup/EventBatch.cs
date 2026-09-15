using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Cloud.Common;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.Installs;

namespace Modbot.Cloud.Features.EventBackup;

/// <summary>
/// One <c>POST /api/v1/events</c> body: the client protocol's event batch (protocol 4.1), plus the
/// client's own send time and the paired server's id (cloud event backup spec 2).
/// </summary>
/// <remarks>Every field is nullable because this is untrusted input; <see cref="EventBatchCheck"/> decides what is required.</remarks>
public sealed record EventBatch(
    [property: JsonPropertyName("batchId")] string? BatchId,
    [property: JsonPropertyName("clientVersion")] string? ClientVersion,
    [property: JsonPropertyName("sentAt")] DateTimeOffset? SentAt,
    [property: JsonPropertyName("clockOffsetMs")] long? ClockOffsetMs,
    [property: JsonPropertyName("clockConfidence")] string? ClockConfidence,
    [property: JsonPropertyName("modbotServerId")] string? ModbotServerId,
    [property: JsonPropertyName("events")] IReadOnlyList<BatchEvent?>? Events);

/// <summary>One event, in the client protocol's shape (protocol 4.2), with a group that may be null.</summary>
public sealed record BatchEvent(
    [property: JsonPropertyName("clientEventId")] string? ClientEventId,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset? OccurredAt,
    [property: JsonPropertyName("occurredBefore")] DateTimeOffset? OccurredBefore,
    [property: JsonPropertyName("subjectId")] string? SubjectId,
    [property: JsonPropertyName("worldId")] string? WorldId,
    [property: JsonPropertyName("instanceId")] string? InstanceId,
    [property: JsonPropertyName("groupId")] string? GroupId,
    [property: JsonPropertyName("data")] JsonElement? Data);

/// <summary>What happened to a batch's events.</summary>
public sealed record EventBatchResponse(
    [property: JsonPropertyName("stored")] int Stored,
    [property: JsonPropertyName("duplicates")] int Duplicates);

/// <summary>One event, checked and ready to store.</summary>
public sealed record CheckedEvent(
    string ClientEventId,
    string Type,
    string? TypeRaw,
    DateTimeOffset OccurredAt,
    DateTimeOffset? OccurredBefore,
    string SubjectId,
    string WorldId,
    string InstanceId,
    string? GroupId,
    string Data);

/// <summary>A batch, checked.</summary>
public sealed record CheckedBatch(
    string ClientVersion,
    DateTimeOffset SentAt,
    long? ClockOffsetMs,
    string ClockConfidence,
    string? ModbotServerId,
    IReadOnlyList<CheckedEvent> Events);

/// <summary>
/// Decides whether a batch is well formed, and tidies what it may.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Refused</strong> (a <c>400</c>): no events, no <c>sentAt</c>, or an event with no id, no
/// type, no time, no subject, no world or no instance. A client that sends these is broken and
/// resending will not fix it.
/// </para>
/// <para>
/// <strong>Tidied, not refused:</strong> ids and text are cut to their column widths and stripped of
/// control characters; data over <see cref="StoredEvent.MaxDataBytes"/>, or not a JSON object, is stored
/// as <c>{}</c>. VRChat ids are never checked for shape (foundation 3.1.1).
/// </para>
/// </remarks>
public static class EventBatchCheck
{
    public static (CheckedBatch? Batch, string? Problem) Check(EventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Events is not { Count: > 0 } events)
            return (null, "A batch needs at least one event.");

        if (events.Count > EventBackupLimits.MaxEventsPerBatch)
            return (null, $"A batch may carry at most {EventBackupLimits.MaxEventsPerBatch} events.");

        if (batch.SentAt is not { } sentAt)
            return (null, "sentAt is required.");

        var checkedEvents = new List<CheckedEvent>(events.Count);
        foreach (var e in events)
        {
            if (e is null
                || ClientText.Clean(e.ClientEventId, StoredEvent.MaxEventIdLength) is not { } id
                || string.IsNullOrWhiteSpace(e.Type)
                || e.OccurredAt is not { } occurredAt
                || ClientText.Clean(e.SubjectId, StoredEvent.MaxIdLength) is not { } subject
                || ClientText.Clean(e.WorldId, StoredEvent.MaxIdLength) is not { } world
                || ClientText.Clean(e.InstanceId, StoredEvent.MaxInstanceIdLength) is not { } instance)
            {
                return (null, "Every event needs an id, a type, a time, a subject, a world and an instance.");
            }

            var (type, typeRaw) = EventTypes.Classify(e.Type);

            checkedEvents.Add(new CheckedEvent(
                id,
                type,
                typeRaw,
                occurredAt.ToUniversalTime(),
                e.OccurredBefore?.ToUniversalTime(),
                subject,
                world,
                instance,
                ClientText.Clean(e.GroupId, StoredEvent.MaxIdLength),
                Data(e.Data)));
        }

        var confidence = batch.ClockConfidence is "good" or "fair" or "poor" ? batch.ClockConfidence : "unknown";

        return (new CheckedBatch(
            ClientText.Clean(batch.ClientVersion, StoredEvent.MaxVersionLength) ?? "unknown",
            sentAt.ToUniversalTime(),
            batch.ClockOffsetMs,
            confidence,
            ClientText.Clean(batch.ModbotServerId, Install.MaxServerIdLength),
            checkedEvents), null);
    }

    private static string Data(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            return "{}";

        var text = element.GetRawText();

        // jsonb refuses the NUL escape, and one refused row would fail the whole batch.
        if (System.Text.Encoding.UTF8.GetByteCount(text) > StoredEvent.MaxDataBytes || text.Contains("\\u0000", StringComparison.Ordinal))
            return "{}";

        return text;
    }
}
