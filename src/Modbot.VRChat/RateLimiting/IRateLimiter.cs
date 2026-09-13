namespace Modbot.VRChat.RateLimiting;

/// <summary>One bucket's state, for the gate health display (spec 4.3.3).</summary>
/// <remarks>
/// An operator has to be able to see that Modbot is deliberately slow rather than broken, and
/// <em>which</em> part of it is stopped. That is a per-bucket question, not a per-account one.
/// </remarks>
public sealed record RateLimitBucketHealth(
    string Name,
    string EndpointClass,
    string? ResourceId,
    double EffectiveRatePerSecond,
    double BudgetMultiplier,
    bool IsColdStopped,
    DateTimeOffset? StoppedUntil,
    bool Alerting,
    int RateLimitHits,
    DateTimeOffset? LastRateLimitedAt);

/// <summary>
/// The hierarchical token buckets: global, endpoint class, and optionally resource.
/// </summary>
/// <remarks>
/// <para>
/// A request needs a token at every level it belongs to. A 429 cold-stops the most specific level
/// that matched and applies a multiplicative decrease to every ancestor, because a limit hit
/// anywhere is evidence that the whole estimate is optimistic (spec 4.3.1).
/// </para>
/// <para>
/// Acquisition <strong>waits</strong> for tokens but <strong>never</strong> waits out a cold stop.
/// A stopped bucket denies immediately so that callers fail fast with something to say, rather
/// than queueing into a penalty whose end nobody can see.
/// </para>
/// </remarks>
public interface IRateLimiter
{
    /// <summary>
    /// Waits for a token at every level, then returns a lease. The returned lease may be a
    /// refusal — check <see cref="IRateLimitLease.IsAcquired"/> before issuing anything.
    /// </summary>
    Task<IRateLimitLease> AcquireAsync(
        VRChatEndpoint endpoint,
        VRChatCallPriority priority = VRChatCallPriority.Background,
        CancellationToken ct = default);

    /// <summary>Current state of every bucket that exists, for the UI and for tests.</summary>
    Task<IReadOnlyList<RateLimitBucketHealth>> DescribeAsync(CancellationToken ct = default);
}
