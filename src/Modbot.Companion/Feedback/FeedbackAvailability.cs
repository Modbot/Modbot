using Modbot.Companion.Ingest;

namespace Modbot.Companion.Feedback;

/// <summary>
/// Whether the Feedback button is offered: this device's owner may send feedback on the paired server,
/// as the server last said.
/// </summary>
/// <remarks>
/// <para>It follows "Send feedback" and nothing else: not AI Chat, and not whether AI is on, so the
/// button shows on a server with AI off.</para>
/// <para>Off until the server says yes. A server that cannot be reached leaves the last answer as it
/// was; no pairing at all takes it away. Whoever owns this asks again at start, on a slow timer, and
/// when the pairing changes.</para>
/// </remarks>
public sealed class FeedbackAvailability
{
    private readonly IFeedbackClient _client;
    private readonly Func<ServerPairing?> _pairing;

    private volatile bool _available;

    public FeedbackAvailability(IFeedbackClient client, Func<ServerPairing?> pairing)
    {
        _client = client;
        _pairing = pairing;
    }

    /// <summary>The answer changed. Raised on whichever thread asked.</summary>
    public event Action? Changed;

    public bool Available => _available;

    /// <summary>Asks the server now.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var pairing = _pairing();
        var answer = pairing is null ? false : await _client.GetAvailableAsync(pairing, cancellationToken).ConfigureAwait(false);

        if (answer is not { } value || value == _available)
            return;

        _available = value;
        Changed?.Invoke();
    }
}
