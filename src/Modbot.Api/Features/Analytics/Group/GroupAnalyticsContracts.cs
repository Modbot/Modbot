using Modbot.Analytics.Activity;

namespace Modbot.Api.Features.Analytics.Group;

/// <summary>
/// The highest the group's two counts reached inside the window, and when.
/// </summary>
/// <remarks>
/// Both come from the readings the group-info sync keeps, which are VRChat's own numbers at the
/// moment it asked — so the peak is a number VRChat reported, not one Modbot worked out.
/// <paramref name="Coverage"/> says how many of the window's days carry a reading at all, because
/// the highest number in a window nobody was reading is the highest Modbot happened to see.
/// </remarks>
/// <param name="Members">The most members VRChat ever reported inside the window.</param>
/// <param name="Online">The most members online in VRChat at one moment inside the window.</param>
public sealed record MemberCountPeaks(PeakCount? Members, PeakCount? Online, MemberCountCoverage Coverage)
{
    /// <summary>No readings in the window: both peaks absent rather than nought.</summary>
    public static MemberCountPeaks Empty(int windowDays)
        => new(null, null, MemberCountCoverage.Nothing with { WindowDays = windowDays });
}

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

/// <summary>One reading of the group's counts, as VRChat reported them at <paramref name="At"/>.</summary>
/// <param name="Members">VRChat's <c>memberCount</c>.</param>
/// <param name="Online">VRChat's <c>onlineMemberCount</c>: members online in VRChat, anywhere.</param>
public sealed record MemberCountPoint(DateTimeOffset At, int Members, int Online);

/// <summary>
/// The member count chart: readings from <paramref name="From"/> to <paramref name="To"/>, at
/// most one per <paramref name="StepSeconds"/>.
/// </summary>
/// <param name="Range"><c>day</c>, <c>week</c>, <c>month</c> or <c>all</c>.</param>
/// <param name="StepSeconds">
/// The window was cut into steps this long and the last reading in each kept, so the series is
/// never more than about 500 points. A day's steps are shorter than the poll rate, so a day is
/// every reading.
/// </param>
public sealed record GroupMemberCountSeries(
    string Range,
    DateTimeOffset From,
    DateTimeOffset To,
    int StepSeconds,
    IReadOnlyList<MemberCountPoint> Points,
    DateTimeOffset GeneratedAt);

/// <param name="MemberCount">
/// Observed headcounts, from the group-info sync. Real numbers VRChat reported, not a running total
/// Modbot accumulated — see the note on <c>members.net</c> in <c>DailyTotalMetrics</c>. One per
/// day; the chart itself reads every reading through <c>GroupMemberCountSeries</c>.
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
/// <param name="Peaks">The highest the two counts reached inside the window, and when.</param>
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
    MemberCountPeaks Peaks,
    AnalyticsCoverage Coverage,
    DateTimeOffset GeneratedAt);
