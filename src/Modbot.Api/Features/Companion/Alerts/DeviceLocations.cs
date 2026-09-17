namespace Modbot.Api.Features.Companion.Alerts;

/// <summary>
/// Which instance each paired device was last standing in, so an alert can be sent to the
/// moderators who can act on it and to nobody else.
/// </summary>
/// <remarks>
/// <para><strong>Why this exists.</strong> A flagged-user alert is only actionable to a moderator
/// who is in that instance. Raising it to every paired device instead spent a round trip per device
/// per alert to deliver something all but one of them would discard — and, worse, told every other
/// moderator's machine which instance a colleague was standing in and who had just walked into it.
/// That is one moderator's context handed to devices with no reason to hold it, which is a
/// data-minimisation problem before it is a bandwidth one.</para>
/// <para><strong>It is not a presence channel, and must not become one.</strong> Nothing is sent to
/// obtain any of this: every entry is a by-product of a request the device was already making for
/// another reason — an ingest batch, which names the instance its events came from, or the
/// overlay's roster read, which names the instance it is showing. No new field on the wire, no new
/// endpoint, no extra request, and nothing a paused client discloses. The alternative — having the
/// overlay's long poll declare its location every thirty seconds — was rejected for exactly that
/// reason: it would be a second, slower presence report under a different name, and one that kept
/// reporting a moderator's whereabouts after they had paused everything else.</para>
/// <para>A batch whose newest event says VRChat's log stopped (<c>LogStopped</c>, sent once per stop
/// and never repeated) forgets the device instead: it can no longer see the room, so it is offered
/// nothing more about it. That is still a by-product of a batch, not a location report.</para>
/// <para><strong>Nothing here is durable, and nothing here is a fact.</strong> This is in-memory
/// routing state for one push channel, sitting beside the queues it routes into. Presence history
/// lives in the fact log; this is a hint about the present and is allowed to be wrong. It is never
/// written down, never queried, and lost on restart, which costs one refresh.</para>
/// <para><strong>It expires, because the ordinary way a session ends is that it stops.</strong> A
/// client whose VRChat was closed, crashed or slept sends no goodbye — the log simply stops and so
/// does the client. A device that has not named an instance recently is therefore treated as
/// somewhere unknown and told nothing, rather than being credited with a location forever.</para>
/// </remarks>
public sealed class DeviceLocations
{
    /// <summary>
    /// How long a device is believed to still be where it last said it was.
    /// </summary>
    /// <remarks>
    /// <para>A running overlay re-reads its instance roster every twenty seconds and an active
    /// client batches at least every thirty, so ten minutes is on the order of thirty missed
    /// refreshes: well past "this client has gone away" and nowhere near "they might still be
    /// standing there".</para>
    /// <para>Erring long is the safer direction. Too generous costs one alert delivered to a device
    /// that has moved, which that client's own filter discards — it shows a card only for the
    /// instance it is actually in, precisely because it cannot trust a server to have filtered
    /// correctly. Too tight costs a missed alert, which is the whole point of the channel.</para>
    /// </remarks>
    public static readonly TimeSpan RememberedFor = TimeSpan.FromMinutes(10);

    private readonly Dictionary<Guid, Entry> _known = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Notes that this device just named this instance in a request it was making anyway.
    /// </summary>
    /// <remarks>
    /// A client replaying an offline buffer names the instance those observations came from, which
    /// it may since have left. That is left uncorrected on purpose: guessing at which reports are
    /// "current enough" would add a second threshold nobody can tune, and the error it would guard
    /// against is one stale alert that the receiving client already drops.
    /// </remarks>
    public void Record(Guid deviceId, string instanceId, DateTimeOffset now)
    {
        if (instanceId is not { Length: > 0 })
            return;

        lock (_gate)
        {
            // Swept here rather than on a timer. The set is bounded by a group's staff list, so
            // walking it costs nothing, and a background sweep would be machinery for a dictionary
            // with a dozen entries in it.
            foreach (var (id, entry) in _known)
            {
                if (now - entry.At > RememberedFor)
                    _known.Remove(id);
            }

            _known[deviceId] = new Entry(instanceId, now);
        }
    }

    /// <summary>
    /// Whether this device is believed to be standing in this instance right now.
    /// </summary>
    /// <remarks>
    /// A device that has never said where it is answers <c>false</c>, so an alert reaches nobody
    /// rather than everybody. That is the right default for the same reason the client filters
    /// again on receipt: the failure worth designing against is context reaching somewhere it was
    /// not needed, not a card arriving a refresh late.
    /// </remarks>
    public bool IsIn(Guid deviceId, string instanceId, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _known.TryGetValue(deviceId, out var entry)
                && now - entry.At <= RememberedFor
                && string.Equals(entry.InstanceId, instanceId, StringComparison.Ordinal);
        }
    }

    /// <summary>Forgets a device. Called when its token is revoked.</summary>
    public void Forget(Guid deviceId)
    {
        lock (_gate)
            _known.Remove(deviceId);
    }

    private readonly record struct Entry(string InstanceId, DateTimeOffset At);
}
