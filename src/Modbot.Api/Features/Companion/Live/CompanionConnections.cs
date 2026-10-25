namespace Modbot.Api.Features.Companion.Live;

/// <summary>
/// Which paired companions have their live connection open right now, so an automatic role can ask
/// whether a moderator's companion is running.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A by-product, like <see cref="Alerts.DeviceLocations"/>.</strong> A WebSocket is counted
/// from the moment it is accepted until it ends, which the socket's own keepalive makes about 40
/// seconds after the companion goes away. A companion that polls instead has no connection to
/// watch, so its latest poll counts for <see cref="PollOpenFor"/>. In memory only, and lost on
/// restart, which costs one reconnect.
/// </para>
/// </remarks>
public sealed class CompanionConnections
{
    /// <summary>How long after a poll the device still counts as running.</summary>
    public static readonly TimeSpan PollOpenFor = TimeSpan.FromSeconds(60);

    private readonly Dictionary<Guid, int> _sockets = [];
    private readonly Dictionary<Guid, DateTimeOffset> _polledAt = [];
    private readonly Lock _gate = new();

    /// <summary>Notes that this device has a socket open. Dispose the result when the socket ends.</summary>
    public IDisposable Opened(Guid deviceId)
    {
        lock (_gate)
            _sockets[deviceId] = _sockets.GetValueOrDefault(deviceId) + 1;

        return new Closer(this, deviceId);
    }

    /// <summary>Notes that this device just made a poll.</summary>
    public void Polled(Guid deviceId, DateTimeOffset now)
    {
        lock (_gate)
            _polledAt[deviceId] = now;
    }

    /// <summary>Whether any of these devices has a socket open, or polled in the last <see cref="PollOpenFor"/>.</summary>
    public bool IsOpen(IEnumerable<Guid> deviceIds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);

        lock (_gate)
        {
            return deviceIds.Any(id => _sockets.ContainsKey(id)
                || (_polledAt.TryGetValue(id, out var at) && now - at <= PollOpenFor));
        }
    }

    private void Closed(Guid deviceId)
    {
        lock (_gate)
        {
            if (_sockets.GetValueOrDefault(deviceId) <= 1)
                _sockets.Remove(deviceId);
            else
                _sockets[deviceId]--;
        }
    }

    private sealed class Closer(CompanionConnections owner, Guid deviceId) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                owner.Closed(deviceId);
        }
    }
}
