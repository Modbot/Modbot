using Modbot.Companion.Ingest;
using Modbot.Core.Time;
using Modbot.Shared.Clips;

namespace Modbot.Companion.Overlay;

/// <summary>
/// One paired server's small live connection for clips (automatic clips, 2026-10-24): open whatever
/// the panels are doing and whatever instance the moderator is in, so a ban made after they left the
/// instance still reaches them. It stays open the whole time the companion runs, which is also how
/// the server knows the companion is open and paired.
/// </summary>
/// <remarks>
/// <para><strong>What this sends and receives.</strong> Nothing itself: it drives a <see cref="LiveLink"/>
/// that names no instance, so the socket and the poll carry only the device token, the cursor and the
/// four Clips settings (<see cref="ClipSettingsReport"/>, on connecting and when one changes), as the
/// <see cref="ILiveSocketFactory"/> and <see cref="IOverlayReadClient"/> remarks say. What comes back is
/// clip requests only: the server asking for the clip of one action. This class does nothing with
/// them but raise <see cref="ClipRequested"/>, once per request id.</para>
/// <para><strong>Reconnecting.</strong> The socket first, long polling when it cannot be kept, the
/// socket again later: the one fallback and backoff of <see cref="LiveLink"/>.</para>
/// </remarks>
public sealed class ClipLink : IDisposable
{
    /// <summary>How many request ids are remembered, oldest forgotten first. A request is open for 15 minutes.</summary>
    public const int RememberedRequests = 256;

    private readonly LiveLink _link;
    private readonly HashSet<Guid> _seen = [];
    private readonly Queue<Guid> _seenOrder = new();

    public ClipLink(ServerPairing pairing, ILiveSocketFactory? sockets, IOverlayReadClient reads, IModbotClock clock, BackoffPolicy? backoff = null)
    {
        ArgumentNullException.ThrowIfNull(pairing);

        Pairing = pairing;
        _link = new LiveLink(sockets, reads, clock, pairing, backoff, clipsOnly: true);
    }

    public ServerPairing Pairing { get; }

    public LiveLinkState State => _link.State;

    /// <summary>The state in one word.</summary>
    public string Word => _link.Word;

    /// <summary>A request the server made, raised once per request id however often it arrives.</summary>
    public event Action<ServerPairing, ClipRequestMessage>? ClipRequested;

    /// <summary>One turn: keeps the connection and raises what arrived. Never waits on the network.</summary>
    public async Task PumpAsync(CancellationToken cancellationToken = default)
    {
        await _link.PumpAsync(cancellationToken).ConfigureAwait(false);

        foreach (var request in _link.DrainClipRequests())
        {
            if (!Remember(request.RequestId))
                continue;

            ClipRequested?.Invoke(Pairing, request);
        }
    }

    private bool Remember(Guid id)
    {
        if (!_seen.Add(id))
            return false;

        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > RememberedRequests)
            _seen.Remove(_seenOrder.Dequeue());

        return true;
    }

    public void Dispose() => _link.Dispose();
}
