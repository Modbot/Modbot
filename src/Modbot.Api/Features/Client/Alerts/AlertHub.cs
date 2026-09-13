using System.Text.Json.Serialization;
using System.Threading.Channels;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Client.Alerts;

/// <param name="Reason">Already resolved server-side, and shown verbatim in the headset.</param>
public sealed record FlaggedJoinAlertDto(
    [property: JsonPropertyName("alertId")] string AlertId,
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("raisedAt")] DateTimeOffset RaisedAt);

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

    private readonly Dictionary<Guid, Channel<FlaggedJoinAlertDto>> _queues = [];
    private readonly Lock _gate = new();

    /// <summary>Raises an alert for every paired device except the one that reported it.</summary>
    /// <remarks>
    /// The reporting client already knows: it read the join out of its own log a moment ago, and
    /// telling it again would put a card in front of the one moderator who does not need it.
    /// </remarks>
    public void Raise(FlaggedJoinAlertDto alert, Guid reportedByDeviceId, IEnumerable<Guid> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(alert);

        lock (_gate)
        {
            foreach (var deviceId in deviceIds)
            {
                if (deviceId == reportedByDeviceId)
                    continue;

                QueueFor(deviceId).Writer.TryWrite(alert);
            }
        }
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

    /// <summary>Forgets a device's queue. Called when its token is revoked.</summary>
    public void Forget(Guid deviceId)
    {
        lock (_gate)
            _queues.Remove(deviceId);
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
        int priorActions)
        => new(
            Guid.NewGuid().ToString("n"),
            subjectId,
            displayName,
            instanceId,
            priorActions == 1
                ? "1 prior moderation action"
                : $"{priorActions} prior moderation actions",
            priorActions,
            clock.UtcNow);
}
