using System.Text.Json.Serialization;

namespace Modbot.Companion.Overlay;

/// <summary>Why a roster row is worth a moderator's attention.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RosterStanding>))]
public enum RosterStanding
{
    /// <summary>Nothing on record. The overwhelming majority of a roster.</summary>
    Ordinary,

    /// <summary>A member of the group whose instance this is.</summary>
    Member,

    /// <summary>Staff of that group. Shown so a moderator knows who else can act.</summary>
    Staff,

    /// <summary>
    /// Prior kicks, an active warning, or a flag. The highest-value thing the overlay shows.
    /// </summary>
    Flagged,
}

/// <param name="SubjectId">Opaque VRChat id. Never validated for shape.</param>
/// <param name="DisplayName">
/// Arbitrary user-controlled text, and therefore hostile input on a display surface — it may carry
/// bidi overrides, zero-width characters or a name chosen to impersonate somebody else.
/// </param>
/// <param name="PriorActions">How many moderation actions this group has previously taken.</param>
/// <param name="Flags">Short labels, already resolved server-side. Shown verbatim, never parsed.</param>
public sealed record RosterMember(
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("standing")] RosterStanding Standing,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags);

/// <summary>
/// What one server knows about the instance the moderator is standing in.
/// </summary>
/// <remarks>
/// A pairing sees exactly one group's context. There is no shape here that could carry another
/// group's data, which is the same boundary as routing, arriving from the other direction.
/// </remarks>
public sealed record InstanceContext(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("members")] IReadOnlyList<RosterMember> Members);

/// <summary>
/// One person's profile summary: deliberately not the full web profile.
/// </summary>
/// <remarks>
/// An overlay card shows prior actions, roles, join date and current flags, and nothing that needs
/// scrolling in a headset. Reading is fine in VR; scrolling and typing are hostile.
/// </remarks>
public sealed record UserSummary(
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("standing")] RosterStanding Standing,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("joinedAt")] DateTimeOffset? JoinedAt,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles);

/// <summary>
/// The one thing that genuinely needs push: a flagged user just joined this instance.
/// </summary>
/// <remarks>
/// By the time a thirty-second poll notices, the moment has passed. Everything else — roster
/// refresh, flag updates, health — rides the ordinary batch cycle.
/// </remarks>
public sealed record FlaggedJoinAlert(
    [property: JsonPropertyName("alertId")] string AlertId,
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("raisedAt")] DateTimeOffset RaisedAt);
