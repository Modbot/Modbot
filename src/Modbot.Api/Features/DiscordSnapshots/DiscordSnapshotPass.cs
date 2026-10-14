using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.DiscordSnapshots;

/// <summary>
/// Saves a snapshot of the Discord server when the schedule says one is due.
/// </summary>
/// <remarks>
/// Due means the schedule is not off and the newest snapshot, however it was taken, is older than one
/// period, or there is none. With the bot offline nothing is tried, so the next pass tries again.
/// Times come from <see cref="IModbotClock"/>.
/// </remarks>
public sealed class DiscordSnapshotPass
{
    /// <summary>How long a claim holds before another Modbot may take over from one that stopped.</summary>
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IDiscordSnapshotActions _discord;
    private readonly ILogger _log;

    public DiscordSnapshotPass(ModbotContext db, IModbotClock clock, IDiscordSnapshotActions discord, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(discord);

        _db = db;
        _clock = clock;
        _discord = discord;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>Takes a snapshot if one is due. Returns whether it did.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        var schedule = settings.DiscordSnapshotSchedule;
        if (schedule == DiscordSnapshotSchedules.Off || !await IsDueAsync(schedule, ct).ConfigureAwait(false))
            return false;

        // Two Modbots on one database can both see a snapshot due. The one that sets the claim first
        // takes it; the other leaves it. A claim older than the lease is from a Modbot that stopped.
        var now = _clock.UtcNow;
        var stale = now - ClaimLease;

        var claimed = await _db.Settings
            .Where(s => s.Id == settings.Id && (s.DiscordSnapshotClaimedAt == null || s.DiscordSnapshotClaimedAt < stale))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.DiscordSnapshotClaimedAt, now), ct)
            .ConfigureAwait(false);

        if (claimed == 0)
            return false;

        try
        {
            // The other Modbot may have saved one between the first look and the claim.
            if (!await IsDueAsync(schedule, ct).ConfigureAwait(false))
                return false;

            var read = await _discord.ReadAsync(ct).ConfigureAwait(false);
            if (read.Structure is not { } structure)
            {
                if (!read.BotOffline)
                    _log.Warning("A scheduled server snapshot could not be taken: {Error}", read.Error);

                return false;
            }

            if (await DiscordSnapshotStore.SaveScheduledAsync(_db, _clock, structure.Content, ct).ConfigureAwait(false) is null)
            {
                _log.Warning(
                    "A scheduled server snapshot was not taken: {Most} snapshots are kept and all are taken by hand. Delete one.",
                    DiscordSnapshotLimits.MostKept);

                return false;
            }

            return true;
        }
        finally
        {
            await _db.Settings
                .Where(s => s.Id == settings.Id && s.DiscordSnapshotClaimedAt == now)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.DiscordSnapshotClaimedAt, (DateTimeOffset?)null), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>No snapshot yet, or the newest one, however it was taken, is older than one period.</summary>
    private async Task<bool> IsDueAsync(string schedule, CancellationToken ct)
    {
        var last = await _db.DiscordSnapshots
            .AsNoTracking()
            .OrderByDescending(s => s.TakenAt)
            .Select(s => (DateTimeOffset?)s.TakenAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return last is not { } taken
            || DiscordSnapshotSchedules.NextAfter(schedule, taken) is not { } due
            || _clock.UtcNow >= due;
    }
}
