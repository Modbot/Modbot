using System.Text.Json.Serialization;
using Modbot.Core.Users;

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

/// <summary>Where somebody who is not in the group stands with it: what staff need to know to invite them.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Invitation>))]
public enum Invitation
{
    /// <summary>Neither invited nor asking. Includes people who left or were kicked.</summary>
    NotInvited,

    /// <summary>The group has invited them and nothing since has answered it.</summary>
    Invited,

    /// <summary>They have asked to join and nobody has answered.</summary>
    AskedToJoin,
}

/// <param name="SubjectId">Opaque VRChat id. Never validated for shape.</param>
/// <param name="DisplayName">
/// Arbitrary user-controlled text, and therefore hostile input on a display surface — it may carry
/// bidi overrides, zero-width characters or a name chosen to impersonate somebody else.
/// </param>
/// <param name="PriorActions">How many moderation actions this group has previously taken.</param>
/// <param name="Flags">Short labels, already resolved server-side. Shown verbatim, never parsed.</param>
/// <param name="TrustRank">Their VRChat trust rank as the server last stored it. Null when the server does not know it yet, or is too old to send it.</param>
/// <param name="EighteenPlus">Whether they carry Modbot's 18+ mark. Null when the server has not read their profile yet, or is too old to send it.</param>
/// <param name="PictureUrl">
/// Where their picture is, as the paired server's own address for it (never VRChat's), or null when the
/// server holds none, has VRChat pictures switched off, or is too old to say. The server sends a path;
/// <see cref="HttpOverlayReadClient"/> makes it whole, and only for a path of the form the server's
/// picture route has, so what is fetched from it is always from the paired server.
/// </param>
/// <param name="IsMember">
/// Whether they are a member of the group now. Apart from <paramref name="Standing"/>, which holds one word and
/// shows Staff or Flagged for somebody who is also a member. Null when the server is too old to say, and for a
/// list made from this PC's log: then no mark is drawn, since nobody can say they are not in the group.
/// </param>
/// <param name="Invitation">
/// For somebody who is not a member: whether the group has invited them or they have asked to join.
/// Null for a member, and from a server too old to say (the row then says only that they are not in the group).
/// </param>
/// <param name="CompanionRunning">
/// Whether their own companion is running now. Null from a server too old to say; the row draws its mark
/// only for true.
/// </param>
public sealed record RosterMember(
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("standing")] RosterStanding Standing,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags,
    [property: JsonPropertyName("trustRank")] TrustRank? TrustRank = null,
    [property: JsonPropertyName("eighteenPlus")] bool? EighteenPlus = null,
    [property: JsonPropertyName("pictureUrl")] string? PictureUrl = null,
    [property: JsonPropertyName("isMember")] bool? IsMember = null,
    [property: JsonPropertyName("invitation")] Invitation? Invitation = null,
    [property: JsonPropertyName("companionRunning")] bool? CompanionRunning = null);

/// <summary>
/// What one server knows about the instance the moderator is standing in.
/// </summary>
/// <remarks>
/// A pairing sees exactly one group's context. There is no shape here that could carry another
/// group's data, which is the same boundary as routing, arriving from the other direction.
/// </remarks>
/// <param name="HeadsUps">
/// The heads-ups standing in this instance, oldest first. Null from a server too old to send them.
/// </param>
public sealed record InstanceContext(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("members")] IReadOnlyList<RosterMember> Members,
    [property: JsonPropertyName("headsUps")] IReadOnlyList<HeadsUp>? HeadsUps = null)
{
    /// <summary>The heads-ups this build can show, never null.</summary>
    [JsonIgnore]
    public IReadOnlyList<HeadsUp> HeadsUpsOrNone => HeadsUps is null ? [] : [.. HeadsUps.Where(h => h.KindOrNull is not null)];
}

/// <summary>
/// One person's profile summary: deliberately not the full web profile.
/// </summary>
/// <remarks>
/// An overlay card shows prior actions, roles, join date, times seen, notes and current flags, and nothing that needs
/// scrolling in a headset. Reading is fine in VR; scrolling and typing are hostile.
/// </remarks>
/// <param name="TimesSeen">How many times a moderator's client saw them arrive. Null when never, or when the server is too old to send it.</param>
/// <param name="FirstSeenAt">When they were first seen in an instance. Null when never, or when the server is too old to send it.</param>
/// <param name="NoteCount">How many notes stand on them. Null when none, or when the server is too old to send it.</param>
/// <param name="LastAction">The last kick, ban, unban or warn the group gave them, in the server's word. Null when none, or when the server is too old to send it.</param>
/// <param name="LastActionAt">When <paramref name="LastAction"/> happened.</param>
/// <param name="Watched">Whether somebody is watching them. Null from a server too old to say.</param>
/// <param name="AvatarName">
/// The avatar they were last seen wearing. Null when none was seen, when the server is too old to say,
/// and when this moderator may not mark avatars: the server leaves it out for them, so the card offers
/// Mark avatar only to somebody it would work for.
/// </param>
/// <param name="AvatarListed">Whether <paramref name="AvatarName"/> is already on the server's problem avatar list.</param>
/// <param name="Invitation">As <see cref="RosterMember.Invitation"/>: set for somebody who is not a member, null otherwise and from a server too old to say.</param>
/// <param name="CompanionRunning">As <see cref="RosterMember.CompanionRunning"/>.</param>
public sealed record UserSummary(
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("standing")] RosterStanding Standing,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("joinedAt")] DateTimeOffset? JoinedAt,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("trustRank")] TrustRank? TrustRank = null,
    [property: JsonPropertyName("timesSeen")] int? TimesSeen = null,
    [property: JsonPropertyName("firstSeenAt")] DateTimeOffset? FirstSeenAt = null,
    [property: JsonPropertyName("noteCount")] int? NoteCount = null,
    [property: JsonPropertyName("lastAction")] string? LastAction = null,
    [property: JsonPropertyName("lastActionAt")] DateTimeOffset? LastActionAt = null,
    [property: JsonPropertyName("watched")] bool? Watched = null,
    [property: JsonPropertyName("avatarName")] string? AvatarName = null,
    [property: JsonPropertyName("avatarListed")] bool? AvatarListed = null,
    [property: JsonPropertyName("invitation")] Invitation? Invitation = null,
    [property: JsonPropertyName("companionRunning")] bool? CompanionRunning = null);

/// <summary>
/// The one thing that genuinely needs push: a flagged user just joined this instance.
/// </summary>
/// <remarks>
/// By the time a thirty-second poll notices, the moment has passed. Everything else — roster
/// refresh, flag updates, health — rides the ordinary batch cycle.
/// </remarks>
/// <param name="WorldId">
/// The world the instance is in, when the server says. An instance number is only unique inside
/// one world, so the overlay compares both. Null from a server that does not say.
/// </param>
/// <param name="Kind"><c>crash</c> for a possible crash, which is about the instance and has no subject; null for a flagged person.</param>
/// <param name="People">Who changed avatar or joined just before a possible crash, newest first. Null on every other alert.</param>
public sealed record FlaggedJoinAlert(
    [property: JsonPropertyName("alertId")] string AlertId,
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("raisedAt")] DateTimeOffset RaisedAt,
    [property: JsonPropertyName("trustRank")] TrustRank? TrustRank = null,
    [property: JsonPropertyName("worldId")] string? WorldId = null,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("people")] IReadOnlyList<CrashPerson>? People = null)
{
    /// <summary>The <see cref="Kind"/> of a possible crash.</summary>
    public const string CrashKind = "crash";

    /// <summary>Whether this is a possible crash rather than a flagged person.</summary>
    public bool IsCrash => Kind == CrashKind;
}

/// <summary>One person a possible crash alert names, and what they did just before it.</summary>
/// <param name="What">Words for the card: <c>joined</c> or <c>changed avatar</c>.</param>
public sealed record CrashPerson(
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("what")] string What);
