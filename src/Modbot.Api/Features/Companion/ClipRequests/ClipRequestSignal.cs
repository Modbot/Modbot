namespace Modbot.Api.Features.Companion.ClipRequests;

/// <summary>
/// Tells a device's live connection that a clip request was written for it, so the request goes
/// down the connection at once rather than at the next poll.
/// </summary>
/// <remarks>
/// Like <see cref="HeadsUps.HeadsUpSignal"/>: a version number and a task per device, in memory. It
/// carries no request; the connection reads the device's open requests from the database when it is
/// woken, so there is one place that decides what a device is sent. Lost on a restart, which costs a
/// companion at most one poll's wait (the connection also reads them when it opens).
/// </remarks>
public sealed class ClipRequestSignal
{
    private readonly Dictionary<Guid, Entry> _devices = [];
    private readonly Lock _gate = new();

    /// <summary>How many requests this device has been told of. Only ever goes up.</summary>
    public long VersionOf(Guid deviceId)
    {
        lock (_gate)
            return EntryFor(deviceId).Version;
    }

    /// <summary>Completes at this device's next request. Take it before reading the version, so none is missed.</summary>
    public Task NextAsync(Guid deviceId)
    {
        lock (_gate)
            return EntryFor(deviceId).Changed.Task;
    }

    /// <summary>Tells this device that a request was written for it.</summary>
    public void Tell(Guid deviceId)
    {
        TaskCompletionSource woken;

        lock (_gate)
        {
            var entry = EntryFor(deviceId);
            entry.Version++;
            woken = entry.Changed;
            entry.Changed = NewSource();
        }

        woken.TrySetResult();
    }

    /// <summary>Forgets a device. Called when its token is revoked.</summary>
    public void Forget(Guid deviceId)
    {
        lock (_gate)
            _devices.Remove(deviceId);
    }

    private Entry EntryFor(Guid deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var entry))
        {
            entry = new Entry();
            _devices[deviceId] = entry;
        }

        return entry;
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Entry
    {
        public long Version { get; set; }

        public TaskCompletionSource Changed { get; set; } = NewSource();
    }
}
