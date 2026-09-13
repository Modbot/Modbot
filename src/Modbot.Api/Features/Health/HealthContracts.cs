namespace Modbot.Api.Features.Health;

/// <summary>
/// What the operator should do about the gate's current state — which is not the same question as
/// what that state is called.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.3.3: <em>"an operator has to be able to see that Modbot is deliberately slow rather than
/// broken."</em> <c>RateLimited</c> and <c>WafBlocked</c> both stop traffic and look identical from
/// outside — a dot that is not green, and data that is not arriving — and they mean opposite
/// things. A cold stop is spec 4.3.1 working exactly as designed and the correct response is to
/// leave it alone; a WAF block is a broken deployment that will stay broken until somebody
/// configures a proxy.
/// </para>
/// <para>
/// So the posture is computed once, on the server, and travels beside the state name. Leaving the
/// SPA to infer it would put that judgement in the layer least able to make it and most likely to
/// be reimplemented differently on the next screen.
/// </para>
/// </remarks>
public enum GatePosture
{
    /// <summary>Requests are going out. Nothing to do.</summary>
    Working = 1,

    /// <summary>
    /// Stopped on purpose and recovering on its own (spec 4.3.1). Intervening makes it worse:
    /// a retry issued during a penalty extends it.
    /// </summary>
    WaitingOnPurpose = 2,

    /// <summary>Stopped, and waiting will not fix it. Somebody has to act.</summary>
    NeedsOperator = 3,

    /// <summary>No VRChat account configured yet. Finish the wizard.</summary>
    NotConfigured = 4,
}

/// <param name="State">The gate's own state name: Healthy, RateLimited, WafBlocked, Unconfigured,
/// Reauthenticating.</param>
/// <param name="Headline">One sentence for a person, not a state name repeated.</param>
/// <param name="ColdStoppedBuckets">How many buckets are currently refusing to send.</param>
/// <param name="ColdStopEndsAt">
/// The earliest moment any stopped bucket will probe again. Null when nothing is stopped — and
/// deliberately not a countdown: the penalty's real length is not published by VRChat and this is
/// only when Modbot will next try.
/// </param>
/// <param name="AlertingBuckets">
/// Buckets that have exhausted their probes (spec 4.3.1, step 4). Something is wrong that waiting
/// will not fix.
/// </param>
public sealed record GateHealth(
    string State,
    GatePosture Posture,
    string Headline,
    int ColdStoppedBuckets,
    DateTimeOffset? ColdStopEndsAt,
    int AlertingBuckets);

/// <param name="EffectiveRatePerSecond">After the AIMD adaptation, not the configured ceiling.</param>
/// <param name="BudgetMultiplier">
/// 1.0 means the budget is at its ceiling. Below it means a 429 halved it and it is recovering by
/// a small step per hour — hours, not minutes, by design.
/// </param>
public sealed record BucketHealth(
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

/// <param name="Reason">
/// The producer's own sentence for why it chose this interval. A published interval without one
/// leaves "quiet group" and "stuck producer" looking identical.
/// </param>
public sealed record CadenceReport(
    double IntervalSeconds,
    string Reason,
    int ConsecutiveQuietPolls,
    DateTimeOffset DecidedAt);

public sealed record SyncRunSummary(
    string Outcome,
    DateTimeOffset At,
    double DurationSeconds,
    string Summary);

/// <param name="SampleEntryId">
/// One entry id, so the event can be looked up in VRChat rather than taken on Modbot's word.
/// </param>
public sealed record UnmappedEvent(
    string EventType,
    int Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string? SampleEntryId,
    string? SampleDescription);

/// <param name="MissingPrimary">
/// <strong>The finding that matters.</strong> Spellings Modbot treats as real that VRChat does not
/// declare. Non-empty means facts of those types are being lost right now, silently: the fact log
/// looks healthy because Modbot is waiting for a string VRChat never sends.
/// </param>
/// <param name="Unmapped">Declared by VRChat, not recorded by Modbot. A known gap, not a fault.</param>
/// <param name="UnusedAliases">Speculative spellings VRChat does not use. Expected; listed so the
/// other two lists are not read as containing them.</param>
public sealed record VocabularyReport(
    DateTimeOffset CheckedAt,
    IReadOnlyList<string> Declared,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> MissingPrimary,
    IReadOnlyList<string> UnusedAliases,
    bool HasProblem);

/// <param name="SyncRunningInThisProcess">
/// False when no producer is registered in this host — a diagnostic host, or a deployment where
/// sync was deliberately left out. Everything else in the response is then empty because nothing
/// is running, not because everything is idle, and those are different.
/// </param>
/// <param name="AuditLogPolledAt">
/// From the settings row rather than from the in-memory diagnostics, so it survives a restart.
/// The in-memory run report says what happened last in <em>this</em> process.
/// </param>
/// <param name="Now">
/// The server's clock (spec 4.4). Sent so "four minutes ago" is computed against the deployment's
/// own time rather than the browser's, which may be wrong and is not the authority here.
/// </param>
public sealed record SyncHealth(
    GateHealth Gate,
    IReadOnlyList<BucketHealth> Buckets,
    bool SyncRunningInThisProcess,
    CadenceReport? AuditLogCadence,
    SyncRunSummary? LastAuditLogRun,
    SyncRunSummary? LastGroupInfoRun,
    DateTimeOffset? AuditLogPolledAt,
    DateTimeOffset? GroupInfoPolledAt,
    bool AuditLogBackfillComplete,
    DateTimeOffset? AuditLogSyncedThrough,
    bool GroupConfigured,
    IReadOnlyList<UnmappedEvent> UnmappedAuditEvents,
    VocabularyReport? Vocabulary,
    DateTimeOffset Now);
