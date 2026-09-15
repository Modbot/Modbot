using Modbot.Client.Ingest;
using Modbot.Client.Instances;
using Modbot.Client.Routing;
using Modbot.Client.Time;
using Modbot.Core.Time;

namespace Modbot.Client.Pipeline;

/// <summary>What one turn of the client's loop did.</summary>
/// <param name="Dropped">
/// Observations no server was entitled to hear about — a moderator's own private, friends-only and
/// public VRChat use, and any group no paired server manages.
/// </param>
public readonly record struct EngineTick(int Observed, int Routed, int Dropped, int BatchesSent);

/// <summary>
/// The whole client, assembled: read the log once, decide who may hear about what, and report.
/// </summary>
/// <remarks>
/// <para><strong>The log is read once, not once per server.</strong> A moderator staffing several
/// groups runs one client, not one per group — otherwise they would have several tray icons,
/// several updaters and several parsers on one machine, for one log file.</para>
/// <para><strong>Everything after the read is per-server and separate:</strong> its own device
/// token, its own negotiated API version, its own buffer, its own clock offset, its own pause
/// switch. One group's operator cannot see another's instances, and pausing one does not pause the
/// other.</para>
/// <para><strong>Nothing is pushed to this client.</strong> Every request is client-initiated;
/// there is no command channel from any server, so what this program does is fully described by
/// this source and nothing a server sends can change it.</para>
/// </remarks>
public sealed class ClientEngine
{
    private readonly PresenceObserver _observer;
    private readonly EventRouter _router;
    private readonly IServerTimeProbe? _timeProbe;
    private readonly IModbotClock _clock;
    private readonly Dictionary<string, DateTimeOffset> _lastClockCheck = new(StringComparer.Ordinal);

    /// <summary>How often each server's clock offset is re-measured. Not per batch.</summary>
    public static readonly TimeSpan ClockCheckInterval = TimeSpan.FromHours(2);

    public ClientEngine(
        PresenceObserver observer,
        IModbotClock clock,
        IEnumerable<ServerConnection>? connections = null,
        IServerTimeProbe? timeProbe = null)
    {
        _observer = observer;
        _clock = clock;
        _timeProbe = timeProbe;
        _router = new EventRouter();
        Connections = [];

        foreach (var connection in connections ?? [])
            Add(connection);
    }

    public List<ServerConnection> Connections { get; }

    public LogHealth LogHealth => _observer.Health;

    /// <summary>
    /// Where the moderator is standing, as the log last said, or <c>null</c> when that is not
    /// known.
    /// </summary>
    /// <remarks>
    /// <para>This is the engine's one output that is <em>not</em> about reporting. The overlay
    /// follows the moderator: whichever paired server manages this instance is the only one it
    /// reads a roster from or shows a card for, and a moderator who is not in a group instance —
    /// most of anybody's VRChat use — sees the idle screen while nothing is contacted at
    /// all.</para>
    /// <para>Nothing is transmitted to obtain this. It is the same parse of the same log lines the
    /// reporting half already made, read a second time by a different consumer.</para>
    /// </remarks>
    public InstanceLocation? CurrentInstance => _observer.CurrentInstance;

    public void Add(ServerConnection connection)
    {
        Connections.Add(connection);
        _router.Add(connection);
    }

    /// <summary>
    /// Removes a pairing and forgets what was queued for it, so a moderator who unpairs a server
    /// leaves nothing of that group's data behind on their disk.
    /// </summary>
    public void Remove(ServerConnection connection, FileEventBuffer buffer)
    {
        Connections.Remove(connection);
        _router.Remove(connection);
        buffer.Clear();
    }

    /// <summary>
    /// One turn: read whatever VRChat has written, route it, and send anything due. Called on a
    /// timer; it does nothing when the log has not grown and no batch is due.
    /// </summary>
    public async Task<EngineTick> TickAsync(CancellationToken cancellationToken = default)
    {
        var observations = _observer.Poll();
        var dropped = _router.DispatchAll(observations);

        var sent = 0;
        foreach (var connection in Connections)
        {
            await MaybeResyncClockAsync(connection, cancellationToken).ConfigureAwait(false);

            if (await connection.PumpAsync(cancellationToken).ConfigureAwait(false) is not null)
                sent++;
        }

        return new EngineTick(observations.Count, observations.Count - dropped, dropped, sent);
    }

    private async Task MaybeResyncClockAsync(ServerConnection connection, CancellationToken cancellationToken)
    {
        if (_timeProbe is null || connection.IsPaused || connection.State is ConnectionState.Stopped)
            return;

        if (_lastClockCheck.TryGetValue(connection.ServerId, out var last)
            && _clock.UtcNow - last < ClockCheckInterval)
        {
            return;
        }

        _lastClockCheck[connection.ServerId] = _clock.UtcNow;

        if (await _timeProbe.MeasureAsync(connection.Pairing, cancellationToken).ConfigureAwait(false) is { } answer)
        {
            connection.ServerClock.Add(answer.Sample);
            connection.Cloud = answer.Cloud;
        }
    }
}
