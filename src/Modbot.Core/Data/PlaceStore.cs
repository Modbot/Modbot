using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Core.Data;

/// <summary>
/// Keeps the record of places -- which worlds Modbot has seen anybody in, and which instances have
/// been open.
/// </summary>
/// <remarks>
/// <para>
/// Two callers put things in: the group instance sync, which polls the instances the managed group
/// has open, and the client event handler, which reports an instance a moderator is standing in. They
/// arrive by different routes and can describe the same instance, so everything here is written to be
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
    /// Records that somebody was at a location, and answers which instance that was.
    /// </summary>
    /// <param name="location">The location string as VRChat wrote it, qualifiers and all.</param>
    /// <param name="seenAt">When the sighting happened -- not when it was reported.</param>
    /// <param name="userCount">How many people were there, when that is known.</param>
    /// <param name="fromGroupList">
    /// True when this sighting came from the managed group's own live instance list. That makes
    /// the list the authority on when this instance ends, and takes it out of the time rule's reach.
    /// </param>
    /// <returns>The instance, whether it was already known or has just been opened.</returns>
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

        // Instances still believed open, plus any the group's list closed recently enough to still be
        // the same instance coming back (InstanceIdentity.ReopensWithin). Almost never more than one.
        var reopenFrom = seenAt - InstanceIdentity.ReopensWithin;

        // Loaded into the context, then read back from what the context holds. The query brings in
        // saved rows; Local also holds an instance added earlier in this same pass and not yet saved,
        // which a query cannot see -- and missing it would open the same instance twice.
        await _db.VRChatInstances
            .Where(i => i.Location == location
                && (i.ClosedAt == null || i.ClosedAt > reopenFrom))
            .LoadAsync(ct).ConfigureAwait(false);

        var candidates = _db.VRChatInstances.Local
            .Where(i => i.Location == location
                && (i.ClosedAt == null || i.ClosedAt > reopenFrom))
            .ToList();

        var instance = InstanceIdentity.Match(candidates, seenAt);

        if (instance is { ClosedAt: not null })
        {
            // It was never a different instance: the list stopped carrying it and then carried it
            // again. Undoing the close is the honest record, and leaves one continuous session
            // rather than two halves with a hole between them.
            instance.ClosedAt = null;
            instance.ClosedBy = null;
        }

        if (instance is null)
        {
            instance = new VRChatInstance
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

            _db.VRChatInstances.Add(instance);
        }

        // A sighting that happened before what is already recorded is ordinary -- a client sends
        // its backlog on reconnect -- and must not drag the instance's last-seen time backwards.
        if (seenAt > instance.LastSeenAt)
            instance.LastSeenAt = seenAt;

        if (seenAt < instance.OpenedAt)
            instance.OpenedAt = seenAt;

        if (fromGroupList)
            instance.SeenInGroupList = true;

        if (userCount is { } count)
        {
            instance.LastUserCount = count;

            if (instance.PeakUserCount is null || count > instance.PeakUserCount)
                instance.PeakUserCount = count;
        }

        return instance;
    }

    /// <summary>
    /// Marks an instance finished.
    /// </summary>
    /// <param name="closedBy">
    /// <c>list</c> when the group's live list stopped carrying it, <c>time</c> when nothing had
    /// been seen for <see cref="VRChatInstance.CountsAsNewAfter"/>. Recorded because the two are
    /// not equally trustworthy.
    /// </param>
    public static void Close(VRChatInstance instance, DateTimeOffset closedAt, string closedBy)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.ClosedAt is not null)
            return;

        // An instance cannot end before it was last seen in: if the two disagree, the sighting is the
        // thing that actually happened.
        instance.ClosedAt = closedAt < instance.LastSeenAt ? instance.LastSeenAt : closedAt;
        instance.ClosedBy = closedBy;
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
        // instance was in a world Modbot had not saved yet. Find checks what the context already holds,
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
    /// Closes instances nobody has seen for <see cref="VRChatInstance.CountsAsNewAfter"/>, so that the
    /// same instance number showing up again opens a new one.
    /// </summary>
    /// <remarks>
    /// Instances the group's live list carries are left alone: the list ends those exactly, and a
    /// quiet group instance is still a real one. This only reaches instances Modbot learned about from a
    /// moderator standing in them, where there is no signal but time.
    /// </remarks>
    /// <returns>How many instances were closed.</returns>
    public async Task<int> CloseLongQuietInstancesAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var cutoff = now - VRChatInstance.CountsAsNewAfter;

        var stale = await _db.VRChatInstances
            .Where(i => i.ClosedAt == null && !i.SeenInGroupList && i.LastSeenAt < cutoff)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var instance in stale)
            Close(instance, instance.LastSeenAt, "time");

        return stale.Count;
    }
}
