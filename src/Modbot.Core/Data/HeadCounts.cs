using Modbot.Core.Data.Entities;

namespace Modbot.Core.Data;

/// <summary>
/// The one place a room's head count is set, so the room row and its change log cannot disagree.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two sources, and which one wins.</strong> The room's own page
/// (<c>GET /instances/{location}</c>) gives <c>n_users</c>, read about every thirty seconds by
/// <c>RoomHeadCountSync</c>. The group's list (<c>GET /groups/{groupId}/instances</c>) gives
/// <c>memberCount</c> every ten seconds. The page is preferred: in the one probe made,
/// <c>memberCount</c> read 2 while the room's page said 3 (research: vrchat-instance-findings.md
/// section 3), so the list's number may count group members only.
/// </para>
/// <para>
/// The list's number is used when the page has never been read, when the last read failed, or
/// when the last good read is older than <see cref="RoomReadGoesStaleAfter"/> -- for example
/// because <c>instances.read</c> is cold-stopped. A room is never shown without a count just
/// because its page could not be read.
/// </para>
/// </remarks>
public static class HeadCounts
{
    /// <summary>The head count came from the room's own page.</summary>
    public const string FromRoom = "room";

    /// <summary>The head count came from the group's instance list.</summary>
    public const string FromList = "list";

    /// <summary>
    /// How old a good page read may be before the group list's number takes over again. About four
    /// missed reads at the thirty-second rate.
    /// </summary>
    public static readonly TimeSpan RoomReadGoesStaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>The count to show for a room: the head count, or the list's number before there is one.</summary>
    public static int? Shown(VRChatInstance room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return room.HeadCount ?? room.LastUserCount;
    }

    /// <summary>Whether the group list's number should set the head count right now.</summary>
    public static bool ListMayUpdate(VRChatInstance room, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(room);

        return room.HeadCountSource != FromRoom
            || room.PageReadAt is not { } read
            || now - read > RoomReadGoesStaleAfter;
    }

    /// <summary>
    /// Sets a room's head count, and writes a change-log row when the number actually changed.
    /// </summary>
    /// <returns>True when a row was written.</returns>
    public static bool Record(
        ModbotContext db,
        VRChatInstance room,
        int headCount,
        string source,
        DateTimeOffset at,
        int? userCount = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(room);

        room.HeadCountSource = source;

        if (room.PeakUserCount is null || headCount > room.PeakUserCount)
            room.PeakUserCount = headCount;

        if (room.HeadCount == headCount)
            return false;

        room.HeadCount = headCount;

        db.InstanceHeadCounts.Add(new InstanceHeadCount
        {
            InstanceId = room.Id,
            CountedAt = at,
            HeadCount = headCount,
            UserCount = source == FromRoom ? userCount : null,
            MemberCount = room.LastUserCount,
            Source = source,
        });

        return true;
    }
}
