namespace Modbot.Api.Features.Analytics.Team;

/// <param name="Metric">The daily total metric, verbatim, so a number can be traced to its row.</param>
/// <param name="Label">Plain words for the column header.</param>
/// <param name="Group">
/// <c>people</c> for an action on a person (instance kick, warn, ban, removal from the group, join
/// request turned away), <c>door</c> for door work and admin (invites, approvals, unbans, role
/// changes). The two are never added together (accountability signals design 3.4).
/// </param>
public sealed record ActionKind(string Metric, string Label, string Group);

/// <summary>The two kinds of moderation work the page keeps apart.</summary>
public static class ActionGroups
{
    public const string People = "people";
    public const string Door = "door";
}

/// <param name="OnPeople">Actions on people in the window: the number the reviews count.</param>
/// <param name="DoorAndAdmin">Invites, approvals, unbans and role changes in the window.</param>
/// <param name="ByKind">Count per <see cref="ActionKind.Metric"/>, for the kinds this person has any of.</param>
/// <param name="DaysActive">Days in the window this person did anything at all.</param>
/// <param name="OnPeoplePerDay">
/// Actions on people per day they took any, in the window; null with none. Measured the way the
/// reviews measure a moderator's usual, so the two can stand side by side.
/// </param>
/// <param name="UsualPerDay">
/// Their usual: actions on people per day they took any, over the reviews' baseline (90 days up to
/// yesterday). Null until the baseline has them.
/// </param>
/// <param name="LastActiveDay">The last day in the window this person did anything.</param>
public sealed record ModeratorSummary(
    Person Who,
    decimal OnPeople,
    decimal DoorAndAdmin,
    IReadOnlyDictionary<string, decimal> ByKind,
    int DaysActive,
    decimal? OnPeoplePerDay,
    decimal? UsualPerDay,
    DateOnly? LastActiveDay);

/// <summary>
/// The middle moderator for each number: half the moderators active in the window are at or
/// below it. A middle rather than an average, so one very busy moderator does not set what is
/// usual for everybody else.
/// </summary>
/// <param name="Moderators">How many moderators it is the middle of.</param>
/// <param name="OnPeoplePerDay">Over the moderators who took any action on people; null when none did.</param>
public sealed record TeamMiddle(
    int Moderators,
    decimal OnPeople,
    decimal DoorAndAdmin,
    decimal DaysActive,
    decimal? OnPeoplePerDay);

/// <summary>
/// Busy hours, by hour of the week: how many there were, and in how many nobody from the team was
/// on. 168 buckets each, Monday 00:00 UTC first; the page moves them to the viewer's clock.
/// </summary>
/// <remarks>
/// An hour is one clock hour on one date, so four Fridays in the window can give the Friday 20:00
/// bucket up to four busy hours. It is busy when a group instance held at least
/// <paramref name="People"/> people at some point in it, by VRChat's head count. Of those, an hour
/// has <em>nobody on</em> when a gap (a stretch no moderator was in that instance) overlapped it,
/// and is <em>not seen</em> when no companion reported from the busy instance during it, so Modbot
/// cannot tell either way. The rest had a moderator on.
/// </remarks>
/// <param name="People">The head count that makes an instance busy: the saved setting, or the one asked for.</param>
/// <param name="SavedPeople">The saved setting.</param>
public sealed record CoverWeek(
    int People,
    int SavedPeople,
    IReadOnlyList<int> Busy,
    IReadOnlyList<int> NobodyOn,
    IReadOnlyList<int> NotSeen);

/// <summary>
/// How long one kind of thing waited for somebody to decide it, over the decisions made in the window.
/// </summary>
/// <param name="Queue"><c>join-requests</c>, <c>flags</c> or <c>reviews</c>.</param>
/// <param name="Decided">How many were decided in the window.</param>
/// <param name="MiddleMinutes">The middle wait, in minutes; null when nothing was decided.</param>
/// <param name="MiddleMinutesPerDay">The middle wait of each UTC day's decisions; days with none are left out.</param>
public sealed record QueueWait(
    string Queue,
    int Decided,
    decimal? MiddleMinutes,
    IReadOnlyList<DayValue> MiddleMinutesPerDay);

/// <summary>The queues <see cref="QueueWait.Queue"/> names.</summary>
public static class Queues
{
    public const string JoinRequests = "join-requests";
    public const string Flags = "flags";
    public const string Reviews = "reviews";
}

/// <summary>
/// People acted on in the window, and how many of them had been acted on in the
/// <paramref name="Days"/> days before one of those actions.
/// </summary>
/// <remarks>
/// Only the kinds of action the repeat-offender rule counts (Settings, repeat offenders), so a
/// group that does not count instance kicks as strikes does not see them here either.
/// </remarks>
public sealed record ActedOnAgain(int People, int Again, int Days);

/// <summary>
/// Bans in the window, and how many of them were lifted within <paramref name="Days"/> days;
/// and, for the case files whose ban was lifted from Modbot in the window, the reasons given.
/// </summary>
/// <param name="Bans">Every ban in the window.</param>
/// <param name="OldEnough">
/// The bans in the window at least <paramref name="Days"/> days old: the ones that have had their
/// whole chance to be lifted, so the only ones <paramref name="LiftedWithin"/> is out of. A ban from
/// last week counted as "not lifted" would make the share look smaller than it is.
/// </param>
/// <param name="LiftedWithin">Of <paramref name="OldEnough"/>, those lifted within <paramref name="Days"/> days.</param>
/// <param name="Reasons">Each reason picked on a lift, most used first.</param>
/// <param name="LiftedWithoutReason">Case files lifted in the window with no reason picked.</param>
public sealed record BansLifted(
    int Bans,
    int OldEnough,
    int LiftedWithin,
    int Days,
    IReadOnlyList<LiftReason> Reasons,
    int LiftedWithoutReason);

public sealed record LiftReason(string Label, int Count);

public sealed record KindSeries(string Metric, string Label, decimal Total, IReadOnlyList<DayValue> Points);

/// <summary>
/// A stretch when people were in a group instance and no moderator was.
/// </summary>
/// <param name="WorldName">The world's name now, or null while Modbot has only seen its id.</param>
/// <param name="ModbotInstanceId">
/// Modbot's own id for the instance the gap happened in, which is what opens its popup. Null when
/// no instance Modbot has a row for was open under that number at the time.
/// </param>
/// <param name="InstanceName">The name the instance was opened with, when it has one.</param>
/// <param name="StartedAt">When the last moderator's presence ended.</param>
/// <param name="EndedAt">
/// When a moderator's presence resumed or the instance closed; null when neither was seen, in
/// which case nothing more is known about that instance after the gap began.
/// </param>
/// <param name="EndedBy"><c>moderator-arrived</c>, <c>instance-closed</c>, or <c>unknown</c>.</param>
/// <param name="PeopleWhenLastModeratorLeft">
/// How many people were known to be in the instance at that moment — from the last client's
/// reports, so it is a floor, not a count.
/// </param>
/// <param name="LastModerator">
/// Who left, when a presence fact names them; null when only the client's reporting stopped, and
/// always null for a caller who cannot see each moderator (<see cref="TeamAnalytics.CanSeeEachModerator"/>).
/// </param>
public sealed record CoverageGap(
    string WorldId,
    string InstanceId,
    string? WorldName,
    Guid? ModbotInstanceId,
    string? InstanceName,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string EndedBy,
    int PeopleWhenLastModeratorLeft,
    Person? LastModerator);

/// <param name="ModeratorsRecognised">How many people the moderator test currently matches.</param>
/// <param name="InstancesWatched">Instances in the window with at least one moderator presence report.</param>
/// <param name="InstancesOpenedWithoutAnyWatch">
/// The group's instances Modbot saw opened in the window (from its instance list) that no client
/// reported from while they ran. Nothing is known about who was in them, which is itself the finding.
/// </param>
/// <param name="Today">The window's last day when it is today by the server's clock, so not over yet.</param>
/// <param name="CanSeeEachModerator">
/// Whether the caller may see each moderator's own numbers and who left an instance last: the
/// people who can read the audit log, which already says who did what. Everybody else sees the
/// team's numbers and their own.
/// </param>
/// <param name="You">
/// The caller's own numbers, by the VRChat account linked to their Modbot account; null when none
/// is linked.
/// </param>
/// <param name="Moderators">Each moderator active in the window; empty unless <paramref name="CanSeeEachModerator"/>.</param>
/// <param name="ModeratorsActive">How many moderators took any action in the window.</param>
/// <param name="Middle">
/// The team's middle; null when fewer than <see cref="TeamAnalyticsQuery.LeastForMiddle"/>
/// moderators were active: with an odd count of three or fewer the middle is one person's own
/// figure, and with two, the middle and your own number give the other's away.
/// </param>
/// <param name="OnPeoplePerDay">Actions on people per day.</param>
/// <param name="DoorAndAdminPerDay">Door work and admin per day.</param>
/// <param name="DaysWithoutAuditLog">Days before Modbot began reading the group's audit log, which every action count comes from.</param>
public sealed record TeamAnalytics(
    DateOnly From,
    DateOnly To,
    DateOnly? Today,
    IReadOnlyList<ActionKind> Kinds,
    bool CanSeeEachModerator,
    ModeratorSummary? You,
    IReadOnlyList<ModeratorSummary> Moderators,
    int ModeratorsActive,
    TeamMiddle? Middle,
    IReadOnlyList<DayValue> OnPeoplePerDay,
    IReadOnlyList<DayValue> DoorAndAdminPerDay,
    IReadOnlyList<KindSeries> ActionsPerDayByKind,
    IReadOnlyList<CoverageGap> CoverageGaps,
    CoverWeek Cover,
    IReadOnlyList<QueueWait> Waits,
    ActedOnAgain ActedOnAgain,
    BansLifted BansLifted,
    int ModeratorsRecognised,
    int InstancesWatched,
    int InstancesOpenedWithoutAnyWatch,
    IReadOnlyList<DateOnly> DaysWithoutAuditLog,
    AnalyticsCoverage Coverage,
    DateTimeOffset GeneratedAt);

/// <param name="People">How many people in one group instance mean it wants a moderator, from 1 to 100.</param>
public sealed record SetCoverPeopleRequest(int People);

/// <param name="People">The saved setting.</param>
public sealed record CoverPeopleView(int People);

/// <summary>Who is asking, as far as the Moderation tab needs to know.</summary>
/// <param name="CanSeeEachModerator">Holds the audit log permission.</param>
/// <param name="VRChatUserId">The VRChat account linked to their Modbot account, if any.</param>
public sealed record TeamViewer(bool CanSeeEachModerator, string? VRChatUserId);
