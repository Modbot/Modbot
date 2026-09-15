using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Core.Data;

/// <summary>
/// Keeps the record of places -- which worlds Modbot has seen anybody in, and which rooms have
/// been open.
/// </summary>
/// <remarks>
/// <para>
/// Two callers put things in: the group instance sync, which polls the rooms the managed group
/// has open, and the client event handler, which reports a room a moderator is standing in. They
/// arrive by different routes and can describe the same room, so everything here is written to be
/// safe to call again with the same facts.
/// </para>
/// <para>
/// <strong>Nothing here calls VRChat.</strong> A world this has never heard of gets a row holding
/// its id and nothing else; filling in the name is the world sweep's job, and until it runs the
/// UI shows the id. That keeps the expensive, rate-limited part out of the path a presence report
/// takes, which happens thousands of times an hour.
/// </para>
/// </remarks>
public sealed class PlaceStore
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public PlaceStore(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Records that somebody was at a location, and answers which room that was.
    /// </summary>
    /// <param name="location">The location string as VRChat wrote it, qualifiers and all.</param>
    /// <param name="seenAt">When the sighting happened -- not when it was reported.</param>
    /// <param name="userCount">How many people were there, when that is known.</param>
    /// <param name="fromGroupList">
    /// True when this sighting came from the managed group's own live instance list. That makes
    /// the list the authority on when this room ends, and takes it out of the time rule's reach.
    /// </param>
    /// <returns>The room, whether it was already known or has just been opened.</returns>
    public async Task<VRChatInstance?> RecordSightingAsync(
        string? location,
        DateTimeOffset seenAt,
        int? userCount = null,
        bool fromGroupList = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;

        var parts = InstanceLocationParts.Split(location);
        if (parts.WorldId is null)
            return null;

        await NoteWorldSeenAsync(parts.WorldId, seenAt, ct).ConfigureAwait(false);

        // Rooms still believed open, plus any the group's list closed recently enough to still be
        // the same room coming back (InstanceIdentity.ReopensWithin). Almost never more than one.
        var reopenFrom = seenAt - InstanceIdentity.ReopensWithin;

        // Loaded into the context, then read back from what the context holds. The query brings in
        // saved rows; Local also holds a room added earlier in this same pass and not yet saved,
        // which a query cannot see -- and missing it would open the same room twice.
        await _db.VRChatInstances
            .Where(i => i.Location == location
                && (i.ClosedAt == null || i.ClosedAt > reopenFrom))
            .LoadAsync(ct).ConfigureAwait(false);

        var candidates = _db.VRChatInstances.Local
            .Where(i => i.Location == location
                && (i.ClosedAt == null || i.ClosedAt > reopenFrom))
            .ToList();

        var room = InstanceIdentity.Match(candidates, seenAt);

        if (room is { ClosedAt: not null })
        {
            // It was never a different room: the list stopped carrying it and then carried it
            // again. Undoing the close is the honest record, and leaves one continuous session
            // rather than two halves with a hole between them.
            room.ClosedAt = null;
            room.ClosedBy = null;
        }

        if (room is null)
        {
            room = new VRChatInstance
            {
                Id = Guid.CreateVersion7(),
                Location = location,
                WorldId = parts.WorldId,
                VRChatInstanceId = parts.InstanceId,
                GroupId = parts.GroupId,
                Type = parts.Type,
                GroupAccessType = parts.GroupAccessType,
                Region = parts.Region,
                OpenedAt = seenAt,
                LastSeenAt = seenAt,
            };

            _db.VRChatInstances.Add(room);
        }

        // A sighting that happened before what is already recorded is ordinary -- a client sends
        // its backlog on reconnect -- and must not drag the room's last-seen time backwards.
        if (seenAt > room.LastSeenAt)
            room.LastSeenAt = seenAt;

        if (seenAt < room.OpenedAt)
            room.OpenedAt = seenAt;

        if (fromGroupList)
            room.SeenInGroupList = true;

        if (userCount is { } count)
        {
            room.LastUserCount = count;

            if (room.PeakUserCount is null || count > room.PeakUserCount)
                room.PeakUserCount = count;
        }

        return room;
    }

    /// <summary>
    /// Marks a room finished.
    /// </summary>
    /// <param name="closedBy">
    /// <c>list</c> when the group's live list stopped carrying it, <c>time</c> when nothing had
    /// been seen for <see cref="VRChatInstance.CountsAsNewAfter"/>. Recorded because the two are
    /// not equally trustworthy.
    /// </param>
    public static void Close(VRChatInstance room, DateTimeOffset closedAt, string closedBy)
    {
        ArgumentNullException.ThrowIfNull(room);

        if (room.ClosedAt is not null)
            return;

        // A room cannot end before it was last seen in: if the two disagree, the sighting is the
        // thing that actually happened.
        room.ClosedAt = closedAt < room.LastSeenAt ? room.LastSeenAt : closedAt;
        room.ClosedBy = closedBy;
    }

    /// <summary>
    /// Makes sure a world has a row, creating a placeholder holding only its id when it does not.
    /// </summary>
    /// <remarks>
    /// The placeholder is what the world sweep looks for. Creating it here rather than making the
    /// sweep discover worlds from the fact log means a world is queued for a name the moment it
    /// is first seen, without anything scanning a table of millions of rows to find out.
    /// </remarks>
    public async Task NoteWorldSeenAsync(string worldId, DateTimeOffset seenAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(worldId))
            return;

        // FindAsync, not a query. A query only sees saved rows, so a world added earlier in the same
        // pass looked missing, was added a second time, and EF Core refused to track the copy --
        // which failed the whole group instance poll, every ten seconds, for as long as any open
        // room was in a world Modbot had not saved yet. Find checks what the context already holds,
        // added rows included, before it asks the database.
        var world = await _db.VRChatWorlds.FindAsync([worldId], ct).ConfigureAwait(false);

        if (world is null)
        {
            _db.VRChatWorlds.Add(new VRChatWorld
            {
                WorldId = worldId,
                FirstSeenAt = seenAt,
                LastSeenAt = seenAt,
            });

            return;
        }

        if (seenAt > world.LastSeenAt)
            world.LastSeenAt = seenAt;

        if (seenAt < world.FirstSeenAt)
            world.FirstSeenAt = seenAt;
    }

    /// <summary>
    /// Closes rooms nobody has seen for <see cref="VRChatInstance.CountsAsNewAfter"/>, so that the
    /// same instance number showing up again opens a new one.
    /// </summary>
    /// <remarks>
    /// Rooms the group's live list carries are left alone: the list ends those exactly, and a
    /// quiet group room is still a real one. This only reaches rooms Modbot learned about from a
    /// moderator standing in them, where there is no signal but time.
    /// </remarks>
    /// <returns>How many rooms were closed.</returns>
    public async Task<int> CloseLongQuietRoomsAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var cutoff = now - VRChatInstance.CountsAsNewAfter;

        var stale = await _db.VRChatInstances
            .Where(i => i.ClosedAt == null && !i.SeenInGroupList && i.LastSeenAt < cutoff)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var room in stale)
            Close(room, room.LastSeenAt, "time");

        return stale.Count;
    }
}
