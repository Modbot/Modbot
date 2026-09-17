using System.ComponentModel.DataAnnotations.Schema;
using Modbot.Core.Users;

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
    /// The trust rank the tag list says (<see cref="TrustRanks.FromTags"/>). Null until the user
    /// read has filled <see cref="Tags"/>, because a person nobody has read yet is not a Visitor,
    /// they are unknown.
    /// </summary>
    /// <remarks>
    /// Written whenever <see cref="Tags"/> is, and from nothing else: the public profile's
    /// <c>trustTags</c> is a smaller list that may lack the nuisance and staff tags, and a rank
    /// that flipped between the two calls would write a change fact for a change nobody made
    /// (research: <c>2026-09-16-vrchat-trust-ranks.md</c> §4).
    /// </remarks>
    public TrustRank? TrustRank { get; set; }

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
    /// When the public profile was last read. Null means never. Shown beside every profile field
    /// the UI renders, because a bio from March and a bio from an hour ago are not the same claim
    /// (foundation §4.2.5).
    /// </summary>
    /// <remarks>
    /// The public profile is the main read, so this is the freshness the card reports. The join
    /// date, the tag list and the status line come from the rarer user read and carry
    /// <see cref="LastUserReadAt"/> instead.
    /// </remarks>
    public DateTimeOffset? LastRefreshedAt { get; set; }

    /// <summary>What went wrong the last time the public profile was read, or null if it worked.</summary>
    public string? RefreshError { get; set; }

    public DateTimeOffset? RefreshErrorAt { get; set; }

    /// <summary>The public profile answered 404 for this id. See <see cref="NotFoundAt"/>.</summary>
    public DateTimeOffset? ProfileNotFoundAt { get; set; }

    // ── The rarer read: the full user object ─────────────────────────────────────────────

    /// <summary>When the full user object was last read. Null means never.</summary>
    /// <remarks>
    /// Read about once a week per person rather than on the profile schedule: what it carries
    /// alone is the join date (which never changes), the tag list and the status line (which move
    /// slowly), and the avatar pictures (which nothing decides anything from). Research:
    /// <c>vrchat-public-profile-findings.md</c>.
    /// </remarks>
    public DateTimeOffset? LastUserReadAt { get; set; }

    /// <summary>What went wrong the last time the user object was read, or null if it worked.</summary>
    public string? UserReadError { get; set; }

    public DateTimeOffset? UserReadErrorAt { get; set; }

    /// <summary>The user object answered 404 for this id. See <see cref="NotFoundAt"/>.</summary>
    public DateTimeOffset? UserNotFoundAt { get; set; }

    /// <summary>
    /// Set when VRChat has no account with this id -- usually a deleted account. The row is kept,
    /// because the history that mentions them is still real, and the sync leaves them alone for
    /// a long while rather than asking every pass.
    /// </summary>
    /// <remarks>
    /// Modbot asks two different calls about a person, and one of them answering 404 while the
    /// other still works does not mean the account is gone. So this is set only when
    /// <see cref="ProfileNotFoundAt"/> and <see cref="UserNotFoundAt"/> are both set, or when one
    /// of them is set and the other call has never succeeded for this person. A success on either
    /// call clears that call's own mark, and so clears this.
    /// </remarks>
    public DateTimeOffset? NotFoundAt { get; set; }

    /// <summary>
    /// The user object as VRChat returned it on the last successful user read, minus the fields
    /// Modbot must not keep (instance locations and the account's private note). Here so a
    /// question nobody has asked yet can be answered without another fetch.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? RawProfile { get; set; }

    /// <summary>The public profile as VRChat returned it on the last successful profile read.</summary>
    /// <remarks>
    /// Its own column rather than sharing <see cref="RawProfile"/>, so neither call erases the
    /// other's copy. It is also the only place the fields the public profile carries alone -- the
    /// trust tags, the languages, the group being represented -- are kept.
    /// </remarks>
    [Column(TypeName = "jsonb")]
    public string? RawPublicProfile { get; set; }
}

/// <summary>Where <see cref="VRChatUser.Is18PlusVerifiedSource"/> values come from.</summary>
public static class AgeVerificationSource
{
    /// <summary>A profile refresh saw VRChat report the user as 18+ verified.</summary>
    public const string VRChat = "vrchat";

    /// <summary>A moderator set or cleared the flag by hand.</summary>
    public const string Manual = "manual";
}
