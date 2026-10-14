using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordSnapshots;

/// <summary>Saving a snapshot, for the button and for the schedule alike.</summary>
public static class DiscordSnapshotStore
{
    /// <summary>
    /// Saves what the server looks like now, taken by hand. The caller has checked there is room
    /// (<see cref="DiscordSnapshotLimits.MostKept"/>); snapshots taken by hand are only ever removed
    /// by somebody deleting them.
    /// </summary>
    public static async Task<DiscordSnapshot> SaveAsync(
        ModbotContext db, IModbotClock clock, ServerSnapshotContent content, string? takenBy, CancellationToken ct)
    {
        var snapshot = Build(clock, content, scheduled: false, takenBy);
        db.DiscordSnapshots.Add(snapshot);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>
    /// Saves what the server looks like now, taken by the schedule. The oldest scheduled ones past
    /// <see cref="DiscordSnapshotLimits.MostScheduledKept"/> are dropped, and so are more of them if
    /// that is what it takes to stay within <see cref="DiscordSnapshotLimits.MostKept"/>. Null when
    /// the snapshots left are all taken by hand and the total is already full: nothing is saved.
    /// </summary>
    public static async Task<DiscordSnapshot?> SaveScheduledAsync(
        ModbotContext db, IModbotClock clock, ServerSnapshotContent content, CancellationToken ct)
    {
        var scheduled = await db.DiscordSnapshots
            .Where(s => s.Scheduled)
            .OrderByDescending(s => s.TakenAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var total = await db.DiscordSnapshots.CountAsync(ct).ConfigureAwait(false);

        // The new one counts as one of the scheduled ones kept; the oldest go first.
        var keep = scheduled.Count;
        keep = Math.Min(keep, DiscordSnapshotLimits.MostScheduledKept - 1);

        while (keep > 0 && total - (scheduled.Count - keep) + 1 > DiscordSnapshotLimits.MostKept)
            keep--;

        var drop = scheduled.Skip(keep).ToList();

        if (total - drop.Count + 1 > DiscordSnapshotLimits.MostKept)
            return null;

        db.DiscordSnapshots.RemoveRange(drop);

        var snapshot = Build(clock, content, scheduled: true, takenBy: null);
        db.DiscordSnapshots.Add(snapshot);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return snapshot;
    }

    private static DiscordSnapshot Build(IModbotClock clock, ServerSnapshotContent content, bool scheduled, string? takenBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(content);

        return new DiscordSnapshot
        {
            TakenAt = clock.UtcNow,
            Scheduled = scheduled,
            TakenBy = takenBy,
            RoleCount = content.Roles.Count,
            ChannelCount = content.Channels.Count,
            Content = content.ToJson(),
        };
    }
}
