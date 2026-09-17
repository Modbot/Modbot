namespace Modbot.Api.Features.Members;

/// <summary>One role the group defines, as last read by the group-info producer.</summary>
public sealed record RoleOption(string Id, string? Name);

/// <summary>The Discord account a group member has linked.</summary>
/// <param name="Name">The name the server shows them by, else the Discord username saved with the link.</param>
/// <param name="AvatarUrl">Their picture in the server, when the bot has read the member list.</param>
/// <param name="InServer">In the Discord server now, by the stored member list.</param>
/// <param name="LeftAt">When they left the server, when the stored list has them as having left.</param>
public sealed record LinkedDiscordView(
    string UserId,
    string Name,
    string? AvatarUrl,
    bool InServer,
    DateTimeOffset? LeftAt);

/// <summary>
/// One row of the Members page.
/// </summary>
/// <param name="DisplayName">From the stored profile, when the profile sync has fetched one. Null until then.</param>
/// <param name="PlainName">The display name in plain letters, when that differs from the display name. Null otherwise.</param>
/// <param name="AvatarThumbnailUrl">The profile picture override when set, else the avatar thumbnail. Null until fetched.</param>
/// <param name="RoleNames">The role ids resolved against the group's roles; an id with no known name is shown as the id.</param>
/// <param name="JoinedAt">When VRChat says they joined. Exact, and VRChat's.</param>
/// <param name="EighteenPlus">Modbot's sticky "18+ verified" flag (user profile sync design §4).</param>
/// <param name="LastSeenAt">The most recent time this person did something Modbot recorded.</param>
/// <param name="ProfileRefreshedAt">When the profile columns were last fetched. Null means the name and picture are not known yet.</param>
/// <param name="LeftAt">Set when a full sweep no longer listed them. Null for a current member.</param>
/// <param name="LinkedDiscord">
/// Their linked Discord account. Null when they have not linked, and always null for a caller
/// without See profiles, who may not see links (Discord account linking design §11).
/// </param>
public sealed record MemberRow(
    string UserId,
    string? DisplayName,
    string? PlainName,
    string? AvatarThumbnailUrl,
    IReadOnlyList<string> RoleIds,
    IReadOnlyList<string> RoleNames,
    DateTimeOffset? JoinedAt,
    string? MembershipStatus,
    string? Visibility,
    bool IsRepresenting,
    bool EighteenPlus,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? ProfileRefreshedAt,
    DateTimeOffset? LeftAt,
    LinkedDiscordView? LinkedDiscord);

/// <summary>
/// How far the member list can be trusted right now.
/// </summary>
/// <param name="FirstSweepComplete">
/// False until the first full sweep has finished. Until then the list is partial -- however many
/// pages have been read -- and the screen says so rather than showing a short list as the group.
/// </param>
/// <param name="LastSyncedAt">When the last full sweep finished. What "last synced X ago" shows.</param>
/// <param name="SweepInProgress">True while a sweep is part-way through its pages.</param>
/// <param name="MemberCount">How many members the last full sweep listed.</param>
/// <param name="Now">The server's clock (spec 4.4), so ages are computed against it.</param>
public sealed record MemberListCoverage(
    bool FirstSweepComplete,
    DateTimeOffset? LastSyncedAt,
    bool SweepInProgress,
    int MemberCount,
    DateTimeOffset Now);

public sealed record MemberListResponse(
    IReadOnlyList<MemberRow> Members,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<RoleOption> Roles,
    MemberListCoverage Coverage);

/// <summary>One person's membership and ban standing, for the subject pane.</summary>
/// <param name="Known">False when no sweep has ever listed this person as a member. The membership fields are then null.</param>
/// <param name="Banned">True when the ban list currently holds them.</param>
public sealed record MembershipView(
    string UserId,
    bool Known,
    bool IsMember,
    IReadOnlyList<string> RoleIds,
    IReadOnlyList<string> RoleNames,
    DateTimeOffset? JoinedAt,
    string? MembershipStatus,
    string? Visibility,
    bool IsRepresenting,
    string? ManagerNotes,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LeftAt,
    bool Banned,
    DateTimeOffset? BannedAt,
    DateTimeOffset? BanLiftedAt,
    MemberListCoverage Members,
    BanListCoverage Bans);

/// <summary>One row of the group's ban list.</summary>
/// <param name="BannedAt">When VRChat says the ban was issued.</param>
/// <param name="FirstSeenAt">The first sweep that listed the ban -- for a ban older than Modbot, this is when Modbot learned of it.</param>
/// <param name="LiftedAt">Set when a full sweep no longer listed the ban.</param>
/// <param name="PlainName">The display name in plain letters, when that differs from the display name. Null otherwise.</param>
public sealed record BanRow(
    string UserId,
    string? DisplayName,
    string? PlainName,
    string? AvatarThumbnailUrl,
    DateTimeOffset? BannedAt,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset? LiftedAt,
    DateTimeOffset? ProfileRefreshedAt);

public sealed record BanListCoverage(
    bool FirstSweepComplete,
    DateTimeOffset? LastSyncedAt,
    bool SweepInProgress,
    int BanCount,
    DateTimeOffset Now);

public sealed record GroupBanListResponse(
    IReadOnlyList<BanRow> Bans,
    int Total,
    int Page,
    int PageSize,
    BanListCoverage Coverage);
