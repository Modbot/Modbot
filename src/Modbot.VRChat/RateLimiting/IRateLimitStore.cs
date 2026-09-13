namespace Modbot.VRChat.RateLimiting;

/// <summary>
/// One bucket's persisted state: what the operator configured, and where the limiter had got to.
/// </summary>
/// <remarks>
/// Configuration and penalty state share a row deliberately. They are read together on every
/// boot, written together on every incident, and separating them would make it possible to
/// restore one without the other — which is precisely the failure spec 4.3.2 is about.
/// </remarks>
public sealed record RateLimitBucketRecord
{
    public required string Name { get; init; }

    /// <summary>The endpoint class, so a query can group resource buckets under their class.</summary>
    public required string EndpointClass { get; init; }

    /// <summary>Null for global and class buckets. Never parsed; ids are opaque (spec 3.1.1).</summary>
    public string? ResourceId { get; init; }

    public double CeilingPerSecond { get; init; }
    public double Fraction { get; init; }
    public double BudgetMultiplier { get; init; } = 1.0;
    public double Tokens { get; init; }
    public DateTimeOffset TokensAt { get; init; }
    public DateTimeOffset? StoppedUntil { get; init; }
    public bool ProbeInFlight { get; init; }
    public int ConsecutiveProbeFailures { get; init; }
    public bool Alerting { get; init; }
    public DateTimeOffset LastAdaptedAt { get; init; }
    public DateTimeOffset? LastRateLimitedAt { get; init; }
    public int RateLimitHits { get; init; }
}

/// <summary>
/// Where limiter state lives across restarts.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.3.2: a Railway redeploy, a crash or an OOM kill must not reset the gate to full budget
/// and send it straight back into a live penalty. A crash-loop would otherwise do that every few
/// seconds, issuing a continuous stream of penalty-extending probes — turning a ten-minute rate
/// limit into an account-level problem.
/// </para>
/// <para>
/// It is an interface so the limiter can be tested without a database, not so the database can be
/// swapped out. The shipped implementation is the only one that satisfies spec 4.3.2.
/// </para>
/// </remarks>
public interface IRateLimitStore
{
    Task<IReadOnlyList<RateLimitBucketRecord>> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(IReadOnlyList<RateLimitBucketRecord> buckets, CancellationToken ct = default);
}
