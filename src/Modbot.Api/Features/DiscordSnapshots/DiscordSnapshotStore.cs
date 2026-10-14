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
    /// Saves what the server looks like now. After a scheduled one is saved, the oldest scheduled ones
    /// past <see cref="DiscordSnapshotLimits.MostScheduledKept"/> are dropped; snapshots taken by hand
    /// are only ever removed by somebody deleting them.
    /// </summary>
    public static async Task<DiscordSnapshot> SaveAsync(
        ModbotContext db,
        IModbotClock clock,
        ServerSnapshotContent content,
        bool scheduled,
        string? takenBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(content);

        var snapshot = new DiscordSnapshot
        {
            TakenAt = clock.UtcNow,
            Scheduled = scheduled,
            TakenBy = scheduled ? null : takenBy,
            RoleCount = content.Roles.Count,
            ChannelCount = content.Channels.Count,
            Content = content.ToJson(),
        };

        db.DiscordSnapshots.Add(snapshot);

        if (scheduled)
        {
            var surplus = await db.DiscordSnapshots
                .Where(s => s.Scheduled)
                .OrderByDescending(s => s.TakenAt)
                .Skip(DiscordSnapshotLimits.MostScheduledKept - 1)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            db.DiscordSnapshots.RemoveRange(surplus);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return snapshot;
    }
}
