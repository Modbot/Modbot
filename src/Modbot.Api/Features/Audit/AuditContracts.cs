using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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
/// <param name="Data">
/// The fact's own payload, verbatim. Secrets are never in it by construction (spec 5.9.3).
/// </param>
public sealed record AuditEntry(
    long Id,
    DateTimeOffset OccurredAt,
    DateTimeOffset? OccurredBefore,
    DateTimeOffset ObservedAt,
    TimePrecision Precision,
    string Type,
    AuditCategory Category,
    string Source,
    string SubjectPlatform,
    string SubjectId,
    string? ActorPlatform,
    string? ActorId,
    string? ActorName,
    string? WorldId,
    string? InstanceId,
    string? Description,
    JsonNode? Data);

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
/// <param name="BackfillComplete">
/// Whether the one-off walk back through the audit log VRChat still held has finished. Until it
/// has, the start of the timeline is still moving backwards.
/// </param>
public sealed record AuditCoverage(
    DateTimeOffset? OldestFact,
    DateTimeOffset? FirstObservedAt,
    bool BackfillComplete);

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
