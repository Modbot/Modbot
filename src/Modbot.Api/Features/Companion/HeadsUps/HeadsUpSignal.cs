namespace Modbot.Api.Features.Companion.HeadsUps;

/// <summary>
/// Tells a device's live connection that the heads-ups where it is standing changed, so its
/// companion reads them again at once rather than at its next roster read.
/// </summary>
/// <remarks>
/// <para><strong>A nudge, not the news.</strong> What changed is never carried here: the
/// connection says "read again", and the roster read -- which already answers only for the instance
/// the device names -- carries the heads-ups. So there is one place that decides who sees one.</para>
/// <para><strong>In memory, like the device locations it is aimed by.</strong> Lost on a restart,
/// which costs a companion at most one roster read's wait.</para>
/// </remarks>
public sealed class HeadsUpSignal
{
    private readonly Dictionary<Guid, Entry> _devices = [];
    private readonly Lock _gate = new();

    /// <summary>How many changes this device has been told of. Only ever goes up.</summary>
    public long VersionOf(Guid deviceId)
    {
        lock (_gate)
            return EntryFor(deviceId).Version;
    }

    /// <summary>Completes at this device's next change. Take it before reading the version, so none is missed.</summary>
    public Task NextAsync(Guid deviceId)
    {
        lock (_gate)
            return EntryFor(deviceId).Changed.Task;
    }

    /// <summary>Tells these devices that something changed where they are.</summary>
    public void Tell(IEnumerable<Guid> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);

        List<TaskCompletionSource> woken = [];

        lock (_gate)
        {
            foreach (var deviceId in deviceIds)
            {
                var entry = EntryFor(deviceId);
                entry.Version++;
                woken.Add(entry.Changed);
                entry.Changed = NewSource();
            }
        }

        foreach (var source in woken)
            source.TrySetResult();
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
