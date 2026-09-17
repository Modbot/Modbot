using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Puts names to worlds that have only ever been seen as an id.
/// </summary>
/// <remarks>
/// <para>
/// The group's own worlds are named for free by the instance poll, which returns the world
/// attached to each open instance. This exists for the rest: a world a moderator wandered into, and a
/// world that only ever appeared in an audit entry. Those never touch the group's instance list,
/// so without this they would stay as ids forever.
/// </para>
/// <para>
/// <strong>A world is read once and then left alone.</strong> World names change so rarely that
/// polling for one would spend a real budget on an answer that is almost always the same, so the
/// queue here is simply "rows that have never been read", and once a group has settled it is
/// empty nearly all the time. That is why this class is absent from the scheduled totals the
/// settings screen shows: its cap is one request per second, but its duty cycle is close to zero.
/// </para>
/// <para>
/// <strong>A world that cannot be read is not a fault.</strong> Private and deleted worlds are
/// ordinary. The failure is written to the row so the sweep does not ask again every pass, and
/// the UI goes on showing the id, which is the truth about what is known.
/// </para>
/// </remarks>
public sealed class WorldSync
{
    /// <summary>
    /// How many worlds one pass will read. The budget is one per second (measured), so a pass
    /// takes at most this many seconds and then gets out of the way.
    /// </summary>
    public const int WorldsPerPass = 10;

    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public WorldSync(IVRChatGate gate, ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _gate = gate;
        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<WorldSyncRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        // Oldest sighting first, so the world somebody has been looking at an id for longest is
        // the one that gets a name first.
        var waiting = await _db.VRChatWorlds
            .Where(w => w.LastRefreshedAt == null && w.RefreshError == null)
            .OrderBy(w => w.FirstSeenAt)
            .Take(WorldsPerPass)
            .ToListAsync(ct).ConfigureAwait(false);

        if (waiting.Count == 0)
        {
            await RecordPollAsync(ct).ConfigureAwait(false);
            return new WorldSyncRunResult(SyncOutcome.Quiet);
        }

        var named = 0;
        var failed = 0;

        foreach (var row in waiting)
        {
            var endpoint = new VRChatEndpoint(VRChatEndpointClass.WorldsRead, row.WorldId, "GetWorld");

            var result = await _gate.ExecuteAsync(
                endpoint,
                (client, token) => client.Worlds.GetWorldWithHttpInfoAsync(row.WorldId, cancellationToken: token),
                VRChatCallPriority.Background,
                ct).ConfigureAwait(false);

            if (result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting)
            {
                // Cold stop: stop the pass where it is rather than walking the rest of the list
                // into the same wall. Never retried (spec 4.3.1).
                _log.Information(
                    "Naming worlds is paused: {Reason}",
                    result.ErrorMessage ?? "the worlds.read bucket is cold-stopped");

                await RecordPollAsync(ct).ConfigureAwait(false);
                return new WorldSyncRunResult(SyncOutcome.RateLimited, named, failed, result.ErrorMessage);
            }

            if (!result.Success || result.Value is not { } world)
            {
                // Private, deleted, or gone. Written down so the sweep stops asking.
                row.RefreshError = Shorten(result.ErrorMessage) ?? $"VRChat returned {result.StatusCode}";
                failed++;
                continue;
            }

            WorldSnapshot.From(world).ApplyTo(row, _clock.UtcNow);
            named++;
        }

        await RecordPollAsync(ct).ConfigureAwait(false);

        if (named > 0 || failed > 0)
            _log.Information("Named {Named} worlds; {Failed} could not be read", named, failed);

        return new WorldSyncRunResult(SyncOutcome.Produced, named, failed);
    }

    private async Task RecordPollAsync(CancellationToken ct)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        settings.WorldSweepPolledAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Keeps a failure inside the column it is stored in.</summary>
    private static string? Shorten(string? message) =>
        message is null ? null : message.Length <= 512 ? message : message[..512];
}

/// <summary>What one pass of the world sweep did.</summary>
/// <param name="Named">Worlds that now have a name they did not have before.</param>
/// <param name="Failed">Worlds that could not be read -- normally private or deleted.</param>
public sealed record WorldSyncRunResult(
    SyncOutcome Outcome,
    int Named = 0,
    int Failed = 0,
    string? Message = null);
