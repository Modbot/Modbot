using Modbot.Companion.Ingest;
using Modbot.Core.Time;

namespace Modbot.Companion.Overlay;

/// <summary>What one turn of the long poll concluded.</summary>
public enum AlertPollOutcome
{
    /// <summary>An alert arrived. The overlay shows a card.</summary>
    Alert,

    /// <summary>The wait expired with nothing to say. The commonest answer by a long way.</summary>
    Quiet,

    /// <summary>
    /// Something between here and the server closed an idle connection. Indistinguishable from a
    /// quiet period in its effect, so it is treated as one — but it shortens the next wait.
    /// </summary>
    ConnectionCutShort,

    /// <summary>A real failure: the server is down, or the request failed immediately.</summary>
    Failed,

    /// <summary>Terminal for this pairing. Surfaced, not retried.</summary>
    Unauthorised,
}

public sealed record AlertPoll(AlertPollOutcome Outcome, FlaggedJoinAlert? Alert = null, int WaitUsedSeconds = 0);

/// <summary>
/// The one push channel in the protocol, and the rules that keep it working through a proxy.
/// </summary>
/// <remarks>
/// <para><strong>What this sends.</strong> One GET carrying a bearer token and nothing else, held
/// open by the server until either a flagged user joins the instance this moderator is in or the
/// wait expires. It discloses nothing: the server already knows what it is going to tell the
/// client, and the client tells it nothing here.</para>
/// <para><strong>Why a long poll and not a websocket.</strong> One endpoint, one concern, and it
/// degrades to a slow poll rather than to nothing when a corporate proxy, a captive portal or a
/// VPN interferes — which are ordinary conditions on a moderator's laptop, not edge cases.</para>
/// <para><strong>The adaptive wait.</strong> Load balancers and proxies routinely cap idle
/// connections below thirty seconds, and the symptom is a request that dies at almost exactly the
/// same duration every time. A failure arriving after most of the requested wait is therefore read
/// as an idle timeout rather than as a broken server: the alert is simply not there, the channel
/// does not back off, and the next wait is shortened to fit under whatever cap is in the way. A
/// clean answer restores it. This is what stops a proxy turning the client's one push channel into
/// a retry storm that also never delivers anything.</para>
/// </remarks>
public sealed class AlertChannel
{
    /// <summary>Protocol section 6.1's starting point.</summary>
    public const int DefaultWaitSeconds = 30;

    /// <summary>
    /// Below this there is no point long-polling at all; it has become an ordinary poll, and
    /// shortening further would only add requests.
    /// </summary>
    public const int MinimumWaitSeconds = 5;

    /// <summary>
    /// A failure this far into the wait is an idle timeout, not a fault. Deliberately generous:
    /// misreading a genuine failure as quiet costs one missed alert, while misreading a proxy
    /// timeout as a failure costs the channel entirely.
    /// </summary>
    private const double CutShortFraction = 0.6;

    private readonly IOverlayReadClient _reads;
    private readonly IModbotClock _clock;
    private readonly BackoffPolicy _backoff;

    private int _waitSeconds = DefaultWaitSeconds;
    private int _consecutiveFailures;
    private DateTimeOffset? _notBefore;

    public AlertChannel(IOverlayReadClient reads, IModbotClock clock, BackoffPolicy? backoff = null)
    {
        _reads = reads;
        _clock = clock;
        _backoff = backoff ?? new BackoffPolicy();
    }

    /// <summary>The wait the next poll will ask for, after any shortening.</summary>
    public int WaitSeconds => _waitSeconds;

    /// <summary>Whether the channel has given up on this pairing entirely.</summary>
    public bool IsStopped { get; private set; }

    /// <summary>Whether a failure is still being waited out.</summary>
    public bool IsBackingOff => _notBefore is { } until && _clock.UtcNow < until;

    public async Task<AlertPoll> PollOnceAsync(ServerPairing pairing, CancellationToken cancellationToken = default)
    {
        if (IsStopped || IsBackingOff)
            return new AlertPoll(AlertPollOutcome.Quiet, WaitUsedSeconds: _waitSeconds);

        var asked = _waitSeconds;
        var result = await _reads.WaitForAlertAsync(pairing, asked, cancellationToken).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case ReadOutcome.Fetched when result.Value is not null:
                Succeeded();
                return new AlertPoll(AlertPollOutcome.Alert, result.Value, asked);

            case ReadOutcome.Fetched:
            case ReadOutcome.NothingWaiting:
                Succeeded();
                return new AlertPoll(AlertPollOutcome.Quiet, WaitUsedSeconds: asked);

            case ReadOutcome.Unauthorised:
                IsStopped = true;
                return new AlertPoll(AlertPollOutcome.Unauthorised, WaitUsedSeconds: asked);

            default:
                if (result.Elapsed >= TimeSpan.FromSeconds(asked * CutShortFraction))
                {
                    // Something in the path hung up on an idle connection. Fit under it rather
                    // than fight it, and do not back off: there is nothing wrong with the server.
                    _waitSeconds = Math.Max(MinimumWaitSeconds, (int)(result.Elapsed.TotalSeconds * 0.8));
                    _consecutiveFailures = 0;
                    _notBefore = null;
                    return new AlertPoll(AlertPollOutcome.ConnectionCutShort, WaitUsedSeconds: asked);
                }

                _consecutiveFailures++;
                _notBefore = _clock.UtcNow + _backoff.Delay(_consecutiveFailures);
                return new AlertPoll(AlertPollOutcome.Failed, WaitUsedSeconds: asked);
        }
    }

    private void Succeeded()
    {
        _consecutiveFailures = 0;
        _notBefore = null;

        // Creep back towards the full wait, so a transient proxy does not permanently halve the
        // channel's efficiency long after it has gone.
        if (_waitSeconds < DefaultWaitSeconds)
            _waitSeconds = Math.Min(DefaultWaitSeconds, _waitSeconds + 5);
    }
}
