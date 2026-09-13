using System.Text.Json;
using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using VRChat.API.Model;

namespace Modbot.VRChat.Sync;

/// <summary>Why an audit entry did not become a fact.</summary>
public enum AuditLogRejection
{
    /// <summary>It did. <see cref="AuditLogMapping.Fact"/> is set.</summary>
    None,

    /// <summary>Modbot has no fact type for this event type. Reported, never dropped silently.</summary>
    UnknownEventType,

    /// <summary>The entry names nobody it happened to, so there is no subject to record it under.</summary>
    MissingTarget,

    /// <summary>The entry carries no timestamp, so there is no partition it belongs in.</summary>
    MissingTimestamp,
}

/// <param name="Fact">The fact to write, when there is one.</param>
/// <param name="EntryId">
/// VRChat's id for the entry. The idempotency key: a poll deliberately re-reads a window, so the
/// producer has to be able to recognise an entry it has already recorded.
/// </param>
public readonly record struct AuditLogMapping(
    FactRecord? Fact,
    AuditLogRejection Rejection,
    string? EntryId,
    string EventType)
{
    public bool Mapped => Fact is not null;
}

/// <summary>
/// Turns one <see cref="GroupAuditLogEntry"/> into a fact.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and separate from the job that fetches pages, because this is where all the judgement
/// is: which event types mean what, who the actor was, and how VRChat's timestamps are read. A
/// test for "an audit entry type you do not recognise" should not need a database or an HTTP
/// stub to ask the question.
/// </para>
/// <para>
/// <strong>Actor attribution is the point of this source.</strong> Spec 5.8's accountability is
/// built entirely on knowing who did a thing, and VRChat's audit log is the only place that
/// survives -- a sync diff can see that someone was banned and can never see by whom. So
/// <c>actorId</c> is carried onto every fact, and the actor's display name is kept alongside it
/// in the payload because an id alone is unreadable in a timeline months later.
/// </para>
/// </remarks>
public static class AuditLogEntryMapper
{
    public static AuditLogMapping Map(GroupAuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var eventType = entry.EventType ?? string.Empty;
        var entryId = string.IsNullOrWhiteSpace(entry.Id) ? null : entry.Id;

        // An event type Modbot has no name for is still recorded -- as Unrecognised, with VRChat's
        // own wording kept in TypeRaw -- and reported so somebody adds the mapping. It used to be
        // dropped. That was the one thing this producer must never do: VRChat's audit log ages
        // out, so an entry not written today cannot be fetched tomorrow (spec 5.1).
        var recognised = GroupAuditLogEvents.TryMap(eventType, out var type);
        if (!recognised)
            type = FactType.Unrecognised;

        // These two are different: with no subject or no time there is genuinely no fact to write.
        if (string.IsNullOrWhiteSpace(entry.TargetId))
            return new AuditLogMapping(null, AuditLogRejection.MissingTarget, entryId, eventType);

        if (entry.CreatedAt == default)
            return new AuditLogMapping(null, AuditLogRejection.MissingTimestamp, entryId, eventType);

        var fact = new FactRecord
        {
            Type = type,
            TypeRaw = recognised ? null : eventType,
            OccurredAt = ReadTimestamp(entry.CreatedAt),

            // No window. The audit log states when the thing happened, which is exactly the
            // distinction spec 5.3 draws between this source and a sync diff: an inferred
            // departure gets an occurred_before and this does not.
            OccurredBefore = null,

            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = entry.TargetId,

            ActorPlatform = string.IsNullOrWhiteSpace(entry.ActorId) ? null : FactPlatform.VRChat,
            ActorId = string.IsNullOrWhiteSpace(entry.ActorId) ? null : entry.ActorId,

            Source = FactSource.AuditLog,
            Data = Payload(entry, eventType),
        };

        return new AuditLogMapping(
            fact,
            recognised ? AuditLogRejection.None : AuditLogRejection.UnknownEventType,
            entryId,
            eventType);
    }

    /// <summary>
    /// Reads VRChat's <c>created_at</c> as an instant.
    /// </summary>
    /// <remarks>
    /// An unspecified <see cref="DateTimeKind"/> is read as UTC rather than as local time. The SDK
    /// deserialises through Newtonsoft, whose <c>Kind</c> depends on how the string was written
    /// and on the process's settings -- and reading an API timestamp as local would make every
    /// fact's time depend on the host's timezone. That is precisely the class of silent,
    /// plausible-looking corruption spec 4.4 exists to rule out, so the assumption is made here,
    /// once, in the open.
    /// </remarks>
    internal static DateTimeOffset ReadTimestamp(DateTime createdAt) => createdAt.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(createdAt),
        DateTimeKind.Local => new DateTimeOffset(createdAt).ToUniversalTime(),
        _ => new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)),
    };

    private static JsonObject Payload(GroupAuditLogEntry entry, string eventType)
    {
        var payload = new JsonObject
        {
            // Kept so a later pass can recognise this entry as one it has already recorded, and
            // so spec 5.9.1's merge can line a VRChat entry up with Modbot's own record of the
            // action that caused it.
            ["auditEntryId"] = entry.Id,
            ["eventType"] = eventType,
            ["groupId"] = entry.GroupId,
        };

        if (!string.IsNullOrWhiteSpace(entry.ActorDisplayName))
            payload["actorDisplayName"] = entry.ActorDisplayName;

        if (!string.IsNullOrWhiteSpace(entry.Description))
            payload["description"] = entry.Description;

        if (ReadEntryData(entry.Data) is { } data)
            payload["auditData"] = data;

        return payload;
    }

    /// <summary>
    /// Carries VRChat's per-event payload through verbatim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Untouched and unparsed, because its shape is documented only as "dependent on the event
    /// type" and guessing at it is how a role id ends up in the field meant for a user id. A
    /// role grant's role, a ban's reason, the old and new value of a renamed group -- all of it
    /// is in here, and the queries that want it can read it out of <c>jsonb</c> later without
    /// this producer having had to be right about it in advance.
    /// </para>
    /// <para>
    /// It is also the one place an unrecognised shape cannot cost anything: it is stored, not
    /// interpreted.
    /// </para>
    /// </remarks>
    private static JsonNode? ReadEntryData(object? data)
    {
        if (data is null)
            return null;

        // The SDK types this as `object` and fills it from Newtonsoft, so what arrives is a JToken
        // whose ToString() is JSON. Re-parsing the text rather than referencing Newtonsoft keeps
        // the dependency out of Modbot and works whatever the SDK deserialises into next.
        var text = data as string ?? data.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // Not JSON after all. Keep it as a string: an unreadable payload is still evidence,
            // and losing it would be losing the only detail the entry carried.
            return JsonValue.Create(text);
        }
    }
}
