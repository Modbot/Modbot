using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.Pacing;
using Serilog;

namespace Modbot.VRChat.RateLimiting;

/// <summary>
/// The hierarchical token buckets, held in one process and persisted to the database.
/// </summary>
/// <remarks>
/// <para>
/// Prevention is the mechanism; recovery is damage control (spec 4.3.1). Almost all of this class
/// is the prevention half — buckets that pace, a lane that serialises, a priority that allocates.
/// The recovery half is deliberately dull: stop, wait a long time, probe once, and never, ever
/// retry.
/// </para>
/// <para>
/// Everything it decides is written to <see cref="IRateLimitStore"/> before it acts on it, because
/// the process this runs in can be killed at any moment and a restart that forgot a live penalty
/// would walk straight back into it (spec 4.3.2).
/// </para>
/// </remarks>
public sealed class InProcessRateLimiter : IRateLimiter
{
    private readonly RateLimitOptions _options;
    private readonly IRateLimitStore _store;
    private readonly IModbotClock _clock;
    private readonly IDelayScheduler _delays;
    private readonly ISyncPacingSource? _pacing;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _state = new(1, 1);
    private readonly Dictionary<string, PriorityGate> _lanes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TokenBucket> _buckets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string EndpointClass, string? ResourceId)> _identity =
        new(StringComparer.Ordinal);

    private bool _loaded;
    private DateTimeOffset _lastFlush;

    /// <summary>The operator's rates, as last applied to the buckets.</summary>
    private SyncPacing _applied;

    /// <summary>-1 until the first apply, so configuration is always applied at least once.</summary>
    private long _appliedVersion = -1;

    public InProcessRateLimiter(
        IRateLimitStore store,
        IModbotClock clock,
        IDelayScheduler delays,
        RateLimitOptions? options = null,
        ISyncPacingSource? pacing = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(delays);

        _store = store;
        _clock = clock;
        _delays = delays;
        _options = options ?? new RateLimitOptions();
        _pacing = pacing;
        _applied = SyncPacing.Resolve(SyncPacingDocument.Empty, _options.Classes);
        _logger = logger ?? Log.Logger;
    }

    public async Task<IRateLimitLease> AcquireAsync(
        VRChatEndpoint endpoint,
        VRChatCallPriority priority = VRChatCallPriority.Background,
        CancellationToken ct = default)
    {
        var limits = ResolveClass(endpoint.Class);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        await EnsureConfiguredAsync(ct).ConfigureAwait(false);

        var gate = Lane(limits.Lane);
        var release = await gate.EnterAsync(priority, ct).ConfigureAwait(false);

        try
        {
            while (true)
            {
                TimeSpan wait;

                await _state.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var now = _clock.UtcNow;
                    var chain = Chain(endpoint, limits, now);

                    if (StoppedIn(chain) is { } blocked)
                    {
                        // Deny rather than wait. A cold stop has no known end -- VRChat sends no
                        // Retry-After (spec 4.3) -- so queueing here would hold a caller for a
                        // quarter of an hour with nothing to tell the operator.
                        if (!AllProbesDue(chain, now))
                        {
                            var denial = Denial(blocked, now);
                            await FlushAsync(force: false, ct).ConfigureAwait(false);
                            release.Dispose();
                            return new Lease(this, endpoint, chain, isProbe: false, denial, release: null);
                        }

                        foreach (var bucket in chain.Where(b => b.IsColdStopped))
                        {
                            bucket.BeginProbe(now);
                            _logger
                                .ForContext(LogArea.Name, LogArea.Http)
                                .Warning(
                                    "Probing rate-limited bucket {Bucket}; next window opens {NextProbe:u}",
                                    bucket.Name, bucket.StoppedUntil);
                        }

                        // Written before the probe is issued, so a crash between the two cannot
                        // buy a second probe in the same waiting period (spec 4.3.2).
                        await FlushAsync(force: true, ct).ConfigureAwait(false);
                        return new Lease(this, endpoint, chain, isProbe: true, denial: null, release);
                    }

                    wait = chain.Max(b => b.TimeUntilToken(now));
                    if (wait <= TimeSpan.Zero)
                    {
                        foreach (var bucket in chain)
                            bucket.Take(now);

                        await FlushAsync(force: false, ct).ConfigureAwait(false);
                        return new Lease(this, endpoint, chain, isProbe: false, denial: null, release);
                    }
                }
                finally
                {
                    _state.Release();
                }

                await _delays.DelayAsync(wait, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            release.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<RateLimitBucketHealth>> DescribeAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        await EnsureConfiguredAsync(ct).ConfigureAwait(false);
        await _state.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return [.. _buckets.Values.Select(b =>
            {
                var (endpointClass, resourceId) = _identity[b.Name];
                return new RateLimitBucketHealth(
                    b.Name,
                    endpointClass,
                    resourceId,
                    b.EffectiveRatePerSecond,
                    b.BudgetMultiplier,
                    b.IsColdStopped,
                    b.StoppedUntil,
                    b.Alerting,
                    b.RateLimitHits,
                    b.LastRateLimitedAt);
            })];
        }
        finally
        {
            _state.Release();
        }
    }

    private async ValueTask ReportAsync(
        VRChatEndpoint endpoint,
        IReadOnlyList<TokenBucket> chain,
        bool isProbe,
        int statusCode,
        CancellationToken ct)
    {
        await _state.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;

            if (statusCode == 429)
            {
                RecordRateLimit(endpoint, chain, isProbe, now);
            }
            else if (statusCode >= 200)
            {
                // Anything that came back with a status other than 429 says this bucket is not
                // rate limited right now. A 403 is a permissions problem, not a pacing one.
                if (isProbe)
                {
                    foreach (var bucket in chain)
                    {
                        if (!bucket.IsColdStopped)
                        {
                            // An ancestor that was never stopped still issued nothing while its
                            // descendant was cold, so it has not earned anything back either.
                            bucket.ResetRecoveryClock(now);
                            continue;
                        }

                        bucket.ProbeSucceeded(now);
                        _logger
                            .ForContext(LogArea.Name, LogArea.Http)
                            .Information(
                                "Bucket {Bucket} resumed at {Rate:0.###} req/s after a successful probe",
                                bucket.Name, bucket.EffectiveRatePerSecond);
                    }
                }
                else if (statusCode is < 300)
                {
                    foreach (var bucket in chain)
                        bucket.ApplyAdditiveIncrease(now);
                }
            }
            else if (isProbe)
            {
                // No response at all. Not evidence either way, so the probe is neither a failure
                // nor a success -- but the waiting period BeginProbe already scheduled stands.
                foreach (var bucket in chain.Where(b => b.ProbeInFlight))
                    bucket.ProbeInconclusive();
            }

            await FlushAsync(force: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _state.Release();
        }
    }

    private void RecordRateLimit(
        VRChatEndpoint endpoint,
        IReadOnlyList<TokenBucket> chain,
        bool isProbe,
        DateTimeOffset now)
    {
        var mostSpecific = chain[^1];

        if (isProbe)
        {
            foreach (var bucket in chain.Where(b => b.IsColdStopped))
            {
                bucket.ProbeFailed(now);
                if (bucket.Alerting)
                {
                    _logger
                        .ForContext(LogArea.Name, LogArea.Http)
                        .Error(
                            "Bucket {Bucket} is still rate limited after {Failures} probes; leaving it stopped",
                            bucket.Name, bucket.ConsecutiveProbeFailures);
                }
            }
        }
        else
        {
            mostSpecific.ColdStop(now);
            _logger
                .ForContext(LogArea.Name, LogArea.Http)
                .Warning(
                    "429 on {Endpoint}; cold stop on {Bucket} until {Until:u}. Nothing will be issued on it until then",
                    endpoint.ToString(), mostSpecific.Name, mostSpecific.StoppedUntil);
        }

        // Every ancestor takes the multiplicative decrease: a 429 anywhere is evidence the whole
        // estimate is optimistic, and may mean an unseen account-wide limit is close.
        foreach (var bucket in chain)
            bucket.ApplyMultiplicativeDecrease(now);

        // Including the global backstop for classes that do not otherwise pass through it. Spec
        // 4.2.5 is explicit: users.read is exempt from the ceiling but not from the evidence.
        var global = GlobalBucket(now);
        if (!chain.Contains(global))
            global.ApplyMultiplicativeDecrease(now);
    }

    private RateLimitClassOptions ResolveClass(string endpointClass)
    {
        if (_options.Classes.TryGetValue(endpointClass, out var limits))
            return limits;

        // Spec 4.3.4 is a standing instruction, and this is where it is enforced: a rate limit for
        // a new endpoint is a question to ask, never a value to infer from a neighbour.
        throw new InvalidOperationException(
            $"No rate-limit budget is configured for endpoint class '{endpointClass}'. " +
            "Ask about the endpoint's rate limit and add it to VRChatRateLimits (spec 4.3.4); " +
            "do not reuse a neighbouring class's budget.");
    }

    private PriorityGate Lane(string lane)
    {
        lock (_lanes)
        {
            if (!_lanes.TryGetValue(lane, out var gate))
            {
                gate = new PriorityGate();
                _lanes[lane] = gate;
            }

            return gate;
        }
    }

    private List<TokenBucket> Chain(VRChatEndpoint endpoint, RateLimitClassOptions limits, DateTimeOffset now)
    {
        var chain = new List<TokenBucket>(3);

        if (limits.CountsAgainstGlobal)
            chain.Add(GlobalBucket(now));

        chain.Add(Bucket(limits.Name, limits, limits.Name, resourceId: null, now));

        if (limits.ResourceScoped && !string.IsNullOrEmpty(endpoint.ResourceId))
        {
            chain.Add(Bucket(
                $"{limits.Name}:{endpoint.ResourceId}", limits, limits.Name, endpoint.ResourceId, now));
        }

        return chain;
    }

    private TokenBucket GlobalBucket(DateTimeOffset now) =>
        Bucket(
            VRChatEndpointClass.Global,
            _options.Classes[VRChatEndpointClass.Global],
            VRChatEndpointClass.Global,
            resourceId: null,
            now);

    private TokenBucket Bucket(
        string name, RateLimitClassOptions limits, string endpointClass, string? resourceId, DateTimeOffset now)
    {
        if (_buckets.TryGetValue(name, out var bucket))
            return bucket;

        bucket = new TokenBucket(name, limits, _options, now);

        // Configured as it is created, not only when the operator next writes. A resource bucket
        // comes into existence the first time its group id is called, which is long after the
        // rates were set, and a bucket that ran at the default until somebody touched the screen
        // again would make the setting look like it had not worked.
        bucket.Configure(_applied.CeilingFor(endpointClass), _applied.BudgetFraction);

        _buckets[name] = bucket;
        _identity[name] = (endpointClass, resourceId);

        return bucket;
    }

    private static TokenBucket? StoppedIn(IReadOnlyList<TokenBucket> chain)
    {
        // Most specific first: the one an operator needs named is the one that actually stopped.
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].IsColdStopped)
                return chain[i];
        }

        return null;
    }

    private static bool AllProbesDue(IReadOnlyList<TokenBucket> chain, DateTimeOffset now) =>
        chain.Where(b => b.IsColdStopped).All(b => b.IsProbeDue(now));

    private static RateLimitDenial Denial(TokenBucket bucket, DateTimeOffset now)
    {
        var reason = bucket.Alerting
            ? RateLimitDenialReason.Alerting
            : bucket.ProbeInFlight
                ? RateLimitDenialReason.ProbeInFlight
                : RateLimitDenialReason.ColdStop;

        var retryAfter = bucket.StoppedUntil is { } until && until > now
            ? until - now
            : TimeSpan.Zero;

        return new RateLimitDenial(bucket.Name, reason, retryAfter);
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _loaded))
            return;

        await _state.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded)
                return;

            var now = _clock.UtcNow;

            foreach (var record in await _store.LoadAsync(ct).ConfigureAwait(false))
            {
                if (!_options.Classes.TryGetValue(record.EndpointClass, out var limits))
                    continue;

                var bucket = Bucket(record.Name, limits, record.EndpointClass, record.ResourceId, now);
                bucket.Restore(record);

                // A probe that was in flight when the process died has an outcome nobody will ever
                // learn. Clearing the flag unblocks the bucket; the waiting period BeginProbe
                // already wrote keeps the restart from probing again straight away, which is the
                // whole point of writing it first (spec 4.3.2).
                if (bucket.ProbeInFlight)
                    bucket.ProbeInconclusive();

                if (bucket.IsColdStopped)
                {
                    _logger
                        .ForContext(LogArea.Name, LogArea.Http)
                        .Warning(
                            "Resuming cold stop on {Bucket} from persisted state; next probe not before {Until:u}",
                            bucket.Name, bucket.StoppedUntil);
                }
            }

            _lastFlush = now;
            _loaded = true;
        }
        finally
        {
            _state.Release();
        }
    }

    /// <summary>
    /// Picks up a rate the operator has changed, without a restart (spec 4.2.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This must not disturb penalty state.</strong> It moves the ceiling and the
    /// fraction and nothing else: not <c>StoppedUntil</c>, not the AIMD multiplier, not the probe
    /// counters. Spec 4.3.2 persists those precisely so that a restart cannot be used to clear a
    /// cold stop, and a settings page that cleared one by writing a budget would be the same hole
    /// reopened from the other side — with the difference that anyone could reach it, and that
    /// clearing a stop is exactly what an operator watching a slow sync would be tempted to try.
    /// </para>
    /// <para>
    /// The version check keeps this off the hot path: an acquisition where nothing has changed
    /// does one cached read and one integer comparison.
    /// </para>
    /// </remarks>
    private async Task EnsureConfiguredAsync(CancellationToken ct)
    {
        if (_pacing is null)
            return;

        var pacing = await _pacing.CurrentAsync(ct).ConfigureAwait(false);
        var version = _pacing.Version;

        if (Interlocked.Read(ref _appliedVersion) == version)
            return;

        await _state.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref _appliedVersion) == version)
                return;

            // Re-resolved against this limiter's own budgets rather than taken as handed over:
            // the caps a rate is clamped to belong to the RateLimitOptions in force here, and a
            // test host or a future per-deployment budget table would otherwise be clamped
            // against somebody else's numbers.
            _applied = SyncPacing.Resolve(pacing.Document, _options.Classes);

            foreach (var bucket in _buckets.Values)
            {
                var (endpointClass, _) = _identity[bucket.Name];
                bucket.Configure(_applied.CeilingFor(endpointClass), _applied.BudgetFraction);
            }

            Interlocked.Exchange(ref _appliedVersion, version);

            // Written through, so the rates survive a restart even on a deployment whose settings
            // row is unreadable at boot.
            await FlushAsync(force: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _state.Release();
        }
    }

    /// <summary>Persists bucket state. The caller holds <see cref="_state"/>.</summary>
    private async Task FlushAsync(bool force, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (!force && now - _lastFlush < _options.StateFlushInterval)
            return;

        _lastFlush = now;

        var records = _buckets.Values
            .Select(b =>
            {
                var (endpointClass, resourceId) = _identity[b.Name];
                return b.ToRecord(endpointClass, resourceId);
            })
            .ToList();

        await _store.SaveAsync(records, ct).ConfigureAwait(false);
    }

    private sealed class Lease : IRateLimitLease
    {
        private readonly InProcessRateLimiter _limiter;
        private readonly VRChatEndpoint _endpoint;
        private readonly IReadOnlyList<TokenBucket> _chain;
        private IDisposable? _release;
        private bool _reported;

        public Lease(
            InProcessRateLimiter limiter,
            VRChatEndpoint endpoint,
            IReadOnlyList<TokenBucket> chain,
            bool isProbe,
            RateLimitDenial? denial,
            IDisposable? release)
        {
            _limiter = limiter;
            _endpoint = endpoint;
            _chain = chain;
            _release = release;

            IsProbe = isProbe;
            Denial = denial;
            Tokens = chain.Count == 0 ? 0 : chain[^1].Tokens;
        }

        public bool IsAcquired => Denial is null;

        public RateLimitDenial? Denial { get; }

        public bool IsProbe { get; }

        public double Tokens { get; }

        public ValueTask ReportAsync(int statusCode, CancellationToken ct = default)
        {
            if (!IsAcquired || _reported)
                return ValueTask.CompletedTask;

            _reported = true;
            return _limiter.ReportAsync(_endpoint, _chain, IsProbe, statusCode, ct);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _release, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
