using Modbot.Companion.Ingest;
using Modbot.Core.Time;
using Modbot.Shared.Clips;

namespace Modbot.Companion.Overlay;

/// <summary>
/// The clip links of every paired server that should have one: opened when the pairing is wanted
/// (the switch is on and the pairing is neither paused nor rejected) and closed when it is not
/// (automatic clips, 2026-10-24).
/// </summary>
/// <remarks>
/// <para><strong>What this sends and receives.</strong> Nothing itself; see <see cref="ClipLink"/>.</para>
/// <para><strong>One thread.</strong> <see cref="Want"/> and <see cref="PumpAsync"/> are called from the
/// same timer, so nothing here is locked.</para>
/// <para><strong>A short grace before closing.</strong> A link that is no longer wanted stays open
/// for <see cref="CloseDelay"/>, so the Clips settings report that says the switch went off is on its
/// way to the server before the connection goes.</para>
/// </remarks>
public sealed class ClipLinks : IDisposable
{
    public static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(5);

    private readonly ILiveSocketFactory? _sockets;
    private readonly IOverlayReadClient _reads;
    private readonly IModbotClock _clock;
    private readonly Dictionary<string, Entry> _links = new(StringComparer.Ordinal);
    private IReadOnlyList<ServerPairing> _wanted = [];

    private sealed class Entry(ClipLink link)
    {
        public ClipLink Link { get; } = link;

        /// <summary>When it stops being kept, or null while it is wanted.</summary>
        public DateTimeOffset? CloseAt { get; set; }
    }

    public ClipLinks(ILiveSocketFactory? sockets, IOverlayReadClient reads, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(reads);

        _sockets = sockets;
        _reads = reads;
        _clock = clock;
    }

    /// <summary>A request from any of the links.</summary>
    public event Action<ServerPairing, ClipRequestMessage>? ClipRequested;

    /// <summary>The pairings that should have a link now. Empty when the switch is off.</summary>
    public void Want(IEnumerable<ServerPairing> pairings) => _wanted = [.. pairings];

    /// <summary>Each server's link state in one word, by server id, for the pairings that have a link.</summary>
    public IReadOnlyDictionary<string, string> Words
        => _links.ToDictionary(l => l.Key, l => l.Value.Link.Word, StringComparer.Ordinal);

    /// <summary>One turn: opens, closes and pumps the links. Never waits on the network.</summary>
    public async Task PumpAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        foreach (var pairing in _wanted)
        {
            if (_links.TryGetValue(pairing.ServerId, out var held) && SamePairing(held.Link.Pairing, pairing))
            {
                held.CloseAt = null;
                continue;
            }

            // New, or paired again with another token or address.
            Remove(pairing.ServerId);

            var link = new ClipLink(pairing, _sockets, _reads, _clock);
            link.ClipRequested += Relay;
            _links[pairing.ServerId] = new Entry(link);
        }

        foreach (var (serverId, entry) in _links.ToList())
        {
            if (_wanted.Any(p => p.ServerId == serverId && SamePairing(entry.Link.Pairing, p)))
                continue;

            entry.CloseAt ??= now + CloseDelay;

            if (now >= entry.CloseAt)
                Remove(serverId);
        }

        foreach (var entry in _links.Values.ToList())
            await entry.Link.PumpAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Relay(ServerPairing pairing, ClipRequestMessage request) => ClipRequested?.Invoke(pairing, request);

    private static bool SamePairing(ServerPairing a, ServerPairing b)
        => a.ServerId == b.ServerId && a.DeviceToken == b.DeviceToken && a.BaseUri == b.BaseUri;

    private void Remove(string serverId)
    {
        if (!_links.Remove(serverId, out var entry))
            return;

        entry.Link.ClipRequested -= Relay;
        entry.Link.Dispose();
    }

    public void Dispose()
    {
        foreach (var serverId in _links.Keys.ToList())
            Remove(serverId);
    }
}
