using Modbot.Core.Users;

namespace Modbot.Api.Features.People;

/// <summary>
/// One row of the People page: somebody Modbot has a record of, member or not.
/// </summary>
/// <param name="DisplayName">From the stored profile, when the profile sync has fetched one. Null until then.</param>
/// <param name="PlainName">The display name in plain letters, when that differs from the display name. Null otherwise.</param>
/// <param name="AvatarThumbnailUrl">The profile picture override when set, else the avatar thumbnail. Null until fetched.</param>
/// <param name="TrustRank">The VRChat trust rank as last read. Null until the profile's tags are known.</param>
/// <param name="EighteenPlus">Modbot's sticky "18+ verified" flag (user profile sync design §4).</param>
/// <param name="IsMember">A member of the group right now, by the last sweep.</param>
/// <param name="LeftAt">When a full sweep stopped listing them as a member. Null for a current member and for somebody who was never one.</param>
/// <param name="Banned">The group's ban list holds them right now.</param>
/// <param name="FirstSeenAt">The first time this id appeared anywhere Modbot looks. How long they have been known for.</param>
/// <param name="LastSeenAt">The most recent time this person did something Modbot recorded.</param>
/// <param name="ProfileRefreshedAt">When the profile columns were last fetched. Null means the name and picture are not known yet.</param>
/// <param name="NotFoundAt">Set when VRChat has no account with this id.</param>
public sealed record PersonRow(
    string UserId,
    string? DisplayName,
    string? PlainName,
    string? AvatarThumbnailUrl,
    TrustRank? TrustRank,
    bool EighteenPlus,
    bool IsMember,
    DateTimeOffset? LeftAt,
    bool Banned,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ProfileRefreshedAt,
    DateTimeOffset? NotFoundAt);

/// <summary>
/// How many people Modbot knows about in total, beside however many the filters left.
/// </summary>
/// <param name="Known">Every row in the table, whatever the filters say.</param>
/// <param name="Members">How many of them are members of the group right now.</param>
/// <param name="Now">The server's clock (spec 4.4), so ages are computed against it.</param>
public sealed record PeopleCoverage(int Known, int Members, DateTimeOffset Now);

public sealed record PeopleListResponse(
    IReadOnlyList<PersonRow> People,
    int Total,
    int Page,
    int PageSize,
    PeopleCoverage Coverage);
