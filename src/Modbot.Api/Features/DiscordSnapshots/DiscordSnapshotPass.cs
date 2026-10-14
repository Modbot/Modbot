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
        if (settings.DiscordSnapshotSchedule == DiscordSnapshotSchedules.Off)
            return false;

        var last = await _db.DiscordSnapshots
            .AsNoTracking()
            .OrderByDescending(s => s.TakenAt)
            .Select(s => (DateTimeOffset?)s.TakenAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (last is { } taken
            && DiscordSnapshotSchedules.NextAfter(settings.DiscordSnapshotSchedule, taken) is { } due
            && _clock.UtcNow < due)
        {
            return false;
        }

        var read = await _discord.ReadAsync(ct).ConfigureAwait(false);
        if (read.Structure is not { } structure)
        {
            if (!read.BotOffline)
                _log.Warning("A scheduled server snapshot could not be taken: {Error}", read.Error);

            return false;
        }

        await DiscordSnapshotStore.SaveAsync(_db, _clock, structure.Content, scheduled: true, takenBy: null, ct)
            .ConfigureAwait(false);

        return true;
    }
}
