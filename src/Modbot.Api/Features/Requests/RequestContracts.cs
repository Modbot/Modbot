using Modbot.Core.Users;

namespace Modbot.Api.Features.Requests;

/// <summary>
/// One person waiting to be let into the group.
/// </summary>
/// <param name="UserId">VRChat's id for them. Opaque, taken as sent (foundation §3.1.1).</param>
/// <param name="DisplayName">
/// Their name as VRChat sent it with the request, falling back to the stored profile. Null when
/// neither has one.
/// </param>
/// <param name="PlainName">The display name in plain letters, when that differs from it.</param>
/// <param name="AvatarThumbnailUrl">Their picture, from the stored profile. Null until it has been fetched.</param>
/// <param name="TrustRank">The VRChat trust rank as last read. Null until the profile's tags are known.</param>
/// <param name="EighteenPlus">Modbot's sticky "18+ verified" mark.</param>
/// <param name="AskedAt">When VRChat says they asked.</param>
/// <param name="Banned">True when the group's ban list holds them right now.</param>
/// <param name="BannedBefore">
/// True when Modbot has a ban on record for them that has since been lifted. A request from
/// somebody the group has thrown out before is not the same request as anybody else's.
/// </param>
/// <param name="WasMember">True when Modbot has them as a member who left, or was removed.</param>
/// <param name="LeftAt">When a sweep last stopped listing them as a member.</param>
/// <param name="Known">
/// False when Modbot has never recorded anything about this person: no profile, no membership, no
/// ban. Everything above is then whatever VRChat sent with the request, and nothing more.
/// </param>
public sealed record JoinRequestRow(
    string UserId,
    string? DisplayName,
    string? PlainName,
    string? AvatarThumbnailUrl,
    TrustRank? TrustRank,
    bool EighteenPlus,
    DateTimeOffset? AskedAt,
    bool Banned,
    bool BannedBefore,
    bool WasMember,
    DateTimeOffset? LeftAt,
    bool Known);

/// <summary>
/// One page of the join queue, as VRChat had it a moment ago.
/// </summary>
/// <param name="Requests">The page, in VRChat's own order.</param>
/// <param name="Page">Which numbered page this is, from 1.</param>
/// <param name="PageSize">How many were asked for.</param>
/// <param name="HasMore">
/// True when VRChat filled the page, so there is probably another. VRChat does not send a total
/// for this list, so this is what a next-page control has to go on.
/// </param>
/// <param name="ReadAt">
/// When Modbot asked VRChat, on Modbot's clock. This list is read live and never stored, so how
/// old it is, is how long the screen has been open.
/// </param>
public sealed record JoinRequestList(
    IReadOnlyList<JoinRequestRow> Requests,
    int Page,
    int PageSize,
    bool HasMore,
    DateTimeOffset ReadAt);

/// <summary>What a moderator asked Modbot to do about one join request.</summary>
/// <param name="UserId">The person whose request it is.</param>
/// <param name="Key">
/// The key the browser made for this confirmation. Every press carries the same one and only the
/// first does anything, the same guarantee a kick or a ban has (M4 §4.3).
/// </param>
/// <param name="ReasonIds">
/// Reasons from the group's list. Optional, unless the group has switched on requiring one — a
/// rejection is not a ban, and most of them have nothing to say.
/// </param>
/// <param name="Note">The moderator's own words. Always optional.</param>
public sealed record JoinRequestAnswerRequest(
    string UserId,
    string Key,
    IReadOnlyList<Guid>? ReasonIds = null,
    string? Note = null);
