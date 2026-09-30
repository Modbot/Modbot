using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Api.Features.Audit;

namespace Modbot.Api.Features.Places;

/// <summary>
/// What the companion's presence reports say about a place.
/// </summary>
/// <remarks>
/// All four numbers are bounded by who was watching: they exist only while a moderator's client
/// was in the instance. A busy world nobody with the client visited reads as nothing here, and every
/// screen that shows these says so rather than letting a zero pass as a measurement.
/// </remarks>
/// <param name="MinutesSeen">People-time: summed across everybody, not wall-clock.</param>
/// <param name="Visitors">Distinct people seen.</param>
/// <param name="Arrivals">Somebody entering, or already there when a client arrived.</param>
public sealed record PlaceCounts(
    decimal MinutesSeen,
    int Visitors,
    int Arrivals,
    DateTimeOffset? LastSeenAt)
{
    /// <summary>Nothing was ever seen here. Not an error: nobody with the client was watching.</summary>
    public static PlaceCounts Nothing { get; } = new(0m, 0, 0, null);
}

/// <summary>
/// One person's own presence figures, over all of recorded history.
/// </summary>
/// <param name="Instances">Instances they were seen in, counted once each.</param>
/// <param name="Worlds">Worlds they were seen in, counted once each.</param>
/// <param name="Arrivals">Times they were seen arriving, or were already there when a client came in.</param>
public sealed record PersonCounts(
    decimal MinutesSeen,
    int Worlds,
    int Instances,
    int Arrivals,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt)
{
    public static PersonCounts Nothing { get; } = new(0m, 0, 0, 0, null, null);
}

/// <summary>Somebody seen in one instance, and for how long.</summary>
/// <param name="DisplayName">
/// The name stored for them, or null when Modbot has only ever had the id. Never substituted with
/// the id dressed up as a name.
/// </param>
public sealed record PersonSeen(
    string UserId,
    string? DisplayName,
    decimal MinutesSeen,
    int Arrivals,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <summary>
/// One world: its page as Modbot last read it, the instances that have run in it, and what presence
/// reports say about it.
/// </summary>
/// <remarks>
/// <para>
/// Every field comes from Modbot's own tables. Nothing here reaches VRChat — the world page is
/// read once by the world sweep when the id is first seen, and this endpoint shows what that
/// read stored (<c>vrchat_world</c>).
/// </para>
/// <para>
/// <strong>A world with no name is ordinary.</strong> <paramref name="Known"/> is false when
/// Modbot has only ever seen the id, and <paramref name="Name"/> is null when the row exists but
/// the page has not been read yet — a private or deleted world never gets a name at all. The
/// screen shows the id and says it has not been read yet.
/// </para>
/// </remarks>
/// <param name="Known">False when there is no row for this id at all.</param>
/// <param name="LastReadAt">When the world page was last read. Null means never.</param>
/// <param name="ReadError">Why the last read failed, in VRChat's words. Usually a private or deleted world.</param>
/// <param name="Capacity">
/// What the page said the world holds. Never treated as a limit Modbot enforces — exemptions
/// raise real capacity above it (foundation section 3.1).
/// </param>
/// <param name="Instances">The most recent instances in this world, newest first.</param>
/// <param name="InstancesTotal">How many instances have ever run in it, however many are listed.</param>
/// <param name="InstancesOpenNow">How many of those are believed still open.</param>
/// <param name="VisitorsPerDay">Distinct people per day, from the daily totals.</param>
/// <param name="InstancesPerDay">Instances opened per day, from the daily totals.</param>
public sealed record WorldView(
    string WorldId,
    bool Known,
    string? Name,
    string? Description,
    string? AuthorId,
    string? AuthorName,
    string? ImageUrl,
    string? ThumbnailImageUrl,
    int? Capacity,
    int? RecommendedCapacity,
    IReadOnlyList<string> Tags,
    string? ReleaseStatus,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastReadAt,
    string? ReadError,
    PlaceCounts Counts,
    IReadOnlyList<InstanceRow> Instances,
    int InstancesTotal,
    int InstancesOpenNow,
    IReadOnlyList<DayValue> VisitorsPerDay,
    IReadOnlyList<DayValue> InstancesPerDay,
    DateTimeOffset Now);

/// <summary>
/// One instance, as it happened: where it was, when, how busy, who was in it and what happened there.
/// </summary>
/// <remarks>
/// <para>
/// Keyed on Modbot's own id rather than VRChat's number, because VRChat reissues numbers after a
/// instance closes and two evenings under one number are two instances (<c>VRChatInstance</c>). Both the
/// people and the log below are bounded to the stretch this instance was open, for the same reason.
/// </para>
/// <para>
/// <paramref name="People"/> and <paramref name="Log"/> are empty, and
/// <paramref name="CanSeeWhoWasThere"/> false, for a caller without <c>ViewAuditLog</c>: who was
/// in an instance and what was done to them is moderation history (spec 5.9.4), and the instance's own
/// shape is not.
/// </para>
/// </remarks>
/// <param name="Instance">The instance row, the same shape the Instances page lists.</param>
/// <param name="Type">Public, group, friends or private, in VRChat's own words.</param>
/// <param name="LastSeenAt">The most recent moment the instance was known to still exist.</param>
/// <param name="SeenInGroupList">
/// Whether the group's own live list has ever carried it. When true, the list is the authority on
/// when it ended; when false, it was judged finished only by having gone quiet.
/// </param>
/// <param name="Counts">What presence reports say about this instance.</param>
/// <param name="ReturningMembers">
/// How many of the people seen here are members of the managed group now: the popup's "Returning
/// members", beside every join (<see cref="PlaceCounts.Arrivals"/>) and the different people who
/// made them (<see cref="PlaceCounts.Visitors"/>). Membership is read as the member list last
/// showed it, not as it stood on the night, because that is the question a moderator asks: of the
/// people who came, how many are ours. Zero, like the people, for a caller without <c>ViewAuditLog</c>.
/// </param>
/// <param name="LogTruncated">True when more facts happened here than the list carries.</param>
/// <param name="HeadCounts">
/// How many people were in it, each time the count changed, oldest first: the popup's "people over
/// time". The most recent <see cref="HeadCountPoint.Most"/> changes when there were more. Not
/// about who was there, so it is shown to everyone who may open the instance.
/// </param>
public sealed record InstanceView(
    InstanceRow Instance,
    bool Known,
    string? WorldAuthorName,
    string? WorldImageUrl,
    int? WorldCapacity,
    string? Type,
    string? GroupId,
    DateTimeOffset LastSeenAt,
    bool SeenInGroupList,
    PlaceCounts Counts,
    int ReturningMembers,
    bool CanSeeWhoWasThere,
    IReadOnlyList<PersonSeen> People,
    IReadOnlyList<AuditEntry> Log,
    bool LogTruncated,
    DateTimeOffset Now,
    IReadOnlyList<HeadCountPoint> HeadCounts);

/// <summary>An instance's head count from one moment until it next changed.</summary>
/// <param name="At">When the count was read.</param>
/// <param name="People">How many were in it: the instance page's count, or the group list's before that was read.</param>
/// <param name="UserCount">
/// The instance page's <c>userCount</c>, which <paramref name="People"/> is taken from. Null when the
/// reading came from the group list, or when the page had none and <paramref name="People"/> is
/// <paramref name="NUsers"/>.
/// </param>
/// <param name="MemberCount">The group list's count of group members in it at the time.</param>
/// <param name="Source"><c>page</c> or <c>list</c>: which read the reading came from.</param>
/// <param name="NUsers">
/// The instance page's <c>n_users</c>, kept beside <paramref name="UserCount"/>. Null for a list reading.
/// </param>
/// <param name="Unsure">
/// True when <paramref name="People"/> is <paramref name="NUsers"/> because the page had no
/// <c>userCount</c>. Shown as "80?".
/// </param>
public sealed record HeadCountPoint(
    DateTimeOffset At,
    int People,
    int? UserCount,
    int? MemberCount,
    string Source,
    int? NUsers = null,
    bool Unsure = false)
{
    /// <summary>How many changes one popup carries. A day's busy instance changes about this often.</summary>
    public const int Most = 2000;
}

/// <summary>One person's presence figures, for the Metrics tab of their popup.</summary>
/// <param name="Known">False when no presence report has ever mentioned them.</param>
public sealed record PersonMetrics(
    string UserId,
    bool Known,
    PersonCounts Counts,
    IReadOnlyList<InstanceRow> RecentInstances,
    DateTimeOffset Now);

/// <summary>
/// How one instance compared with the other instances in its world while it was open, from the
/// world's page as Modbot read it every two minutes (<c>world_head_count</c>).
/// </summary>
/// <remarks>
/// <para>
/// Empty for an instance that closed before the world was being read, which is every instance
/// before 2026-09-27, and for stretches when <c>worlds.read</c> was cold-stopped. The readings were
/// taken while the instance was open; nothing here waits on VRChat.
/// </para>
/// <para>
/// The other instances' names and their groups' names are the one thing asked for when this is
/// read: once each, in the background, for an instance or group Modbot has never asked about and
/// whose location does not say outsiders cannot join. The answer is kept, so every later read has
/// it without asking; <see cref="NamesComing"/> says one is still on its way.
/// </para>
/// <para>
/// The other instances' numbers are the world page's list, and so is this instance's whenever the
/// list carries it, so a rank compares like with like. When the list leaves it out -- VRChat may
/// list only instances anyone can see -- its own head count at that moment stands in, and
/// <see cref="WorldReading.Listed"/> is false.
/// </para>
/// </remarks>
/// <param name="From">When the instance opened.</param>
/// <param name="To">When it closed, or now while it is open.</param>
/// <param name="Readings">Every read of the world's page while it was open, oldest first.</param>
/// <param name="Others">
/// The busiest of the other instances seen in the world while it was open, busiest first: at most
/// <see cref="InstanceWorldView.OthersShown"/>.
/// </param>
/// <param name="OthersTotal">How many other instances the world's list carried at any read, however many are shown.</param>
/// <param name="AtPeak">The read at which this instance held the most people, first such read on a tie.</param>
/// <param name="BusiestMinutes">
/// How long it held more people than every other instance in the world, from one read to the next,
/// each read counting for at most <see cref="InstanceWorldView.ReadingCoversAtMost"/>.
/// </param>
/// <param name="Truncated">True when there were more reads than <see cref="InstanceWorldView.ReadingsMost"/>; the newest are kept.</param>
/// <param name="NamesComing">
/// True when VRChat is being asked for the name of a shown instance or its group, so asking again in
/// a few seconds may name it.
/// </param>
public sealed record InstanceWorldView(
    Guid InstanceId,
    string WorldId,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<WorldReading> Readings,
    IReadOnlyList<OtherInstance> Others,
    int OthersTotal,
    WorldReading? AtPeak,
    decimal BusiestMinutes,
    bool Truncated,
    bool NamesComing = false)
{
    /// <summary>How many other instances are shown. Enough to see the busy ones; the rest are a crowd.</summary>
    public const int OthersShown = 8;

    /// <summary>How many reads one answer carries: about two and a half days at one every two minutes.</summary>
    public const int ReadingsMost = 1800;

    /// <summary>
    /// The longest one read speaks for. Two and a half reads' worth, so a missed read or a cold stop
    /// is a gap rather than a stretch in which nothing was known being counted as busiest.
    /// </summary>
    public static readonly TimeSpan ReadingCoversAtMost = TimeSpan.FromMinutes(5);
}

/// <summary>One read of the world's page, and where this instance stood in it.</summary>
/// <param name="Occupants">The page's <c>occupants</c>: everyone in the world. Null when the body had none.</param>
/// <param name="People">
/// This instance's head count at the read: the world list's number when it carries the instance,
/// otherwise its own head count at that moment. Null when neither is known.
/// </param>
/// <param name="Unsure">True when <paramref name="People"/> is its own head count and that count is unsure ("80?").</param>
/// <param name="Listed">Whether the world's list carried this instance at this read.</param>
/// <param name="Rank">1 for the busiest instance in the world at this read. Null when <paramref name="People"/> is.</param>
/// <param name="Of">How many instances were ranked: the list's, with this one counted once.</param>
public sealed record WorldReading(
    DateTimeOffset At,
    int? Occupants,
    int? PublicOccupants,
    int? PrivateOccupants,
    int? People,
    bool Unsure,
    bool Listed,
    int? Rank,
    int Of);

/// <summary>Another instance in the same world, as the world's list showed it.</summary>
/// <param name="InstanceId">The instance id exactly as the list carried it, qualifiers and all.</param>
/// <param name="Number">VRChat's number: the part before the first <c>~</c>.</param>
/// <param name="OwnGroup">True when it belongs to the managed group.</param>
/// <param name="ModbotInstanceId">Modbot's own id for it when Modbot has a row for it, so a screen can open it.</param>
/// <param name="Name">
/// The name it was opened with, when it had one: from Modbot's own row for one of the group's
/// instances, and from the one time VRChat was asked for anybody else's.
/// </param>
/// <param name="GroupName">
/// Its group's name, for another group's instance, once VRChat has been asked. Null for the managed
/// group's own instances, whose name the screen already has.
/// </param>
/// <param name="Peak">The most people the list showed in it while this instance was open.</param>
/// <param name="Readings">Its head count at each read that carried it, oldest first.</param>
public sealed record OtherInstance(
    string InstanceId,
    string? Number,
    string? GroupId,
    string? GroupAccessType,
    string? Region,
    bool OwnGroup,
    Guid? ModbotInstanceId,
    string? Name,
    string? GroupName,
    int Peak,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<OtherReading> Readings);

/// <summary>Another instance's head count at one read of the world's page.</summary>
public sealed record OtherReading(DateTimeOffset At, int People);
