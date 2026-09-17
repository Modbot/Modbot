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
/// <param name="PeopleNow">How many were in it when Modbot last counted.</param>
/// <param name="PeakPeople">The most in it at once over its whole life.</param>
/// <param name="MinutesOpen">How long it ran, or has been running.</param>
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
    string? GroupAccessType,
    string? Region,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    string? ClosedBy,
    int? PeopleNow,
    int? PeakPeople,
    decimal MinutesOpen);

/// <param name="Opened">Instances opened per day (daily totals).</param>
/// <param name="Closed">Instances closed per day (daily totals).</param>
/// <param name="MostOpenAtOnce">
/// The most instances open at the same moment, per day. An instance with no close on record is
/// open until the last thing Modbot saw happen in it.
/// </param>
/// <param name="MostPeopleInOne">The most people known to be in a single instance at once, per day. From presence reports.</param>
/// <param name="TypicalMinutesOpen">Median time from open to close, for instances with both on record.</param>
/// <param name="InstancesWithBothEnds">How many instances that median is over.</param>
/// <param name="InstancesOpened">Instances opened inside the window.</param>
/// <param name="OpenNow">Instances the group has open right now, busiest first.</param>
/// <param name="Recent">The most recent instances in the window, newest first.</param>
public sealed record InstancesAnalytics(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<DayValue> Opened,
    IReadOnlyList<DayValue> Closed,
    IReadOnlyList<DayValue> MostOpenAtOnce,
    IReadOnlyList<DayValue> MostPeopleInOne,
    decimal? TypicalMinutesOpen,
    int InstancesWithBothEnds,
    int InstancesOpened,
    IReadOnlyList<InstanceRow> OpenNow,
    IReadOnlyList<InstanceRow> Recent,
    HourOfWeek HourOfWeek,
    AnalyticsCoverage Coverage,
    DateTimeOffset GeneratedAt);
