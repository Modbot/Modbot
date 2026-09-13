namespace Modbot.VRChat.RateLimiting;

/// <summary>
/// One level of the budget hierarchy: global, endpoint class, or resource.
/// </summary>
/// <remarks>
/// <para>
/// A request must take a token at <em>every</em> level it belongs to (spec 4.3.1). The bucket
/// holds both the pacing state and the adaptation state, because a 429 changes both and they
/// have to be persisted together or a restart can resurrect one without the other.
/// </para>
/// <para>
/// All time comes in as a parameter, never read here. Spec 4.3.3 is explicit that rate-limit
/// windows are measured on <c>IModbotClock</c>, so that a host clock adjustment — an NTP step, a
/// VM migration, a DST bug — cannot make the limiter believe a penalty has expired.
/// </para>
/// </remarks>
public sealed class TokenBucket
{
    private const double Tolerance = 1e-9;

    private readonly RateLimitOptions _options;

    public TokenBucket(string name, RateLimitClassOptions limits, RateLimitOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        Name = name;
        Limits = limits;
        CeilingPerSecond = limits.DefaultCeilingPerSecond;
        Fraction = RateLimitOptions.DefaultFraction;
        BudgetMultiplier = 1.0;
        Tokens = limits.BurstTokens;
        TokensAt = now;
        LastAdaptedAt = now;
    }

    /// <summary>Bucket identity: the class name, or <c>class:resource</c> for a resource bucket.</summary>
    public string Name { get; }

    public RateLimitClassOptions Limits { get; }

    /// <summary>The operator's estimate of VRChat's real limit for this class.</summary>
    public double CeilingPerSecond { get; private set; }

    /// <summary>The fraction of that estimate Modbot is willing to use.</summary>
    public double Fraction { get; private set; }

    /// <summary>The AIMD state: halved on a 429, recovered additively over hours.</summary>
    public double BudgetMultiplier { get; private set; }

    public double Tokens { get; private set; }

    public DateTimeOffset TokensAt { get; private set; }

    /// <summary>When the next probe is due. Non-null means the bucket is cold-stopped.</summary>
    public DateTimeOffset? StoppedUntil { get; private set; }

    /// <summary>A probe has been issued and its outcome is not yet known.</summary>
    public bool ProbeInFlight { get; private set; }

    public int ConsecutiveProbeFailures { get; private set; }

    /// <summary>Probing has been abandoned; waiting is not going to fix this (spec 4.3.1 step 4).</summary>
    public bool Alerting { get; private set; }

    public DateTimeOffset LastAdaptedAt { get; private set; }

    public DateTimeOffset? LastRateLimitedAt { get; private set; }

    /// <summary>How many 429s this bucket has seen. A healthy Modbot emits approximately zero.</summary>
    public int RateLimitHits { get; private set; }

    /// <summary>
    /// The rate the bucket actually issues at: the configured fraction of the estimate, clamped
    /// to spec 4.2's hard maximum, scaled by the adapted budget.
    /// </summary>
    /// <remarks>
    /// The clamp is here rather than only at the settings boundary so that no configuration path
    /// — an API write, a hand-edited row, a future import — can raise a rate past the cap.
    /// </remarks>
    public double EffectiveRatePerSecond =>
        Math.Min(Limits.HardMaxPerSecond, CeilingPerSecond * Fraction) * BudgetMultiplier;

    public bool IsColdStopped => StoppedUntil is not null;

    /// <summary>Applies operator configuration, refusing anything above spec 4.2's cap.</summary>
    public void Configure(double ceilingPerSecond, double fraction)
    {
        if (ceilingPerSecond > 0)
            CeilingPerSecond = ceilingPerSecond;

        if (fraction is > 0 and <= 1)
            Fraction = fraction;
    }

    /// <summary>Restores persisted state after a restart (spec 4.3.2).</summary>
    public void Restore(RateLimitBucketRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        Configure(record.CeilingPerSecond, record.Fraction);

        BudgetMultiplier = Math.Clamp(record.BudgetMultiplier, _options.MinimumBudgetMultiplier, 1.0);
        Tokens = Math.Clamp(record.Tokens, 0, Limits.BurstTokens);
        TokensAt = record.TokensAt;
        StoppedUntil = record.StoppedUntil;
        ProbeInFlight = record.ProbeInFlight;
        ConsecutiveProbeFailures = record.ConsecutiveProbeFailures;
        Alerting = record.Alerting;
        LastAdaptedAt = record.LastAdaptedAt;
        LastRateLimitedAt = record.LastRateLimitedAt;
        RateLimitHits = record.RateLimitHits;
    }

    public RateLimitBucketRecord ToRecord(string endpointClass, string? resourceId) => new()
    {
        Name = Name,
        EndpointClass = endpointClass,
        ResourceId = resourceId,
        CeilingPerSecond = CeilingPerSecond,
        Fraction = Fraction,
        BudgetMultiplier = BudgetMultiplier,
        Tokens = Tokens,
        TokensAt = TokensAt,
        StoppedUntil = StoppedUntil,
        ProbeInFlight = ProbeInFlight,
        ConsecutiveProbeFailures = ConsecutiveProbeFailures,
        Alerting = Alerting,
        LastAdaptedAt = LastAdaptedAt,
        LastRateLimitedAt = LastRateLimitedAt,
        RateLimitHits = RateLimitHits,
    };

    /// <summary>
    /// How long until a token is available. <see cref="TimeSpan.Zero"/> means one can be taken now.
    /// </summary>
    public TimeSpan TimeUntilToken(DateTimeOffset now)
    {
        Refill(now);

        // Tolerance, because the refill is floating point: a wait computed to land exactly on one
        // token can land a fraction short, and without this the caller waits again for an
        // interval that rounds to nothing and spins.
        if (Tokens >= 1 - Tolerance)
            return TimeSpan.Zero;

        var rate = EffectiveRatePerSecond;
        if (rate <= 0)
            return TimeSpan.MaxValue;

        var seconds = (1 - Tokens) / rate;
        var ticks = (long)Math.Ceiling(seconds * TimeSpan.TicksPerSecond);

        return TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, ticks));
    }

    /// <summary>Takes a token. Callers check <see cref="TimeUntilToken"/> across the whole chain first.</summary>
    public void Take(DateTimeOffset now)
    {
        Refill(now);
        Tokens = Math.Max(0, Tokens - 1);
    }

    private void Refill(DateTimeOffset now)
    {
        // A clock that moved backwards (a corrected server clock, a restored snapshot) must not
        // mint tokens; it also must not strand the bucket, so the window simply restarts.
        if (now <= TokensAt)
        {
            TokensAt = now;
            return;
        }

        var elapsed = (now - TokensAt).TotalSeconds;
        Tokens = Math.Min(Limits.BurstTokens, Tokens + (elapsed * EffectiveRatePerSecond));
        TokensAt = now;
    }

    /// <summary>
    /// Halts this bucket and schedules its first probe. Called on the most specific bucket that
    /// matched the failing request — never on all VRChat traffic (spec 4.3.1).
    /// </summary>
    public void ColdStop(DateTimeOffset now)
    {
        StoppedUntil = now + _options.ColdStopBase;
        ProbeInFlight = false;
        ConsecutiveProbeFailures = 0;
        Alerting = false;

        // A cold stop that left tokens banked would let the bucket burst the instant it reopened,
        // straight back into the limit it just hit.
        Tokens = 0;
        TokensAt = now;
    }

    /// <summary>The multiplicative half of AIMD, applied to every bucket in the chain.</summary>
    public void ApplyMultiplicativeDecrease(DateTimeOffset now)
    {
        BudgetMultiplier = Math.Max(
            _options.MinimumBudgetMultiplier,
            BudgetMultiplier * _options.DecreaseFactor);

        LastAdaptedAt = now;
        LastRateLimitedAt = now;
        RateLimitHits++;
    }

    /// <summary>
    /// The additive half. Recovery is proportional to elapsed time rather than to the number of
    /// successful calls, so a busy bucket does not recover faster than a quiet one — the thing
    /// being waited out is VRChat's opinion of us, not our own request count.
    /// </summary>
    public void ApplyAdditiveIncrease(DateTimeOffset now)
    {
        if (BudgetMultiplier >= 1.0)
        {
            LastAdaptedAt = now;
            return;
        }

        var hours = (now - LastAdaptedAt).TotalHours;
        if (hours <= 0)
            return;

        BudgetMultiplier = Math.Min(1.0, BudgetMultiplier + (_options.IncreasePerHour * hours));
        LastAdaptedAt = now;
    }

    /// <summary>
    /// True when exactly one probe may be issued now: the wait has elapsed, nothing is already in
    /// flight, and probing has not been abandoned.
    /// </summary>
    public bool IsProbeDue(DateTimeOffset now) =>
        StoppedUntil is { } until && !ProbeInFlight && !Alerting && now >= until;

    /// <summary>
    /// Marks a probe as issued, and immediately schedules the <em>next</em> waiting period.
    /// </summary>
    /// <remarks>
    /// Scheduling the next window before the probe's outcome is known is what makes spec 4.3.2's
    /// crash-loop bound hold. If the process dies between issuing a probe and learning what
    /// happened, the persisted state already says "not before <c>now + period</c>" — so a restart
    /// loop cannot turn one waiting period into a stream of penalty-extending probes.
    /// </remarks>
    public void BeginProbe(DateTimeOffset now)
    {
        ProbeInFlight = true;
        StoppedUntil = now + NextWaitingPeriod(ConsecutiveProbeFailures + 1);

        Tokens = 0;
        TokensAt = now;
    }

    /// <summary>The probe came back clean. Resume, at the reduced budget the 429 imposed.</summary>
    public void ProbeSucceeded(DateTimeOffset now)
    {
        StoppedUntil = null;
        ProbeInFlight = false;
        ConsecutiveProbeFailures = 0;
        Alerting = false;
        Tokens = 0;
        TokensAt = now;

        // The budget stays where the 429 left it, and the recovery clock starts here: waiting out
        // a penalty is not the sustained success that earns budget back.
        LastAdaptedAt = now;
    }

    /// <summary>
    /// Restarts the recovery clock without changing the budget. Time spent waiting out a cold
    /// stop is not time spent succeeding, so it must not silently earn budget back for the
    /// ancestors of the bucket that stopped.
    /// </summary>
    public void ResetRecoveryClock(DateTimeOffset now) => LastAdaptedAt = now;

    /// <summary>
    /// The probe produced no answer — a transport failure, a cancellation, or a process that died
    /// before it could report. The flag clears so the bucket is not frozen forever, but the
    /// waiting period <see cref="BeginProbe"/> already scheduled stands: a probe that might have
    /// reached VRChat has to be paid for as though it did.
    /// </summary>
    public void ProbeInconclusive() => ProbeInFlight = false;

    /// <summary>The probe was rate limited too. Wait longer, and eventually stop guessing.</summary>
    public void ProbeFailed(DateTimeOffset now)
    {
        ConsecutiveProbeFailures++;
        ProbeInFlight = false;
        StoppedUntil = now + NextWaitingPeriod(ConsecutiveProbeFailures);

        if (ConsecutiveProbeFailures >= _options.MaxProbeFailures)
            Alerting = true;
    }

    /// <summary>
    /// The wait before probe number <paramref name="attempt"/>. The first cold stop is the base
    /// period; every failed probe adds one increment, linearly.
    /// </summary>
    private TimeSpan NextWaitingPeriod(int attempt) =>
        _options.ColdStopBase + (_options.ColdStopIncrement * Math.Max(0, attempt));
}
