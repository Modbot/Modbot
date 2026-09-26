using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Audit;

/// <summary>
/// How certain a fact's timestamp is (spec 5.3).
/// </summary>
/// <remarks>
/// Carried explicitly rather than left for the browser to infer from a null, because the whole
/// point of <c>occurred_before</c> is that the difference must reach the person reading the
/// timeline. A window rendered as an instant is an invented precision, and it is invisible.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<TimePrecision>))]
public enum TimePrecision
{
    /// <summary>VRChat said when it happened. <c>OccurredBefore</c> is null.</summary>
    Exact = 1,

    /// <summary>
    /// It happened somewhere between <c>OccurredAt</c> and <c>OccurredBefore</c> — usually
    /// because a sync diff noticed a change between two polls and cannot know when inside that
    /// window it fell.
    /// </summary>
    Window = 2,
}

/// <summary>
/// What a fact's subject is, so that clicking it opens the right thing.
/// </summary>
/// <remarks>
/// The subject column holds whatever the source put there, and for VRChat's audit log that is
/// documented only as "typically a UserID, GroupID, GroupRoleID, or Location". The id itself is
/// never parsed to find out which (spec 3.1.1) — the fact's <em>type</em> says what its subject
/// is, and this carries that answer to the screen.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SubjectKind>))]
public enum SubjectKind
{
    /// <summary>A VRChat or Discord person. The ordinary case.</summary>
    Person = 1,

    /// <summary>An instance: the subject is the location string the event happened at.</summary>
    Instance = 2,

    /// <summary>The managed group itself.</summary>
    Group = 3,

    /// <summary>One of the group's roles.</summary>
    Role = 4,

    /// <summary>A Modbot account — the subject of Modbot's own record of what happened in Modbot.</summary>
    Account = 5,

    /// <summary>Something else: a case file, an invite link, a Discord channel, a partition name.</summary>
    Other = 6,
}

/// <summary>One moderator's client that reported a fact.</summary>
/// <param name="AccountId">The Modbot account the client belongs to.</param>
/// <param name="Name">That account's username. Null when the account is gone.</param>
/// <param name="At">When this client's report reached the server.</param>
public sealed record AuditReporter(Guid AccountId, string? Name, DateTimeOffset At);

/// <param name="Id">The fact's id. Also the second half of the paging cursor.</param>
/// <param name="OccurredBefore">Null when the time is exact; otherwise the end of the window.</param>
/// <param name="ObservedAt">When Modbot learned of it, which is not when it happened.</param>
/// <param name="Category">Which of the two logs this entry belongs to (spec 5.9.4).</param>
/// <param name="Description">
/// VRChat's own human-readable line for the entry, where it supplied one. Displayed as text and
/// never parsed — it is the remote system's prose, not a contract.
/// </param>
/// <param name="ActorName">
/// The actor's display name as it was recorded <em>at the time</em>, from the fact's payload.
/// Absent rather than substituted when the payload did not carry one.
/// </param>
/// <param name="TypeRaw">
/// The source's own word for the event, kept when Modbot had no name for it. Never null for a
/// <c>modbot.unrecognised</c> fact, and the thing that makes such a fact readable later: a screen
/// that learns the word renders every old row correctly without anything being rewritten.
/// </param>
/// <param name="SubjectKind">What the subject is, so a screen knows what clicking it should open.</param>
/// <param name="SubjectName">
/// The name stored for the subject now — a VRChat display name, a Modbot username. Looked up once
/// per page of entries, never once per row. Null when nothing is stored, and never substituted
/// with the id dressed up as a name.
/// </param>
/// <param name="WorldName">
/// What the world is called, from <c>vrchat_world</c>. Null when Modbot has only ever seen the id.
/// </param>
/// <param name="ModbotInstanceId">
/// Modbot's own id for the instance this happened in, where one could be matched. The fact log keys a
/// instance on the world and VRChat's number, which is handed out again after an instance closes, so the
/// match is made on the fact's time falling inside an instance's own open and close times.
/// </param>
/// <param name="InstanceName">
/// The name the matched instance was opened with, when it has one -- shown in place of
/// <c>instanceId</c>, VRChat's number. Null when no instance was matched or it has no name.
/// </param>
/// <param name="Data">
/// The fact's own payload, verbatim. Secrets are never in it by construction (spec 5.9.3).
/// </param>
/// <param name="SubjectTrustRank">The subject's VRChat trust rank as stored now, when the subject is a VRChat person whose tags are known.</param>
/// <param name="ActorTrustRank">The same for the actor.</param>
/// <param name="Linked">
/// The other facts that came from the same decision (spec 5.3.2) — a ban's instance kick, a
/// Discord ban's leave, Modbot's own record of the press behind VRChat's record of the result.
/// They are not listed as entries of their own, because a reader should not have to notice that
/// two rows a second apart are one thing; every one of them is here in full instead. Empty for
/// the ordinary fact, which is on its own.
/// </param>
/// <param name="ReportedBy">
/// Whose clients reported this, oldest first. Only a client-reported fact has any: the first is the
/// client whose report became the fact, and the rest are clients that reported the same thing
/// afterwards and were deduplicated into it (<c>modbot_event_report</c>). Two independent clients
/// agreeing is stronger evidence than one, which is why the extras are worth keeping at all.
///
/// No wider a gate than the entry itself: every client-reported fact is a moderation entry, so a
/// caller reading one already holds <c>ViewAuditLog</c>, and the device id this is resolved from has
/// always been in the entry's payload.
/// </param>
public sealed record AuditEntry(
    long Id,
    DateTimeOffset OccurredAt,
    DateTimeOffset? OccurredBefore,
    DateTimeOffset ObservedAt,
    TimePrecision Precision,
    string Type,
    string? TypeRaw,
    AuditCategory Category,
    string Source,
    string SubjectPlatform,
    string SubjectId,
    SubjectKind SubjectKind,
    string? SubjectName,
    string? ActorPlatform,
    string? ActorId,
    string? ActorName,
    string? WorldId,
    string? WorldName,
    string? InstanceId,
    Guid? ModbotInstanceId,
    string? InstanceName,
    string? Description,
    JsonNode? Data,
    TrustRank? SubjectTrustRank = null,
    TrustRank? ActorTrustRank = null,
    IReadOnlyList<AuditEntry>? Linked = null,
    IReadOnlyList<AuditReporter>? ReportedBy = null);

/// <param name="OccurredAt">Pass back as <c>beforeOccurredAt</c> for the next page.</param>
/// <param name="Id">Pass back as <c>beforeId</c>. Both are required — see the endpoint.</param>
public sealed record AuditCursor(DateTimeOffset OccurredAt, long Id);

/// <param name="OldestFact">
/// The earliest fact this caller can see, or null when they can see none. This is where the
/// timeline actually starts, which is rarely where the group's history does.
/// </param>
/// <param name="FirstObservedAt">
/// The earliest <c>observed_at</c> on a fact drawn from VRChat's audit log — in practice, when
/// this deployment first synced.
/// </param>
/// <param name="CatchUpComplete">
/// Whether the one-off walk back through the audit log VRChat still held has finished. Until it
/// has, the start of the timeline is still moving backwards.
/// </param>
public sealed record AuditCoverage(
    DateTimeOffset? OldestFact,
    DateTimeOffset? FirstObservedAt,
    bool CatchUpComplete);

/// <param name="Next">Null when this was the last page.</param>
public sealed record AuditPage(
    IReadOnlyList<AuditEntry> Entries,
    AuditCursor? Next,
    AuditCoverage Coverage);

/// <param name="Value">The <c>FactType</c> name, as sent back in <c>type</c>.</param>
/// <param name="Label">A short human label for the chip.</param>
public sealed record AuditTypeOption(string Value, string Label, AuditCategory Category);

/// <param name="Actors">
/// Distinct actors seen in the visible facts, newest activity first, with the display name last
/// recorded for them. Bounded — a filter list is not a member list.
/// </param>
/// <param name="CanViewModeration">Whether this caller holds <c>ViewAuditLog</c>.</param>
/// <param name="CanViewOperational">Whether this caller holds <c>ViewOperationalLog</c>.</param>
public sealed record AuditFilters(
    IReadOnlyList<AuditTypeOption> Types,
    IReadOnlyList<string> Sources,
    IReadOnlyList<AuditActor> Actors,
    bool CanViewModeration,
    bool CanViewOperational);

/// <param name="Id">Opaque. Never parsed, never validated (spec 3.1.1).</param>
/// <param name="Name">Last display name recorded alongside this id, if any.</param>
/// <param name="Actions">How many visible facts this actor caused.</param>
public sealed record AuditActor(string Platform, string Id, string? Name, int Actions);

internal static class AuditJson
{
    private static readonly JsonNodeOptions NodeOptions = new();

    /// <summary>
    /// Reads a fact's stored <c>jsonb</c> back into a node for the response.
    /// </summary>
    /// <remarks>
    /// A payload that will not parse is returned as a string rather than throwing. A single
    /// malformed row must not be able to take the whole audit log down — that is the one screen
    /// somebody opens when they are trying to find out what went wrong.
    /// </remarks>
    public static JsonNode? Parse(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return null;

        try
        {
            return JsonNode.Parse(data, NodeOptions);
        }
        catch (JsonException)
        {
            return JsonValue.Create(data);
        }
    }

    /// <summary>A string field from a fact payload, or null if it is absent or not a string.</summary>
    public static string? Text(JsonNode? payload, string property)
    {
        if (payload is not JsonObject obj)
            return null;

        if (!obj.TryGetPropertyValue(property, out var value) || value is not JsonValue candidate)
            return null;

        return candidate.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
    }
}
