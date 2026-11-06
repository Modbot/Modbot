using Modbot.Companion.Ingest;

namespace Modbot.Companion.Chat;

/// <summary>
/// Whether the Dictate button is offered: AI Chat is on for the paired server and this device's owner
/// may use it, as the server last said.
/// </summary>
/// <remarks>
/// <para>Off until the server says yes. A server that cannot be reached leaves the last answer as it
/// was, so a dropped connection does not take the button away in the middle of using it; no pairing
/// at all takes it away.</para>
/// <para>Whoever owns this asks again at start, on a slow timer, and when the pairing changes.</para>
/// </remarks>
public sealed class ChatAvailability
{
    private readonly IChatClient _client;
    private readonly Func<ServerPairing?> _pairing;

    private volatile bool _available;

    public ChatAvailability(IChatClient client, Func<ServerPairing?> pairing)
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
