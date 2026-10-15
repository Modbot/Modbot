using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Calendar;

/// <summary>
/// The worlds a list holds. A list a person made holds the rows saved for it; the Seen list
/// (<see cref="WorldList.SeenId"/>) holds every world Modbot has seen, worked out each time it is asked,
/// so there is nothing stored to edit or to go stale.
/// </summary>
public static class WorldListContents
{
    /// <summary>
    /// The worlds worth offering: not one VRChat refused without ever being read, and not an id with
    /// no name that nothing has confirmed.
    /// </summary>
    public static IQueryable<VRChatWorld> NotJunk(this IQueryable<VRChatWorld> worlds)
    {
        ArgumentNullException.ThrowIfNull(worlds);

        return worlds.Where(w => w.LastRefreshedAt != null || (w.RefreshError == null && w.Name != null));
    }

    /// <summary>The worlds in a list, in the order they are shown.</summary>
    /// <remarks>Seen worlds fit any number of players.</remarks>
    public static async Task<List<WorldListItem>> ItemsAsync(ModbotContext db, Guid listId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (listId != WorldList.SeenId)
        {
            return await db.WorldListItems.AsNoTracking()
                .Where(i => i.ListId == listId)
                .OrderBy(i => i.Position)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var ids = await db.VRChatWorlds.AsNoTracking()
            .NotJunk()
            .OrderBy(w => w.Name)
            .ThenBy(w => w.WorldId)
            .Select(w => w.WorldId)
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. ids.Select((id, position) => new WorldListItem { ListId = listId, WorldId = id, Position = position })];
    }

    /// <summary>Which of the lists hold at least one world.</summary>
    public static async Task<List<Guid>> FilledAsync(ModbotContext db, IReadOnlyCollection<Guid> listIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(listIds);

        var filled = await db.WorldListItems.AsNoTracking()
            .Where(i => listIds.Contains(i.ListId))
            .Select(i => i.ListId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        if (listIds.Contains(WorldList.SeenId) && await db.VRChatWorlds.AsNoTracking().NotJunk().AnyAsync(ct).ConfigureAwait(false))
            filled.Add(WorldList.SeenId);

        return filled;
    }
}
