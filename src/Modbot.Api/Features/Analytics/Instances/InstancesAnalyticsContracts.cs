using Modbot.Analytics.Activity;

namespace Modbot.Api.Features.Analytics.Instances;

/// <summary>
/// 168 buckets, one per hour of the week, in UTC. Index = (Monday = 0 … Sunday = 6) × 24 + hour.
/// </summary>
/// <param name="Arrivals">People seen arriving in a group instance, from client presence reports.</param>
/// <param name="Opened">Group instances opened, from the audit log.</param>
public sealed record HourOfWeek(IReadOnlyList<decimal> Arrivals, IReadOnlyList<decimal> Opened);

/// <summary>
/// One instance, as it happened: where it was, when, and how busy.
/// </summary>
/// <remarks>
/// <para>
/// The counts and charts on this page answer "is the community active"; this answers "what
/// actually ran last night", which is the question a moderator opening the page usually has.
/// It comes from <c>vrchat_instance</c> rather than from the fact log, because that table is
/// where an instance's own identity lives -- VRChat reissues instance numbers, so the fact log's
/// <c>(world_id, instance_id)</c> pair cannot tell two evenings apart and this can.
/// </para>
/// <para>
/// <paramref name="WorldName"/> is null while a world has only ever been seen as an id, which is
/// ordinary for a few minutes after a new world turns up and permanent for a private one.
/// </para>
/// </remarks>
/// <param name="Id">Modbot's own id for the instance, which is what makes it one instance.</param>
/// <param name="VRChatInstanceId">VRChat's number for it -- what a moderator sees in game.</param>
/// <param name="InstanceName">
/// The name the instance was opened with, when it was given one. Screens show it in place of the
/// number. Null for an instance with no name.
/// </param>
/// <param name="PeopleNow">How many were in it when Modbot last counted.</param>
/// <param name="PeakPeople">The most in it at once over its whole life.</param>
/// <param name="MinutesOpen">How long it ran, or has been running.</param>
/// <param name="WorldCapacity">
/// How many people the world holds, as its page says, for "25/40" the way the game shows it. Null
/// until Modbot has read the world. Never a limit: exemptions raise real capacity above it.
/// </param>
/// <param name="WorldPlatforms">
/// The platforms the world has a build for, in VRChat's words, for the game's PC, Android and iOS
/// badges. Null until Modbot has read the world's builds.
/// </param>
/// <param name="ClosedBy">
/// <c>list</c> when the group's live list stopped carrying it -- exact -- or <c>time</c> when it
/// simply went quiet for long enough. Null while it is still open. The two are not equally
/// trustworthy and the screen says which it has.
/// </param>
public sealed record InstanceRow(
    Guid Id,
    string Location,
    string WorldId,
    string? WorldName,
    string? WorldThumbnailImageUrl,
    string? VRChatInstanceId,
    string? InstanceName,
    string? GroupAccessType,
    string? Region,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    string? ClosedBy,
    int? PeopleNow,
    int? PeakPeople,
    decimal MinutesOpen,
    int? WorldCapacity = null,
    IReadOnlyList<string>? WorldPlatforms = null);

/// <summary>
/// The instance that held the most people at one moment inside the window.
/// </summary>
/// <param name="Id">Modbot's own id, so the row opens the instance popup.</param>
/// <param name="WorldName">Null while the world has only ever been seen as an id.</param>
/// <param name="VRChatInstanceId">VRChat's number for it — what a moderator saw in game.</param>
/// <param name="People">How many were in it at that moment, as VRChat's own count reported.</param>
/// <param name="At">The moment. The earliest one, where the instance reached that number twice.</param>
public sealed record BusiestInstance(
    Guid Id,
    string WorldId,
    string? WorldName,
    string? VRChatInstanceId,
    DateTimeOffset OpenedAt,
    int People,
    DateTimeOffset At);

/// <summary>
/// How full the group's instances ever got inside the window, and when.
/// </summary>
/// <remarks>
/// Every number here comes from VRChat's own head counts rather than from the companion's presence
/// reports, so it covers instances no moderator was standing in. <paramref name="Coverage"/> says
/// how much of the window Modbot had a count for, because a peak from two hours of counting in a
/// week is not the week's peak.
/// </remarks>
/// <param name="MostPeopleAtOnce">The most people in the group's instances at one moment, all added together.</param>
/// <param name="MostInstancesAtOnce">The most instances being counted at one moment.</param>
/// <param name="BusiestDay">The day with the most people-minutes.</param>
/// <param name="BusiestHour">The single clock hour with the most people-minutes.</param>
/// <param name="BusiestInstance">The fullest one instance ever got.</param>
/// <param name="MostPeopleAtOncePerDay">The daily peak, as a line.</param>
/// <param name="PeopleMinutesPerDay">People-minutes per day, as a line.</param>
public sealed record InstancePeaks(
    PeakCount? MostPeopleAtOnce,
    PeakCount? MostInstancesAtOnce,
    BusiestDay? BusiestDay,
    BusiestHour? BusiestHour,
    BusiestInstance? BusiestInstance,
    IReadOnlyList<DayValue> MostPeopleAtOncePerDay,
    IReadOnlyList<DayValue> PeopleMinutesPerDay,
    InstanceCoverage Coverage)
{
    /// <summary>Nothing recorded: every peak absent rather than nought (see <c>Peaks</c>).</summary>
    public static InstancePeaks Empty(int windowDays) => new(
        null, null, null, null, null, [], [],
        InstanceCoverage.Nothing with { WindowDays = windowDays });
}

/// <summary>One moment of the staircase: how many people were in the group's instances, and in how many.</summary>
/// <param name="Instances">Instances with a head count at that moment — not instances open, which the page's own charts answer.</param>
public sealed record ActivityPoint(DateTimeOffset At, int People, int Instances);

/// <summary>
/// People in the group's instances over a range of readings, thinned to a drawable number of points.
/// </summary>
/// <param name="Range"><c>day</c>, <c>week</c>, <c>month</c> or <c>all</c>.</param>
/// <param name="StepSeconds">
/// The window was cut into steps this long and the last reading in each kept, so the series is never
/// more than about five hundred points.
/// </param>
/// <param name="DaysWithoutHeadCounts">
/// UTC days the window touches on which a group instance was open and Modbot had no head count for
/// any of that time. A day nothing was open is quiet, not missing, and is not in here.
/// </param>
public sealed record InstanceActivitySeries(
    string Range,
    DateTimeOffset From,
    DateTimeOffset To,
    int StepSeconds,
    IReadOnlyList<ActivityPoint> Points,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<DateOnly> DaysWithoutHeadCounts);

/// <param name="Opened">Instances opened per day (daily totals).</param>
/// <param name="Closed">Instances closed per day (daily totals).</param>
/// <param name="MostOpenAtOnce">
/// The most instances open at the same moment, per day. An instance with no close on record is
/// open until the last thing Modbot saw happen in it.
/// </param>
/// <param name="MostPeopleInOne">The most people known to be in a single instance at once, per day. From presence reports.</param>
/// <param name="TypicalMinutesOpen">Median time from open to close, for instances with both on record.</param>
/// <param name="TypicalMinutesOpenPerDay">
/// The same median, day by day, over instances that opened and closed on that day. One number for a
/// window says whether evenings are long; a line says whether they are getting longer.
/// </param>
/// <param name="InstancesWithBothEnds">How many instances that median is over.</param>
/// <param name="InstancesOpened">Instances opened inside the window.</param>
/// <param name="OpenNow">Instances the group has open right now, busiest first.</param>
/// <param name="Recent">The most recent instances in the window, newest first.</param>
/// <param name="Peaks">How full it ever got, and how much of the window Modbot was counting.</param>
/// <param name="PresenceReports">
/// How many presence facts the window holds. The heatmap and <paramref name="MostPeopleInOne"/> rest
/// on these, so the page marks them thin below a handful — the same test the Worlds page applies.
/// </param>
/// <param name="Today">The window's last day when it is today by the server's clock, so not over yet.</param>
/// <param name="DaysWithoutAuditLog">
/// Days before Modbot began reading the group's audit log, which openings, closings and how long
/// instances stayed open come from.
/// </param>
/// <param name="DaysWithoutHeadCounts">Days an instance was open and never counted (see <see cref="InstanceActivitySeries"/>).</param>
/// <param name="DaysWithoutPresenceReports">Days an instance was open and no companion reported from any.</param>
public sealed record InstancesAnalytics(
    DateOnly From,
    DateOnly To,
    DateOnly? Today,
    IReadOnlyList<DayValue> Opened,
    IReadOnlyList<DayValue> Closed,
    IReadOnlyList<DayValue> MostOpenAtOnce,
    IReadOnlyList<DayValue> MostPeopleInOne,
    decimal? TypicalMinutesOpen,
    IReadOnlyList<DayValue> TypicalMinutesOpenPerDay,
    int InstancesWithBothEnds,
    int InstancesOpened,
    IReadOnlyList<InstanceRow> OpenNow,
    IReadOnlyList<InstanceRow> Recent,
    HourOfWeek HourOfWeek,
    InstancePeaks Peaks,
    long PresenceReports,
    IReadOnlyList<DateOnly> DaysWithoutAuditLog,
    IReadOnlyList<DateOnly> DaysWithoutHeadCounts,
    IReadOnlyList<DateOnly> DaysWithoutPresenceReports,
    AnalyticsCoverage Coverage,
    DateTimeOffset GeneratedAt);
