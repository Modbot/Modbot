using System.Text.Json.Serialization;
using System.Threading.Channels;
using Modbot.Core.Time;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Companion.Alerts;

/// <param name="Reason">Already resolved server-side, and shown verbatim in the headset.</param>
/// <param name="TrustRank">The person's VRChat trust rank as stored, when known.</param>
public sealed record FlaggedJoinAlertDto(
    [property: JsonPropertyName("alertId")] string AlertId,
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("raisedAt")] DateTimeOffset RaisedAt,
    [property: JsonPropertyName("trustRank")] TrustRank? TrustRank = null);

/// <summary>
/// The one thing in the protocol that is pushed rather than polled.
/// </summary>
/// <remarks>
/// <para><strong>Why this case and no other.</strong> A flagged user joining the instance a
/// moderator is standing in is the overlay's highest-value moment, and by the time a thirty-second
/// poll notices, the moment has passed. Roster refreshes, flag updates and health all ride the
/// ordinary batch cycle, because nothing about them is worse for being half a minute late.</para>
/// <para><strong>It is a long poll, not a websocket.</strong> One endpoint, one concern, and it
/// degrades to a slow poll rather than to nothing when a proxy, captive portal or VPN interferes —
/// which are ordinary conditions on a moderator's laptop.</para>
/// <para><strong>Bounded, and dropped rather than queued when full.</strong> An alert is about a
/// moment; a backlog of stale ones delivered at once would interrupt a moderator with news from
/// twenty minutes ago, and an overlay that interrupts constantly gets disabled. A disabled overlay
/// notifies nobody.</para>
/// <para><strong>Scoped to the instance, not broadcast.</strong> An alert reaches only the devices
/// believed to be standing in the instance it names — see <see cref="DeviceLocations"/>. A
/// moderator elsewhere cannot act on it, so sending it to them would spend a round trip to deliver
/// something their client discards, and would hand one moderator's instance and its arrivals to
/// every other moderator's machine for no purpose.</para>
/// <para><strong>This is still not a command channel.</strong> An alert is information the client
/// displays. Nothing here tells a client to do anything, and the client has no code that would act
/// on it if it did.</para>
/// </remarks>
public sealed class AlertHub
{
    /// <summary>
    /// How many undelivered alerts one device may hold. Small on purpose: past a handful, the
    /// headset has nowhere to put them and the moderator has already missed the moment.
    /// </summary>
    public const int PerDeviceCapacity = 8;

    private readonly DeviceLocations _locations;
    private readonly Dictionary<Guid, Channel<FlaggedJoinAlertDto>> _queues = [];
    private readonly Lock _gate = new();

    public AlertHub(DeviceLocations locations) => _locations = locations;

    /// <summary>
    /// Raises an alert for the devices in the instance it names, except the one that reported it.
    /// Returns how many were told.
    /// </summary>
    /// <remarks>
    /// <para>The reporting client already knows: it read the join out of its own log a moment ago,
    /// and telling it again would put a card in front of the one moderator who does not need
    /// it.</para>
    /// <para><strong>Everybody else is filtered by where they are.</strong> The decision lives here
    /// rather than at the call site so there is one place it is made and one place it can be read —
    /// a second caller of this method must not be able to broadcast by omission. A device that has
    /// not recently said where it is is told nothing.</para>
    /// </remarks>
    public int Raise(
        FlaggedJoinAlertDto alert,
        Guid reportedByDeviceId,
        IEnumerable<Guid> deviceIds,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var raised = 0;

        lock (_gate)
        {
            foreach (var deviceId in deviceIds)
            {
                if (deviceId == reportedByDeviceId || !_locations.IsIn(deviceId, alert.InstanceId, now))
                    continue;

                QueueFor(deviceId).Writer.TryWrite(alert);
                raised++;
            }
        }

        return raised;
    }

    /// <summary>
    /// Waits up to <paramref name="wait"/> for something to say, or returns null.
    /// </summary>
    /// <remarks>
    /// Returning null is the ordinary outcome by a long way, and the client treats it as such: a
    /// quiet wait is not an error and does not back anything off.
    /// </remarks>
    public async Task<FlaggedJoinAlertDto?> WaitAsync(Guid deviceId, TimeSpan wait, CancellationToken ct)
    {
        Channel<FlaggedJoinAlertDto> queue;
        lock (_gate)
            queue = QueueFor(deviceId);

        if (queue.Reader.TryRead(out var immediate))
            return immediate;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);

        try
        {
            return await queue.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // The wait expired, or the moderator's client went away mid-poll. Neither is a fault.
            return null;
        }
    }

    /// <summary>Forgets a device's queue and where it was. Called when its token is revoked.</summary>
    /// <remarks>
    /// Both halves go together. Leaving the location behind would keep a revoked device on the
    /// list of places this deployment thinks its staff are standing, which is context about a
    /// person who is no longer entitled to any.
    /// </remarks>
    public void Forget(Guid deviceId)
    {
        lock (_gate)
            _queues.Remove(deviceId);

        _locations.Forget(deviceId);
    }

    private Channel<FlaggedJoinAlertDto> QueueFor(Guid deviceId)
    {
        if (_queues.TryGetValue(deviceId, out var existing))
            return existing;

        // DropOldest rather than DropWrite: when a moderator has been away and alerts have piled
        // up, the newest is the one still worth acting on.
        var created = Channel.CreateBounded<FlaggedJoinAlertDto>(
            new BoundedChannelOptions(PerDeviceCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            });

        _queues[deviceId] = created;
        return created;
    }

    /// <summary>
    /// Builds the alert for somebody with prior actions walking into an instance.
    /// </summary>
    public static FlaggedJoinAlertDto ForFlaggedJoin(
        IModbotClock clock,
        string subjectId,
        string? displayName,
        string instanceId,
        int priorActions,
        TrustRank? trustRank = null)
        => new(
            Guid.NewGuid().ToString("n"),
            subjectId,
            displayName,
            instanceId,
            priorActions == 1
                ? "1 prior moderation action"
                : $"{priorActions} prior moderation actions",
            priorActions,
            clock.UtcNow,
            trustRank);
}
