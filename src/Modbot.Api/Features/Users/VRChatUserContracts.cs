namespace Modbot.Api.Features.Users;

/// <summary>
/// The "18+ verified" flag as Modbot remembers it -- not as VRChat shows it today.
/// </summary>
/// <param name="Verified">
/// Sticky. True once a refresh has ever seen the person as 18+ verified, or a moderator has said
/// so; cleared only by a moderator (user profile sync design §4).
/// </param>
/// <param name="Since">When it was set, by whichever of the two set it.</param>
/// <param name="Source"><c>vrchat</c> or <c>manual</c>.</param>
/// <param name="SetByUsername">The Modbot account that set or cleared it by hand, when one did.</param>
public sealed record AgeVerifiedFlag(
    bool Verified,
    DateTimeOffset? Since,
    string? Source,
    Guid? SetByUserId,
    string? SetByUsername);

/// <summary>
/// Whether a refresh of this person is waiting, running, or blocked.
/// </summary>
/// <param name="Pending">Queued or in flight. The screen shows "refreshing…" while this is true.</param>
/// <param name="Reason">Why they are queued -- <c>SeenInInstance</c>, <c>OpenedInModbot</c>, and so on.</param>
/// <param name="Blocked">
/// A sentence when nothing will be fetched for a while -- the users lane is cold-stopped, or the
/// sync is not running in this process. Null when a refresh can be expected.
/// </param>
public sealed record RefreshState(
    bool Pending,
    string? Reason,
    DateTimeOffset? RequestedAt,
    bool InProgress,
    string? Blocked);

/// <summary>
/// Everything Modbot has stored about one VRChat user, with the age of every bit of it.
/// </summary>
/// <remarks>
/// Spec 4.2.5: freshness is visible, never implied. <see cref="LastRefreshedAt"/> travels with
/// every profile field and <see cref="Stale"/> says whether it is older than the sync's own idea
/// of old, so a screen cannot show a bio without also showing when that bio was true.
/// </remarks>
/// <param name="Known">False when Modbot has no row for this id at all. Every other field is then empty.</param>
/// <param name="AgeVerificationStatusLastSeen">
/// VRChat's own status as of the last refresh -- <c>18+</c>, <c>hidden</c>, <c>verified</c>. This
/// can differ from <see cref="EighteenPlus"/>: hidden today does not undo verified yesterday.
/// </param>
/// <param name="Stale">True when the profile is older than <paramref name="StaleAfterSeconds"/>, or was never fetched.</param>
/// <param name="NotFoundAt">Set when VRChat answered 404 -- usually a deleted account.</param>
/// <param name="Now">The server's clock, so ages are computed against the right one (spec 4.4).</param>
public sealed record VRChatUserProfile(
    string UserId,
    bool Known,
    string? DisplayName,
    string? Bio,
    string? Status,
    string? StatusDescription,
    string? Pronouns,
    string? AvatarImageUrl,
    string? AvatarThumbnailUrl,
    string? ProfilePictureUrl,
    DateOnly? DateJoined,
    IReadOnlyList<string> Tags,
    string? LastPlatform,
    string? AgeVerificationStatusLastSeen,
    bool? AgeVerifiedLastSeen,
    AgeVerifiedFlag EighteenPlus,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastRefreshedAt,
    bool Stale,
    double StaleAfterSeconds,
    string? RefreshError,
    DateTimeOffset? RefreshErrorAt,
    DateTimeOffset? NotFoundAt,
    RefreshState Refresh,
    DateTimeOffset Now);

/// <summary>What became of a request to refresh somebody now.</summary>
/// <param name="Outcome">
/// <c>Queued</c>, <c>Promoted</c>, <c>AlreadyQueued</c>, <c>FreshEnough</c>, or <c>NotAvailable</c>
/// when no profile sync runs in this process.
/// </param>
/// <param name="LastRefreshedAt">For a <c>FreshEnough</c> answer: how fresh.</param>
/// <param name="Explanation">A sentence for the screen.</param>
public sealed record RefreshRequestResult(string Outcome, DateTimeOffset? LastRefreshedAt, string Explanation);

/// <param name="Verified">The value to set. Clearing is the decision that needs a reason most.</param>
/// <param name="Reason">Optional, kept with the fact in the moderator's own words.</param>
public sealed record SetAgeVerifiedRequest(bool Verified, string? Reason = null);
