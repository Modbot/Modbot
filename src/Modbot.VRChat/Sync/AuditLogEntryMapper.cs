using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using VRChat.API.Model;

using Modbot.Core.Data;

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
/// <para>
/// <strong>Every field VRChat sent is kept, for every event type.</strong> The payload carries the
/// whole entry -- id, type, group, actor, actor's display name, target, time, description and the
/// per-event <c>data</c> verbatim -- so that a question nobody has asked yet can be answered from
/// what was recorded rather than from what somebody thought would matter. Fields the producer
/// understands are lifted <em>in addition</em> to that copy, never instead of it.
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

        var occurredAt = ReadTimestamp(entry.CreatedAt);
        var data = ReadEntryData(entry.Data);
        var instance = InstanceOf(type, entry, data);

        var fact = new FactRecord
        {
            Type = type,
            TypeRaw = recognised ? null : eventType,
            OccurredAt = occurredAt,

            // No window. The audit log states when the thing happened, which is exactly the
            // distinction spec 5.3 draws between this source and a sync diff: an inferred
            // departure gets an occurred_before and this does not.
            OccurredBefore = null,

            SubjectPlatform = FactPlatform.VRChat,

            // Whatever VRChat put in targetId, untouched. For an instance create, close, update
            // or announcement that is the location string itself, and it stays here whole; the
            // world and instance columns below are filled beside it, never instead of it.
            SubjectId = entry.TargetId,

            ActorPlatform = string.IsNullOrWhiteSpace(entry.ActorId) ? null : FactPlatform.VRChat,
            ActorId = string.IsNullOrWhiteSpace(entry.ActorId) ? null : entry.ActorId,

            WorldId = instance.WorldId,
            InstanceId = instance.InstanceId,

            Source = FactSource.AuditLog,
            Data = Payload(entry, eventType, type, occurredAt, data),
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
    /// <para>
    /// An unspecified <see cref="DateTimeKind"/> is read as UTC rather than as local time. The SDK
    /// deserialises through Newtonsoft, whose <c>Kind</c> depends on how the string was written
    /// and on the process's settings -- and reading an API timestamp as local would make every
    /// fact's time depend on the host's timezone. That is precisely the class of silent,
    /// plausible-looking corruption spec 4.4 exists to rule out, so the assumption is made here,
    /// once, in the open.
    /// </para>
    /// <para>
    /// Public since 2026-09-18, when the join queue became the second thing to read a VRChat
    /// timestamp off a response body. One copy of this assumption, not two that can drift.
    /// </para>
    /// </remarks>
    public static DateTimeOffset ReadTimestamp(DateTime createdAt) => createdAt.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(createdAt),
        DateTimeKind.Local => new DateTimeOffset(createdAt).ToUniversalTime(),
        _ => new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)),
    };

    /// <summary>
    /// The whole entry as VRChat sent it, plus the few fields there is evidence to lift.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The actor, the target and the time are also columns on the fact. The duplication is
    /// deliberate: the columns are Modbot's reading of the entry, and the payload is the entry.
    /// If a later change reinterprets what a column means, the record of what VRChat actually
    /// said is still here.
    /// </para>
    /// <para>
    /// Nothing is omitted for being blank. A null is written as a null, so a reader can tell
    /// "VRChat sent nothing" from "Modbot did not keep it".
    /// </para>
    /// </remarks>
    private static JsonObject Payload(
        GroupAuditLogEntry entry,
        string eventType,
        string type,
        DateTimeOffset occurredAt,
        JsonNode? data)
    {
        var payload = new JsonObject
        {
            // Kept so a later pass can recognise this entry as one it has already recorded, and
            // so spec 5.9.1's merge can line a VRChat entry up with Modbot's own record of the
            // action that caused it.
            ["auditEntryId"] = entry.Id,
            ["eventType"] = eventType,
            ["groupId"] = entry.GroupId,
            ["actorId"] = entry.ActorId,
            ["actorDisplayName"] = entry.ActorDisplayName,
            ["targetId"] = entry.TargetId,
            ["createdAt"] = occurredAt.ToString("O", CultureInfo.InvariantCulture),
            ["description"] = entry.Description,
            ["auditData"] = data,
        };

        Lift(payload, type, data);

        return payload;
    }

    /// <summary>
    /// The world and instance an instance event happened in, for the two columns the fact row
    /// keeps for exactly that. Null for every other event type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where the location string sits depends on the event, and both places were observed across
    /// 531 live rows with the key set stable in every one (audit-log research section 6): a kick
    /// or a warn names the person in <c>targetId</c> and puts the location in
    /// <c>auditData.location</c>; a create, close, update or announcement puts the location in
    /// <c>targetId</c> itself. This is what lets "which instances get the most kicks" be a query
    /// on two indexed columns instead of a substring match inside <c>jsonb</c>.
    /// </para>
    /// <para>
    /// Split by delimiters only (<see cref="InstanceLocationParts"/>). The string is not checked
    /// for shape first, because a shape check is how legacy ids get silently dropped (spec 3.1.1).
    /// </para>
    /// </remarks>
    private static InstanceLocationParts InstanceOf(string type, GroupAuditLogEntry entry, JsonNode? data)
    {
        switch (type)
        {
            case FactType.GroupInstanceKick:
            case FactType.GroupInstanceWarn:
                return data is JsonObject fields
                       && fields["location"] is JsonValue value
                       && value.TryGetValue<string>(out var location)
                    ? InstanceLocationParts.Split(location)
                    : default;

            case FactType.GroupInstanceCreated:
            case FactType.GroupInstanceClosed:
            case FactType.GroupInstanceUpdated:
            case FactType.GroupInstanceAnnouncement:
                return InstanceLocationParts.Split(entry.TargetId);

            default:
                return default;
        }
    }

    /// <summary>
    /// Copies into first-class fields only what live data or VRChat's own documentation has
    /// shown the shape of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two kinds of lift, and they are decided differently.
    /// </para>
    /// <para>
    /// <strong>The <c>{old, new}</c> diff is a shape, not a type.</strong> Any top-level value of
    /// <c>auditData</c> that is an object with exactly those two keys goes under <c>changed</c>,
    /// whatever the event type -- the key the group-info producer already uses for its own diffs,
    /// so a reader of the timeline meets one shape. It used to be lifted for <c>group.update</c>
    /// and <c>group.role.update</c> by name, and then <c>group.instance.update</c> arrived with
    /// <c>calendarEntryId: {old, new}</c> and was not covered. A scalar beside the pairs is not a
    /// diff and stays out. (The real <c>group.role.update</c> row turned out to carry
    /// <c>lastUpdatedByUserId</c> as a pair too -- <c>{old: null, new: usr_…}</c> -- so it lands
    /// under <c>changed</c> beside <c>permissions</c>, which is exactly why the lift goes by shape.)
    /// </para>
    /// <para>
    /// <strong>Scalars are lifted by type, and only where a real sample showed them.</strong>
    /// <c>roleId</c>/<c>roleName</c> on role events; <c>groupAccessType</c> on an instance create
    /// or close; <c>title</c>/<c>message</c> on an announcement; <c>title</c>/<c>text</c>/
    /// <c>authorId</c>/<c>visibility</c> on a post; <c>title</c>/<c>type</c>/<c>accessType</c> on
    /// a calendar event. Each of those was present in every row of its type across the live
    /// re-walk (audit-log research section 6). Nothing is lifted for a type whose payload has not
    /// been observed, because a field lifted under a guessed name is one two producers and a
    /// query then depend on; the verbatim copy loses nothing in the meantime.
    /// </para>
    /// <para>
    /// Lifting adds a copy; it never moves anything out of <c>auditData</c>. There is no ban
    /// reason to lift: VRChat's <c>description</c> for a ban is its own template ("User X was
    /// preemptively banned by Y.") and carries none.
    /// </para>
    /// </remarks>
    private static void Lift(JsonObject payload, string type, JsonNode? data)
    {
        if (data is not JsonObject fields)
            return;

        CopyChanges(fields, payload);

        switch (type)
        {
            case FactType.RoleGranted:
            case FactType.RoleRevoked:
            case FactType.RoleUpdated:
                CopyString(fields, payload, "roleId");
                CopyString(fields, payload, "roleName");
                break;

            case FactType.GroupInstanceCreated:
            case FactType.GroupInstanceClosed:
                CopyString(fields, payload, "groupAccessType");
                break;

            case FactType.GroupInstanceAnnouncement:
                CopyString(fields, payload, "title");
                CopyString(fields, payload, "message");
                break;

            case FactType.GroupPostCreated:
                CopyString(fields, payload, "title");
                CopyString(fields, payload, "text");
                CopyString(fields, payload, "authorId");
                CopyString(fields, payload, "visibility");
                break;

            case FactType.CalendarEventCreated:
                CopyString(fields, payload, "title");
                CopyString(fields, payload, "type");
                CopyString(fields, payload, "accessType");
                break;
        }
    }

    private static void CopyString(JsonObject from, JsonObject to, string key)
    {
        if (from[key] is JsonValue value && value.TryGetValue<string>(out var text))
            to[key] = text;
    }

    /// <summary>
    /// Every top-level <c>{old, new}</c> pair in the data, under <c>changed</c>. Nothing else
    /// qualifies: a scalar beside the pairs stays out, and so does a pair nested any deeper,
    /// because "the fields of this entry that changed" is a statement about the top level.
    /// </summary>
    private static void CopyChanges(JsonObject from, JsonObject to)
    {
        var changed = new JsonObject();

        foreach (var (key, value) in from)
        {
            if (IsOldNewPair(value))
                changed[key] = value!.DeepClone();
        }

        if (changed.Count > 0)
            to["changed"] = changed;
    }

    private static bool IsOldNewPair(JsonNode? value)
        => value is JsonObject pair
           && pair.Count == 2
           && pair.ContainsKey("old")
           && pair.ContainsKey("new");

    /// <summary>
    /// Carries VRChat's per-event payload through verbatim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Untouched and unparsed, because its shape is documented only as "dependent on the event
    /// type" and guessing at it is how a role id ends up in the field meant for a user id. A
    /// role grant's role, the old and new value of a renamed group -- whatever is in here, the
    /// queries that want it can read it out of <c>jsonb</c> later without this producer having
    /// had to be right about it in advance.
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
