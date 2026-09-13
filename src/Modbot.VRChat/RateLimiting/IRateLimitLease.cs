namespace Modbot.VRChat.RateLimiting;

/// <summary>Why the limiter refused to issue a request.</summary>
public enum RateLimitDenialReason
{
    /// <summary>The bucket is cold-stopped and the waiting period has not elapsed (spec 4.3.1).</summary>
    ColdStop,

    /// <summary>A probe is out and its outcome is unknown. Never a second probe in one period.</summary>
    ProbeInFlight,

    /// <summary>Probing was abandoned after repeated failures; the operator has been alerted.</summary>
    Alerting,
}

/// <param name="Bucket">The bucket that refused — the most specific one, so the message can say which.</param>
/// <param name="RetryAfter">
/// How long until this bucket would next consider a probe. Modbot's own estimate: VRChat sends no
/// <c>Retry-After</c> (spec 4.3), so there is no header this could have come from.
/// </param>
public sealed record RateLimitDenial(string Bucket, RateLimitDenialReason Reason, TimeSpan RetryAfter);

/// <summary>
/// Permission to issue exactly one request, and the obligation to report what happened.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.1 calls for the token bucket and the serialising semaphore to sit behind this interface
/// so the in-process implementation can become a distributed lease without touching call sites.
/// Everything a caller needs is here: whether it may proceed, whether this one request is a probe,
/// and the callback that feeds the outcome back into the budget.
/// </para>
/// <para>
/// Disposing releases the lane. Failing to call <see cref="ReportAsync"/> is not an error — a
/// cancelled or crashed call simply contributes nothing to adaptation, which is the safe default.
/// </para>
/// </remarks>
public interface IRateLimitLease : IAsyncDisposable
{
    /// <summary>False when the request must not be issued at all.</summary>
    bool IsAcquired { get; }

    /// <summary>Set when <see cref="IsAcquired"/> is false.</summary>
    RateLimitDenial? Denial { get; }

    /// <summary>
    /// True when this is the single probe for a cold-stopped bucket's waiting period. A caller
    /// may want to choose the cheapest call in the class (spec 4.3.1 step 2).
    /// </summary>
    bool IsProbe { get; }

    /// <summary>Tokens remaining in the most specific bucket, for the HTTP log.</summary>
    double Tokens { get; }

    /// <summary>
    /// Reports the HTTP status the request produced. Pass 0 when no response arrived — a DNS
    /// failure or timeout is not evidence about the rate limit in either direction.
    /// </summary>
    ValueTask ReportAsync(int statusCode, CancellationToken ct = default);
}
