using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Reads the page of every world the group has an instance open in, for how many people are in the
/// world and in each of its instances.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it is for.</strong> An instance's popup compares it with the other instances in the
/// same world at the time (spec 2026-09-27-instance-against-its-world-design.md). The group's own
/// head counts cover the group's instances only; a world's page is the one place VRChat lists
/// everybody else's, with a head count each.
/// </para>
/// <para>
/// <strong>Saved as sent.</strong> The page's <c>instances</c> array is kept exactly as it arrived
/// (<see cref="WorldHeadCount.Instances"/>), and <c>occupants</c> and its two halves are read from
/// the body rather than the SDK's model, which cannot tell a missing number from a nought.
/// </para>
/// <para>
/// <strong>Only worlds the group is in, only while it is.</strong> A world with no open group
/// instance is never read here, so nothing is kept about worlds the group has no business in, and
/// a quiet group costs nothing.
/// </para>
/// <para>
/// <strong>Budget.</strong> <c>worlds.read</c>, measured at one request per second and shared with
/// the world sweep (<see cref="VRChatEndpointClass.WorldsRead"/>). One read per world every
/// <see cref="WorldHeadCountSyncService.Interval"/>, agreed with the maintainer on 2026-09-27
/// (foundation 4.3.4): three worlds open is a request and a half a minute. A 429 is never retried
/// (spec 4.3.1): the pass stops, and the comparison has a gap for as long as the stop lasts.
/// </para>
/// </remarks>
public sealed class WorldHeadCountSync
{
    /// <summary>
    /// The most worlds one pass reads. Far more than a group has open at once; a bound so that a
    /// list gone wrong cannot spend the bucket on one pass.
    /// </summary>
    public const int WorldsPerPass = 20;

    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public WorldHeadCountSync(IVRChatGate gate, ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _gate = gate;
        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<WorldHeadCountRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(settings.ManagedGroupId))
            return new WorldHeadCountRunResult(SyncOutcome.NotConfigured);

        var groupId = settings.ManagedGroupId;

        // The same instances the head count read covers: open, and carried by the group's list.
        var worlds = await _db.VRChatInstances.AsNoTracking()
            .Where(i => i.GroupId == groupId && i.SeenInGroupList && i.ClosedAt == null)
            .Select(i => i.WorldId)
            .Distinct()
            .OrderBy(w => w)
            .Take(WorldsPerPass)
            .ToListAsync(ct).ConfigureAwait(false);

        if (worlds.Count == 0)
            return new WorldHeadCountRunResult(SyncOutcome.Quiet);

        var read = 0;
        var failed = 0;

        foreach (var worldId in worlds)
        {
            var endpoint = new VRChatEndpoint(VRChatEndpointClass.WorldsRead, worldId, "GetWorld");

            // The id is passed exactly as it was stored and never checked for shape (spec 3.1.1).
            var result = await _gate.ExecuteAsync(
                endpoint,
                (client, token) => client.Worlds.GetWorldWithHttpInfoAsync(worldId, cancellationToken: token),
                VRChatCallPriority.Background,
                ct).ConfigureAwait(false);

            if (result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting)
            {
                _log.Information(
                    "Reading world head counts is paused: {Reason}",
                    result.ErrorMessage ?? "the worlds.read bucket is cold-stopped");

                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                return new WorldHeadCountRunResult(SyncOutcome.RateLimited, read, failed, result.ErrorMessage);
            }

            if (!result.Success || ReadBody(result.RawResponse) is not { } counts)
            {
                failed++;
                continue;
            }

            _db.WorldHeadCounts.Add(new WorldHeadCount
            {
                WorldId = worldId,
                CountedAt = _clock.UtcNow,
                Occupants = counts.Occupants,
                PublicOccupants = counts.PublicOccupants,
                PrivateOccupants = counts.PrivateOccupants,
                Instances = counts.Instances,
            });

            read++;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new WorldHeadCountRunResult(SyncOutcome.Produced, read, failed);
    }

    /// <summary>What one world page's body says about who is in it.</summary>
    /// <param name="Instances">The <c>instances</c> array's own text, as it arrived.</param>
    internal sealed record BodyCounts(int? Occupants, int? PublicOccupants, int? PrivateOccupants, string? Instances);

    /// <summary>
    /// The counts out of a world page's body as it arrived, or null when there is no body to read.
    /// </summary>
    /// <remarks>
    /// A number that is missing, null or not a whole number is null here, never nought: a world
    /// nobody is in reads 0, and a body that did not say must not look like one.
    /// </remarks>
    internal static BodyCounts? ReadBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            int? Number(string name) =>
                root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var number)
                    ? number
                    : null;

            var instances = root.TryGetProperty("instances", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.GetRawText()
                : null;

            return new BodyCounts(Number("occupants"), Number("publicOccupants"), Number("privateOccupants"), instances);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What one pass of the world head count read did.</summary>
/// <param name="Read">Worlds whose page was read and saved.</param>
/// <param name="Failed">Worlds whose page could not be read.</param>
public sealed record WorldHeadCountRunResult(
    SyncOutcome Outcome,
    int Read = 0,
    int Failed = 0,
    string? Message = null);
