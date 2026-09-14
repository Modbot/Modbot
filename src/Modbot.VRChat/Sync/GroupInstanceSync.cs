using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Watches which rooms the managed group has open, and records when they open and close.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the only way Modbot sees a room nobody is standing in.</strong> The desktop
/// client reports rooms a moderator walked into, which means a group hosting an event nobody on
/// the moderation team has joined is invisible without this poll -- and so is the hour before the
/// first moderator arrives and the hour after the last one leaves.
/// </para>
/// <para>
/// One request per pass returns every open instance together, with the world attached, so a
/// group's worlds get their names from this poll for free and never need a world read of their
/// own. The budget is one request per ten seconds, measured rather than guessed
/// (<see cref="VRChatEndpointClass.GroupsInstances"/>), which is close enough for a room that
/// lives for hours.
/// </para>
/// <para>
/// <strong>The list is the authority on when a room ends.</strong> A room that was in the last
/// response and is not in this one has closed, and Modbot knows it to within one poll. That is
/// what makes the same instance number appearing later unambiguously a new room, without waiting
/// out the time rule that covers everywhere the list does not reach
/// (<see cref="VRChatInstance.CountsAsNewAfter"/>).
/// </para>
/// </remarks>
public sealed class GroupInstanceSync
{
    /// <summary>
    /// How stale a world's details must be before a room opening in it is allowed to rewrite them.
    /// </summary>
    /// <remarks>
    /// World details are refreshed when a world turns up again, never on a schedule: a name and a
    /// picture change about as often as the world is rebuilt, and polling for them would spend
    /// requests and writes on an answer that is almost always the same one. This floor is what
    /// keeps "when it turns up again" from meaning "twenty times tonight" for a group that runs
    /// many instances in one world.
    /// </remarks>
    public static readonly TimeSpan RereadWorldAfter = TimeSpan.FromDays(30);

    private readonly IVRChatGate _gate;
    private readonly PlaceStore _places;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public GroupInstanceSync(
        IVRChatGate gate,
        PlaceStore places,
        ModbotContext db,
        IModbotClock clock,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _gate = gate;
        _places = places;
        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<GroupInstanceRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(settings.ManagedGroupId))
            return new GroupInstanceRunResult(SyncOutcome.NotConfigured, Message: "no managed group configured");

        var groupId = settings.ManagedGroupId;

        var endpoint = new VRChatEndpoint(
            VRChatEndpointClass.GroupsInstances, groupId, "GetGroupInstances");

        var result = await _gate.ExecuteAsync(
            endpoint,
            (client, token) => client.Groups.GetGroupInstancesWithHttpInfoAsync(groupId, cancellationToken: token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        if (!result.Success)
            return Failed(result);

        var live = result.Value ?? [];
        var now = _clock.UtcNow;
        var openNow = new HashSet<string>(StringComparer.Ordinal);
        var opened = 0;

        foreach (var instance in live)
        {
            var location = instance.Location;
            if (string.IsNullOrWhiteSpace(location))
                continue;

            openNow.Add(location);

            var known = await _db.VRChatInstances
                .AnyAsync(i => i.Location == location && i.ClosedAt == null, ct).ConfigureAwait(false);

            var room = await _places.RecordSightingAsync(
                location,
                now,
                instance.MemberCount,
                fromGroupList: true,
                ct).ConfigureAwait(false);

            if (room is null || known)
                continue;

            opened++;

            // The world arrives attached to the instance, so a group's own worlds are named by
            // this poll and never cost a world read. A world Modbot meets any other way still
            // needs the sweep.
            //
            // Only when a room opens, never on the polls in between. A world's name and picture
            // change about as often as the world is rebuilt, so rewriting the row every ten
            // seconds would be thousands of writes a day to store the same sentence, and the
            // freshness nobody asked for would cost more than the staleness anybody would notice.
            if (instance.World is { } world)
                await RecordWorldAsync(world, now, ct).ConfigureAwait(false);
        }

        var closed = await CloseRoomsNoLongerListedAsync(groupId, openNow, now, ct).ConfigureAwait(false);

        settings.GroupInstancesPolledAt = now;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (opened > 0 || closed > 0)
        {
            _log.Information(
                "The group has {Open} instances open; {Opened} opened and {Closed} closed since the last poll",
                openNow.Count, opened, closed);
        }

        return new GroupInstanceRunResult(
            openNow.Count == 0 && opened == 0 && closed == 0 ? SyncOutcome.Quiet : SyncOutcome.Produced,
            Open: openNow.Count,
            Opened: opened,
            Closed: closed);
    }

    /// <summary>
    /// Closes group rooms that were open a poll ago and are not in the list now.
    /// </summary>
    /// <remarks>
    /// Scoped to rooms the list has actually carried. A moderator's own private or public
    /// instance is never in a group's list, and closing one just because the group does not know
    /// about it would end a session that is still running.
    /// </remarks>
    private async Task<int> CloseRoomsNoLongerListedAsync(
        string groupId,
        HashSet<string> openNow,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var wasOpen = await _db.VRChatInstances
            .Where(i => i.GroupId == groupId && i.ClosedAt == null && i.SeenInGroupList)
            .ToListAsync(ct).ConfigureAwait(false);

        var closed = 0;

        foreach (var room in wasOpen)
        {
            if (openNow.Contains(room.Location))
                continue;

            PlaceStore.Close(room, now, "list");
            closed++;
        }

        return closed;
    }

    private async Task RecordWorldAsync(global::VRChat.API.Model.World world, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(world.Id))
            return;

        await _places.NoteWorldSeenAsync(world.Id, now, ct).ConfigureAwait(false);

        var row = await _db.VRChatWorlds
            .FirstOrDefaultAsync(w => w.WorldId == world.Id, ct).ConfigureAwait(false);

        if (row is null)
            return;

        // A world read recently enough is left alone even though a room just opened in it. A
        // group that opens twenty instances a day in the same world would otherwise rewrite that
        // world twenty times for an answer that has not moved since it was built.
        if (row.LastRefreshedAt is { } read && now - read < RereadWorldAfter)
            return;

        WorldSnapshot.From(world).ApplyTo(row, now);
    }

    private GroupInstanceRunResult Failed<T>(VRChatResult<T> result)
    {
        if (result.Kind == VRChatFailureKind.RateLimited)
        {
            // Never retried, and not an error: a cold stop is the design working (spec 4.3.1).
            _log.Information(
                "The group instance poll is paused: {Reason}",
                result.ErrorMessage ?? "the groups.instances bucket is cold-stopped");

            return new GroupInstanceRunResult(SyncOutcome.RateLimited, Message: result.ErrorMessage);
        }

        _log.Warning(
            "Could not read the group's instances: {Status} {Reason}",
            result.StatusCode,
            result.ErrorMessage ?? "no detail");

        return new GroupInstanceRunResult(SyncOutcome.Failed, Message: result.ErrorMessage);
    }
}

/// <summary>What one pass of the group instance poll did.</summary>
/// <param name="Open">How many rooms the group has open right now.</param>
/// <param name="Opened">How many of those Modbot had not seen before this pass.</param>
/// <param name="Closed">How many rooms left the list, and so ended.</param>
public sealed record GroupInstanceRunResult(
    SyncOutcome Outcome,
    int Open = 0,
    int Opened = 0,
    int Closed = 0,
    string? Message = null);
