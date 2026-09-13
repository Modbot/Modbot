using System.ComponentModel.DataAnnotations.Schema;

namespace Modbot.Core.Data.Entities;

/// <summary>
/// One VRChat user Modbot has ever seen, and what their public profile looked like the last time
/// it was fetched. The table is <c>vrchat_user</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is <strong>not</strong> <c>modbot_subject_profile</c> (foundation §5.10). That table is
/// derived from facts and can be rebuilt from them at any time; this one holds what VRChat's user
/// endpoint returned, which is not in the fact log at all and cannot be rebuilt from it. A bio a
/// user has since rewritten is gone unless it was written down here.
/// </para>
/// <para>
/// A row exists for every user id that has appeared anywhere Modbot looks -- the group audit
/// log, a client's presence report, a moderator's own action -- created with only the id when
/// nothing else is known yet. The profile columns fill in when the profile sync gets to them
/// (user profile sync design §3).
/// </para>
/// <para>
/// <strong>The 18+ flag is sticky</strong> (user profile sync design §4). VRChat users can hide
/// their age verification again after showing it, so a user who was 18+ verified yesterday can
/// look unverified today. Once Modbot has observed the verification even once,
/// <see cref="Is18PlusVerified"/> stays true and no sync clears it. Only a moderator holding
/// <see cref="ModbotPermissions.EditAgeVerification"/> can, and that is recorded as a fact naming
/// who did it.
/// </para>
/// <para>
/// Everything time-stamped here comes from <c>IModbotClock</c>, and every id is opaque text that
/// is stored exactly as VRChat sent it (foundation §3.1.1).
/// </para>
/// </remarks>
public class VRChatUser
{
    /// <summary>VRChat's id for the user. Opaque: never parsed, never validated, never normalised.</summary>
    public string UserId { get; set; } = string.Empty;

    // ── The profile as last fetched. All null until the first successful refresh. ──────────

    public string? DisplayName { get; set; }

    public string? Bio { get; set; }

    /// <summary>
    /// VRChat's online status word -- <c>active</c>, <c>join me</c>, <c>ask me</c>, <c>busy</c>,
    /// <c>offline</c>. Kept because it is on the object; not tracked as a change, because it flips
    /// every time the person logs in and out and a fact per flip would drown the log.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>The free-text line under the status. User-authored, so treat it as hostile input.</summary>
    public string? StatusDescription { get; set; }

    public string? Pronouns { get; set; }

    public string? CurrentAvatarImageUrl { get; set; }

    public string? CurrentAvatarThumbnailImageUrl { get; set; }

    /// <summary>
    /// The picture VRChat shows instead of the avatar when the user has set one
    /// (<c>profilePicOverride</c>). The SDK's own note: when it is not empty, use it instead.
    /// </summary>
    public string? ProfilePictureUrl { get; set; }

    /// <summary>The day the account was created, as VRChat reports it.</summary>
    public DateOnly? DateJoined { get; set; }

    /// <summary>VRChat's tag list for the user, as a JSON array. Trust-rank tags live here.</summary>
    [Column(TypeName = "jsonb")]
    public string? Tags { get; set; }

    /// <summary>
    /// Whatever VRChat put in <c>last_platform</c>. Documented as normally one of a few words and
    /// "supposedly" any Unity version string, so it is kept as text and never matched against a list.
    /// </summary>
    public string? LastPlatform { get; set; }

    // ── Age verification, as last seen and as remembered ──────────────────────────────────

    /// <summary>
    /// VRChat's <c>ageVerificationStatus</c> exactly as last seen: <c>18+</c>, <c>hidden</c>, or
    /// the obsolete <c>verified</c>. This is what the profile says <em>right now</em>, and it can
    /// go from <c>18+</c> back to <c>hidden</c> at the user's choice.
    /// </summary>
    public string? AgeVerificationStatus { get; set; }

    /// <summary>VRChat's <c>ageVerified</c> boolean as last seen. Null until the first refresh.</summary>
    public bool? AgeVerified { get; set; }

    /// <summary>
    /// Whether Modbot has ever observed this user as 18+ verified, or a moderator has said so.
    /// <strong>Sticky.</strong> A sync may set this to true and may never set it to false.
    /// </summary>
    public bool Is18PlusVerified { get; set; }

    /// <summary>When the flag was first set -- the first sighting, or the moderator's action.</summary>
    public DateTimeOffset? Is18PlusVerifiedAt { get; set; }

    /// <summary><c>vrchat</c> when a sync observed it; <c>manual</c> when a moderator set it.</summary>
    public string? Is18PlusVerifiedSource { get; set; }

    /// <summary>The Modbot account that set the flag by hand, when the source is <c>manual</c>.</summary>
    public Guid? Is18PlusVerifiedByUserId { get; set; }

    // ── When Modbot saw them, and when it last asked VRChat about them ────────────────────

    /// <summary>The first time this id appeared anywhere Modbot looks.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>
    /// The most recent time this person did something Modbot recorded -- joined, was banned, was
    /// seen in an instance. Drives refresh order: someone active a minute ago is refreshed before
    /// someone whose profile is merely old (user profile sync design §3.2).
    /// </summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// When the profile columns were last filled from VRChat. Null means never. Shown beside every
    /// profile field the UI renders, because a bio from March and a bio from an hour ago are not
    /// the same claim (foundation §4.2.5).
    /// </summary>
    public DateTimeOffset? LastRefreshedAt { get; set; }

    /// <summary>What went wrong the last time a refresh was attempted, or null if it succeeded.</summary>
    public string? RefreshError { get; set; }

    public DateTimeOffset? RefreshErrorAt { get; set; }

    /// <summary>
    /// Set when VRChat answered 404 for this id -- usually a deleted account. The row is kept,
    /// because the history that mentions them is still real, and the sync leaves them alone for
    /// a long while rather than asking every pass.
    /// </summary>
    public DateTimeOffset? NotFoundAt { get; set; }

    /// <summary>
    /// The user object as VRChat returned it on the last successful refresh, minus the fields
    /// Modbot must not keep (instance locations and the account's private note). Here so a
    /// question nobody has asked yet can be answered without another fetch.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? RawProfile { get; set; }
}

/// <summary>Where <see cref="VRChatUser.Is18PlusVerifiedSource"/> values come from.</summary>
public static class AgeVerificationSource
{
    /// <summary>A profile refresh saw VRChat report the user as 18+ verified.</summary>
    public const string VRChat = "vrchat";

    /// <summary>A moderator set or cleared the flag by hand.</summary>
    public const string Manual = "manual";
}
