using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Api.Features.Audit;

namespace Modbot.Api.Features.Places;

/// <summary>
/// What the companion's presence reports say about a place.
/// </summary>
/// <remarks>
/// All four numbers are bounded by who was watching: they exist only while a moderator's client
/// was in the room. A busy world nobody with the client visited reads as nothing here, and every
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
/// <param name="Rooms">Rooms they were seen in, counted once each.</param>
/// <param name="Worlds">Worlds they were seen in, counted once each.</param>
/// <param name="Arrivals">Times they were seen arriving, or were already there when a client came in.</param>
public sealed record PersonCounts(
    decimal MinutesSeen,
    int Worlds,
    int Rooms,
    int Arrivals,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt)
{
    public static PersonCounts Nothing { get; } = new(0m, 0, 0, 0, null, null);
}

/// <summary>Somebody seen in one room, and for how long.</summary>
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
/// One world: its page as Modbot last read it, the rooms that have run in it, and what presence
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
/// <param name="Rooms">The most recent rooms in this world, newest first.</param>
/// <param name="RoomsTotal">How many rooms have ever run in it, however many are listed.</param>
/// <param name="RoomsOpenNow">How many of those are believed still open.</param>
/// <param name="VisitorsPerDay">Distinct people per day, from the daily totals.</param>
/// <param name="RoomsPerDay">Rooms opened per day, from the daily totals.</param>
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
    IReadOnlyList<InstanceRow> Rooms,
    int RoomsTotal,
    int RoomsOpenNow,
    IReadOnlyList<DayValue> VisitorsPerDay,
    IReadOnlyList<DayValue> RoomsPerDay,
    DateTimeOffset Now);

/// <summary>
/// One room, as it happened: where it was, when, how busy, who was in it and what happened there.
/// </summary>
/// <remarks>
/// <para>
/// Keyed on Modbot's own id rather than VRChat's number, because VRChat reissues numbers after a
/// room closes and two evenings under one number are two rooms (<c>VRChatInstance</c>). Both the
/// people and the log below are bounded to the stretch this room was open, for the same reason.
/// </para>
/// <para>
/// <paramref name="People"/> and <paramref name="Log"/> are empty, and
/// <paramref name="CanSeeWhoWasThere"/> false, for a caller without <c>ViewAuditLog</c>: who was
/// in a room and what was done to them is moderation history (spec 5.9.4), and the room's own
/// shape is not.
/// </para>
/// </remarks>
/// <param name="Room">The room row, the same shape the Instances page lists.</param>
/// <param name="Type">Public, group, friends or private, in VRChat's own words.</param>
/// <param name="LastSeenAt">The most recent moment the room was known to still exist.</param>
/// <param name="SeenInGroupList">
/// Whether the group's own live list has ever carried it. When true, the list is the authority on
/// when it ended; when false, it was judged finished only by having gone quiet.
/// </param>
/// <param name="Counts">What presence reports say about this room.</param>
/// <param name="LogTruncated">True when more facts happened here than the list carries.</param>
public sealed record InstanceView(
    InstanceRow Room,
    bool Known,
    string? WorldAuthorName,
    string? WorldImageUrl,
    int? WorldCapacity,
    string? Type,
    string? GroupId,
    DateTimeOffset LastSeenAt,
    bool SeenInGroupList,
    PlaceCounts Counts,
    bool CanSeeWhoWasThere,
    IReadOnlyList<PersonSeen> People,
    IReadOnlyList<AuditEntry> Log,
    bool LogTruncated,
    DateTimeOffset Now);

/// <summary>One person's presence figures, for the Metrics tab of their popup.</summary>
/// <param name="Known">False when no presence report has ever mentioned them.</param>
public sealed record PersonMetrics(
    string UserId,
    bool Known,
    PersonCounts Counts,
    IReadOnlyList<InstanceRow> RecentRooms,
    DateTimeOffset Now);
