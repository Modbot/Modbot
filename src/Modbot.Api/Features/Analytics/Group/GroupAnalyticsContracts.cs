namespace Modbot.Api.Features.Analytics.Group;

/// <param name="Id">The role id VRChat uses. Opaque.</param>
/// <param name="IsModerationRole">Whether the role carries a permission that acts on other people (see <c>ModerationRoles</c>).</param>
/// <param name="Granted">Times this role was given to somebody inside the window, from the audit log.</param>
/// <param name="Revoked">Times it was taken away inside the window.</param>
public sealed record RoleSummary(
    string Id,
    string? Name,
    bool IsModerationRole,
    bool IsAddedOnJoin,
    bool IsSelfAssignable,
    decimal Granted,
    decimal Revoked);

/// <param name="Label">Plain words: "Under a week", "1 to 4 weeks", ...</param>
/// <param name="Members">Current members whose recorded join falls in this bucket.</param>
public sealed record TenureBucket(string Label, int MinDays, int? MaxDays, int Members);

/// <summary>
/// Whether invites turn into joins.
/// </summary>
/// <param name="InvitesSent">Invites sent inside the window (daily totals).</param>
/// <param name="JoinedAfterInvite">
/// Of those invites, how many were followed by that person joining within
/// <paramref name="FollowUpDays"/>. From the fact log, because it pairs two facts about one person.
/// </param>
/// <param name="RequestsReceived">People who asked to join inside the window (daily totals).</param>
/// <param name="RequestsApproved">Joins a moderator caused inside the window — see <c>moderator.approvals</c>.</param>
public sealed record InviteFunnel(
    decimal InvitesSent,
    int JoinedAfterInvite,
    int FollowUpDays,
    decimal RequestsReceived,
    decimal RequestsApproved,
    decimal RequestsRejected);

/// <param name="MemberCount">
/// Observed headcounts, from the group-info sync. Real numbers VRChat reported, not a running total
/// Modbot accumulated — see the note on <c>members.net</c> in <c>DailyTotalMetrics</c>.
/// </param>
/// <param name="NetChange">
/// Recorded joins minus recorded leaves, counted from zero on the fact log's first day. Not the
/// member count, and labelled as such wherever it is shown.
/// </param>
/// <param name="RolesKnownAt">When the role list was last read from VRChat, or null if never.</param>
/// <param name="MembersWithKnownTenure">
/// How many current members the tenure buckets cover. Only people whose join Modbot recorded
/// have a join date; members from before recording began are not in any bucket.
/// </param>
public sealed record GroupAnalytics(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<DayValue> MemberCount,
    IReadOnlyList<DayValue> Joined,
    IReadOnlyList<DayValue> Left,
    IReadOnlyList<DayValue> NetChange,
    IReadOnlyList<DayValue> InvitesSent,
    IReadOnlyList<DayValue> RequestsReceived,
    IReadOnlyList<RoleSummary> Roles,
    DateTimeOffset? RolesKnownAt,
    IReadOnlyList<TenureBucket> Tenure,
    int MembersWithKnownTenure,
    InviteFunnel Invites,
    AnalyticsCoverage Coverage,
    DateTimeOffset GeneratedAt);
