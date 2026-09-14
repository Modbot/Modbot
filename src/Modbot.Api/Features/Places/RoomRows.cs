using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Places;

/// <summary>
/// Turns <c>vrchat_instance</c> rows into the room rows every screen lists.
/// </summary>
/// <remarks>
/// <para>
/// One projection, because four screens now show the same row — the Instances page's "open right
/// now" and "recent" lists, the rooms in a world, and the rooms one person was seen in. They must
/// agree about how long a room has been open and what its world is called, and four copies of
/// this arithmetic would eventually not.
/// </para>
/// <para>
/// The world's name is looked up once for the whole page rather than once per row. A world Modbot
/// has only ever seen as an id has no name here and the screen shows the id, which is the truth
/// about what is known.
/// </para>
/// </remarks>
public static class RoomRows
{
    /// <summary>
    /// Reads a page of rooms and names their worlds.
    /// </summary>
    /// <param name="rooms">Already filtered, ordered and limited by the caller.</param>
    /// <param name="now">From <c>IModbotClock</c>. An open room's length is measured against it.</param>
    public static async Task<IReadOnlyList<InstanceRow>> ReadAsync(
        ModbotContext db,
        IQueryable<VRChatInstance> rooms,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rooms);

        var page = await rooms
            .Select(i => new
            {
                i.Id,
                i.Location,
                i.WorldId,
                i.VRChatInstanceId,
                i.GroupAccessType,
                i.Region,
                i.OpenedAt,
                i.ClosedAt,
                i.ClosedBy,
                i.LastUserCount,
                i.PeakUserCount,
                i.LastSeenAt,
            })
            .ToListAsync(ct);

        var worldIds = page.Select(r => r.WorldId).Distinct(StringComparer.Ordinal).ToList();

        var named = await db.VRChatWorlds
            .AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId))
            .Select(w => new { w.WorldId, w.Name, w.ThumbnailImageUrl })
            .ToDictionaryAsync(w => w.WorldId, w => w, StringComparer.Ordinal, ct);

        return page
            .Select(r =>
            {
                var world = named.GetValueOrDefault(r.WorldId);

                // An open room counts to now; a closed one to when it closed. Never past `now`,
                // because a clock that disagrees with a stored time should not produce a room
                // that has been open for minus four minutes.
                var until = r.ClosedAt ?? now;
                var minutes = until <= r.OpenedAt ? 0m : (decimal)(until - r.OpenedAt).TotalMinutes;

                return new InstanceRow(
                    r.Id,
                    r.Location,
                    r.WorldId,
                    world?.Name,
                    world?.ThumbnailImageUrl,
                    r.VRChatInstanceId,
                    r.GroupAccessType,
                    r.Region,
                    r.OpenedAt,
                    r.ClosedAt,
                    r.ClosedBy,
                    r.LastUserCount,
                    r.PeakUserCount,
                    Math.Round(minutes, 1));
            })
            .ToList();
    }
}
