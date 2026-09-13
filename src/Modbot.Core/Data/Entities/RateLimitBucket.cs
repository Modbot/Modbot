namespace Modbot.Core.Data.Entities;

/// <summary>
/// One rate-limit bucket: what the operator configured, and where the limiter had got to.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.3.2. The penalty state, the adapted budget and the recent-request window live in the
/// database rather than in memory, because a Railway redeploy, a crash or an OOM kill would
/// otherwise reset the gate to full budget and send it straight back into a live penalty — and a
/// crash-loop would do that every few seconds, issuing a continuous stream of penalty-extending
/// probes. A restart loop must not be able to escalate a ten-minute rate limit into an
/// account-level problem.
/// </para>
/// <para>
/// Configuration shares the row with state deliberately. Both are read on boot and written on
/// every incident, and keeping them together removes any path that restores one without the
/// other. It also keeps the per-endpoint ceilings out of <see cref="Settings"/>, which would
/// otherwise need a column per endpoint class and a migration every time one is added.
/// </para>
/// </remarks>
public class RateLimitBucket
{
    /// <summary>
    /// The bucket's identity: an endpoint class, or <c>class:resource</c>. Opaque text, because
    /// half of it is a VRChat id and those follow no structure (spec 3.1.1).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The endpoint class, so resource buckets can be grouped under their class.</summary>
    public string EndpointClass { get; set; } = string.Empty;

    /// <summary>Null for global and class-level buckets.</summary>
    public string? ResourceId { get; set; }

    /// <summary>
    /// The operator's estimate of VRChat's limit for this class, in requests per second. An
    /// estimate about an undocumented system, which is why it is configuration and not a constant.
    /// </summary>
    public double CeilingPerSecond { get; set; }

    /// <summary>The fraction of that estimate Modbot is willing to use (spec 4.3.1, default 60%).</summary>
    public double Fraction { get; set; }

    /// <summary>AIMD state: halved on a 429, recovered additively over hours.</summary>
    public double BudgetMultiplier { get; set; } = 1;

    /// <summary>Tokens left at <see cref="TokensAt"/>, so a restart cannot hand back an allowance.</summary>
    public double Tokens { get; set; }

    public DateTimeOffset TokensAt { get; set; }

    /// <summary>
    /// When the next probe may be issued. Non-null means the bucket is cold-stopped and nothing
    /// at all may be sent on it. Absolute rather than a remaining duration: the penalty keeps
    /// running while Modbot is not.
    /// </summary>
    public DateTimeOffset? StoppedUntil { get; set; }

    /// <summary>
    /// A probe was issued and its outcome is not yet known. On boot this is cleared — the answer
    /// is unknowable — but <see cref="StoppedUntil"/> was already advanced when the probe went
    /// out, so clearing it cannot produce a second probe in the same waiting period.
    /// </summary>
    public bool ProbeInFlight { get; set; }

    public int ConsecutiveProbeFailures { get; set; }

    /// <summary>Probing was abandoned; something is wrong that waiting will not fix.</summary>
    public bool Alerting { get; set; }

    public DateTimeOffset LastAdaptedAt { get; set; }

    public DateTimeOffset? LastRateLimitedAt { get; set; }

    /// <summary>Lifetime 429 count. A healthy Modbot's should be approximately zero.</summary>
    public int RateLimitHits { get; set; }
}
