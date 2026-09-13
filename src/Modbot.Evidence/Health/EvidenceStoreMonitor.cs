using Modbot.Core.Time;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;

namespace Modbot.Evidence.Health;

/// <summary>What the store marker probe last concluded about the store.</summary>
public enum EvidenceStoreState
{
    /// <summary>No backend has been set up. Uploads are refused; nothing is wrong.</summary>
    NotConfigured,

    /// <summary>The store marker is there and it is ours.</summary>
    Healthy,

    /// <summary>
    /// <strong>Locked.</strong> The store answered and it is not the store Modbot's metadata
    /// describes. Evidence is either gone or was never here.
    /// </summary>
    Unavailable,

    /// <summary>The store did not answer. Transient until proven otherwise; never locks.</summary>
    Unreachable,
}

/// <param name="State">The verdict.</param>
/// <param name="Explanation">One sentence, for a banner an operator has to act on.</param>
/// <param name="ExpectedStoreId">What <c>Settings</c> says the store id is.</param>
/// <param name="FoundStoreId">What was actually in the store, when anything was.</param>
/// <param name="Since">When this state began.</param>
/// <param name="ConsecutiveFailures">How many probes in a row have failed to get an answer.</param>
/// <param name="ShouldAlarm">
/// Whether the operator should be told <em>now</em>. True immediately on a lock; true for an
/// unreachable store only once it has been unreachable for
/// <see cref="EvidenceOptions.TransientFailuresBeforeAlarm"/> probes.
/// </param>
public sealed record EvidenceStoreHealth(
    EvidenceStoreState State,
    string Explanation,
    Guid? ExpectedStoreId,
    Guid? FoundStoreId,
    DateTimeOffset? Since,
    int ConsecutiveFailures,
    bool ShouldAlarm)
{
    /// <summary>
    /// Accepting an upload into a store that has just demonstrated it loses everything is worse
    /// than refusing it.
    /// </summary>
    public bool UploadsAllowed => State is EvidenceStoreState.Healthy;

    /// <summary>
    /// Whether a sweep may delete anything. Sweeping while the lock is on would be deleting on
    /// the authority of a database whose relationship to the store is exactly what is in doubt.
    /// </summary>
    public bool SweepAllowed => State is EvidenceStoreState.Healthy;
}

/// <param name="DetectedAt">When the lock went on.</param>
/// <param name="ResolvedAt">When a matching store marker reappeared, if it ever did.</param>
public sealed record EvidenceStoreIncident(
    DateTimeOffset DetectedAt,
    string Explanation,
    Guid? ExpectedStoreId,
    Guid? FoundStoreId,
    DateTimeOffset? ResolvedAt = null);

/// <summary>
/// Reads the store marker and holds the locked verdict (design sections 8.3 and 8.4).
/// </summary>
/// <remarks>
/// <para>
/// The one thing this class exists to get right is the difference between <em>the store said no</em>
/// and <em>the store said nothing</em>. An absent or foreign store marker is evidence that the bytes
/// are not where the database says they are — an unmounted volume, a fresh bucket, a mistyped
/// prefix — and it locks. A store that failed to answer is a network, a credential or an outage,
/// and it does not. Conflating them either raises a full-width "your evidence is gone" banner
/// every time a bucket hiccups, which teaches operators to dismiss the one banner that matters, or
/// stays quiet through the failure the banner exists for.
/// </para>
/// <para>
/// <strong>Locking is not refusing to start.</strong> Modbot keeps running, and everything
/// unrelated keeps working: bans, audit ingest, Discord, the overlay, analytics. Refusing to start
/// would trade live data collection — which foundation section 5.1 says cannot be filled in later — for
/// a gesture about data that is already lost, hide the message behind a platform's
/// "deployment failed", and lock the operator out of the settings page that is the only place to
/// fix it.
/// </para>
/// <para>
/// <strong>The lock does not clear itself.</strong> It clears when a matching store marker is read
/// again, and the incident stays on the record afterwards. A misconfiguration that quietly fixes
/// itself between two deploys, leaving no trace, is how an operator concludes the warning was
/// spurious.
/// </para>
/// </remarks>
public sealed class EvidenceStoreMonitor
{
    private readonly IEvidenceStore? _store;
    private readonly EvidenceOptions _options;
    private readonly IModbotClock _clock;
    private readonly List<EvidenceStoreIncident> _incidents = [];
    private readonly Lock _gate = new();

    private EvidenceStoreHealth _current;
    private int _consecutiveFailures;

    /// <param name="store">Null when no backend is configured.</param>
    public EvidenceStoreMonitor(IEvidenceStore? store, EvidenceOptions options, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _options = options;
        _clock = clock;

        _current = new EvidenceStoreHealth(
            EvidenceStoreState.NotConfigured,
            "No evidence store has been configured yet.",
            options.StoreId,
            null,
            null,
            0,
            ShouldAlarm: false);
    }

    /// <summary>The last verdict. Read by the upload path, the banner and the sweep.</summary>
    public EvidenceStoreHealth Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    /// <summary>
    /// Every lock this process has seen, resolved or not. The host turns these into facts; they
    /// are kept here so that the banner can name the one that is current.
    /// </summary>
    public IReadOnlyList<EvidenceStoreIncident> Incidents
    {
        get
        {
            lock (_gate)
                return [.. _incidents];
        }
    }

    /// <summary>
    /// Probes the store marker and updates the verdict. Run at startup, and before the first upload
    /// after any store failure.
    /// </summary>
    public async Task<EvidenceStoreHealth> CheckAsync(CancellationToken ct = default)
    {
        if (_store is null || _options.StoreId is not { } expected)
        {
            // Nothing has been set up, so there is nothing the store marker could conclude.
            // Absence only means "wrong store" once there is a record of a right one.
            return Set(new EvidenceStoreHealth(
                EvidenceStoreState.NotConfigured,
                "No evidence store has been configured yet, so uploads are unavailable.",
                _options.StoreId, null, null, 0, ShouldAlarm: false));
        }

        var probe = await _store.ProbeAsync(ct).ConfigureAwait(false);
        var now = _clock.UtcNow;

        switch (probe.Outcome)
        {
            case StoreProbeOutcome.Present when probe.Marker!.StoreId == expected:
                return Resolve(now, probe.Explanation, expected);

            case StoreProbeOutcome.Present:
                return Lock(
                    now,
                    $"This is a different Modbot's evidence store. Expected {expected}, found "
                    + $"{probe.Marker!.StoreId} in {_store.Description}.",
                    expected,
                    probe.Marker.StoreId);

            case StoreProbeOutcome.Absent:
            case StoreProbeOutcome.Malformed:
                return Lock(now, probe.Explanation, expected, null);

            default:
                return Transient(now, probe.Explanation, expected);
        }
    }

    private EvidenceStoreHealth Resolve(DateTimeOffset now, string explanation, Guid expected)
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;

            for (var i = 0; i < _incidents.Count; i++)
            {
                if (_incidents[i].ResolvedAt is null)
                    _incidents[i] = _incidents[i] with { ResolvedAt = now };
            }

            var since = _current.State is EvidenceStoreState.Healthy ? _current.Since ?? now : now;

            _current = new EvidenceStoreHealth(
                EvidenceStoreState.Healthy, explanation, expected, expected, since, 0, ShouldAlarm: false);

            return _current;
        }
    }

    private EvidenceStoreHealth Lock(DateTimeOffset now, string explanation, Guid expected, Guid? found)
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;

            var alreadyLocked = _current.State is EvidenceStoreState.Unavailable;

            if (!alreadyLocked)
                _incidents.Add(new EvidenceStoreIncident(now, explanation, expected, found));

            _current = new EvidenceStoreHealth(
                EvidenceStoreState.Unavailable,
                explanation,
                expected,
                found,
                alreadyLocked ? _current.Since : now,
                0,

                // Immediately, on every channel, the first time. Repeating it on every probe would
                // be the same mistake as a banner nobody reads.
                ShouldAlarm: !alreadyLocked);

            return _current;
        }
    }

    private EvidenceStoreHealth Transient(DateTimeOffset now, string explanation, Guid expected)
    {
        lock (_gate)
        {
            _consecutiveFailures++;

            // A locked store that has since stopped answering stays locked. Silence is not
            // evidence that the earlier finding was wrong.
            if (_current.State is EvidenceStoreState.Unavailable)
            {
                _current = _current with { ConsecutiveFailures = _consecutiveFailures, ShouldAlarm = false };
                return _current;
            }

            var since = _current.State is EvidenceStoreState.Unreachable ? _current.Since ?? now : now;

            _current = new EvidenceStoreHealth(
                EvidenceStoreState.Unreachable,
                explanation,
                expected,
                null,
                since,
                _consecutiveFailures,
                ShouldAlarm: _consecutiveFailures == _options.TransientFailuresBeforeAlarm);

            return _current;
        }
    }

    private EvidenceStoreHealth Set(EvidenceStoreHealth health)
    {
        lock (_gate)
        {
            _current = health;
            return health;
        }
    }
}
