using System.Text.Json.Serialization;

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
/// So the status is computed once, on the server, and travels beside the state name. Leaving the
/// SPA to infer it would put that judgement in the layer least able to make it and most likely to
/// be reimplemented differently on the next screen.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<GateStatus>))]
public enum GateStatus
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
    GateStatus Status,
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
public sealed record PollRateReport(
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

/// <summary>
/// The one-off walk through existing history stopped where VRChat stops paging, not at the end
/// of the log. "Catch-up complete" then means the most recent entries, not all of them.
/// </summary>
public sealed record HistoryHorizonReport(int EntriesRead, DateTimeOffset ReachedAt);

/// <summary>
/// What the profile sync is doing: how many people it knows, how many are waiting and why, and
/// how fast the lane is being used.
/// </summary>
/// <param name="KnownUsers">Rows in <c>vrchat_user</c>: everyone Modbot has ever seen.</param>
/// <param name="NeverRefreshed">People whose profile has never been fetched -- the backlog.</param>
/// <param name="NotFound">People VRChat answered 404 for.</param>
/// <param name="OldestRefreshedAt">The least recent successful refresh among people who have had one.</param>
/// <param name="Waiting">Queue entries right now, in this process.</param>
/// <param name="WaitingByReason">The same, per tier -- <c>SeenInInstance</c>, <c>OpenedInModbot</c>, and so on.</param>
/// <param name="RefreshingUserId">Whose profile is being fetched at this moment, if anyone's.</param>
/// <param name="RefreshesInLastHour">Requests spent on the users lane in the last hour.</param>
/// <param name="LastRateLimitedAt">When the lane last answered 429, in this process.</param>
/// <param name="CountedAt">When the table counts were last measured; they are refreshed about once a minute, not per request.</param>
public sealed record UserProfileHealth(
    int KnownUsers,
    int NeverRefreshed,
    int NotFound,
    DateTimeOffset? OldestRefreshedAt,
    int Waiting,
    IReadOnlyDictionary<string, int> WaitingByReason,
    string? RefreshingUserId,
    int RefreshesInLastHour,
    DateTimeOffset? LastRateLimitedAt,
    DateTimeOffset? CountedAt);

/// <summary>
/// Where a member or ban sweep has got to.
/// </summary>
/// <param name="Phase"><c>sweeping</c>, <c>resting</c>, <c>cold-stopped</c>, <c>retrying</c> or <c>idle</c>, from the service's last decision.</param>
/// <param name="LastCompletedAt">When the last full sweep finished, from the settings row so it survives a restart.</param>
/// <param name="StartedAt">When the sweep in progress started. Null between sweeps.</param>
/// <param name="Offset">How far into the list the sweep in progress has read.</param>
/// <param name="Count">How many the last full sweep listed.</param>
/// <param name="PagesWalked">Requests spent on the sweep in progress, or the last one.</param>
/// <param name="RowsChanged">Rows that sweep inserted, updated or marked gone.</param>
/// <param name="FactsWritten">Inferred facts recorded since this process started.</param>
/// <param name="FactsDeduplicated">Inferred facts dropped because the audit log had already recorded the event.</param>
/// <param name="NextPassAt">When the service will next do something.</param>
/// <param name="ColdStopped">Whether this list's bucket is cold-stopped right now.</param>
/// <param name="PolledAt">When the sweep last completed a pass of any kind.</param>
public sealed record SweepHealth(
    string Phase,
    DateTimeOffset? LastCompletedAt,
    DateTimeOffset? StartedAt,
    int Offset,
    int Count,
    int PagesWalked,
    int RowsChanged,
    int FactsWritten,
    int FactsDeduplicated,
    DateTimeOffset? NextPassAt,
    bool ColdStopped,
    DateTimeOffset? PolledAt,
    SyncRunSummary? LastRun);

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
    PollRateReport? AuditLogPollRate,
    SyncRunSummary? LastAuditLogRun,
    SyncRunSummary? LastGroupInfoRun,
    SyncRunSummary? LastUserProfileRun,
    DateTimeOffset? AuditLogPolledAt,
    DateTimeOffset? GroupInfoPolledAt,
    DateTimeOffset? UserProfilePolledAt,
    bool AuditLogCatchUpComplete,
    DateTimeOffset? AuditLogSyncedThrough,
    bool GroupConfigured,
    IReadOnlyList<UnmappedEvent> UnmappedAuditEvents,
    HistoryHorizonReport? AuditLogHistoryHorizon,
    UserProfileHealth? UserProfiles,
    SweepHealth? MemberSweep,
    SweepHealth? BanSweep,
    DateTimeOffset Now);
