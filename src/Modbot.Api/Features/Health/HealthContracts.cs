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
/// Reauthenticating, SignInWaiting, NoGroupAccess.</param>
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
/// <param name="SignInWait">
/// Set while Modbot is waiting to sign in to VRChat (spec 4.1.2), and null otherwise. The web app
/// shows a banner on every page for as long as it is set.
/// </param>
/// <param name="LastSignedInAt">When Modbot last signed in to VRChat with the password.</param>
/// <param name="SignInsInLastHour">Requests counted against the sign-in limit in the last hour.</param>
/// <param name="SignInLimit">The most Modbot sends in any rolling hour.</param>
public sealed record GateHealth(
    string State,
    GateStatus Status,
    string Headline,
    int ColdStoppedBuckets,
    DateTimeOffset? ColdStopEndsAt,
    int AlertingBuckets,
    SignInWaitHealth? SignInWait = null,
    DateTimeOffset? LastSignedInAt = null,
    int SignInsInLastHour = 0,
    int SignInLimit = 0);

/// <summary>A wait before Modbot signs in to VRChat again (spec 4.1.2).</summary>
/// <param name="Reason">
/// <c>RateLimitedByVRChat</c> when VRChat refused a sign-in, <c>SignInLimitReached</c> when
/// Modbot's own limit per hour is used up. Both are rate limits and shown the same way.
/// </param>
/// <param name="RetryAt">When Modbot will try again.</param>
/// <param name="SecondsLeft">
/// Whole seconds until <paramref name="RetryAt"/>, measured on the server's clock when this was
/// read. A countdown starts from this rather than from the browser's clock. Zero once the wait is
/// over and the attempt has not finished yet.
/// </param>
public sealed record SignInWaitHealth(
    Modbot.VRChat.Session.SignInWaitReason Reason,
    DateTimeOffset RetryAt,
    int SecondsLeft);

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
    // The Discord bot's own account of itself (foundation §9). Null when no bot is registered
    // in this host; NotConfigured when one is but no token is stored, which is not a fault.
    Modbot.Core.Discord.DiscordBotSnapshot? DiscordBot,
    SweepHealth? MemberSweep,
    SweepHealth? BanSweep,
    DateTimeOffset Now,
    // Channels an enabled route sends to that cannot be posted in (Discord event routes design §6).
    // Empty when every channel is fine or none is set.
    IReadOnlyList<DiscordChannelProblem>? DiscordChannelProblems = null,
    DiscordReadBackHealth? DiscordReadBack = null,
    // Spend limits for everyone or a feature at 80% or more, estimated to be passed this month, or
    // reached (AI chat design §10.7). Empty when none is.
    IReadOnlyList<AiSpendWarningView>? AiSpend = null,
    // AI calls in the last hour: how many failed, how many ran out of time, and which model is
    // answering when the fallback is in use. Null when nothing is worth saying.
    AiCallsHealth? AiCalls = null,
    // The email queue under the daily email limit (accounts and access design §4.4).
    EmailHealth? Email = null,
    // Calendar events that could not be published or opened, and whether the bot lacks Manage
    // Events while an event wants a Discord event (calendar design §3.2, §4). Null when there is
    // nothing to say.
    CalendarHealth? Calendar = null,
    // Moderation rules that stopped themselves after acting far more in an hour than usual
    // (AI moderation design §13.2). Empty when none has.
    IReadOnlyList<PausedRule>? PausedRules = null,
    // The rarer of the two profile reads: the full user object, on its own budget.
    SyncRunSummary? LastUserReadRun = null,
    UserReadHealth? UserReads = null,
    // The log Modbot keeps in its own database, and the copy it sends Modbot Cloud. Null when this
    // host has no log store registered at all.
    LogHealth? Logs = null);

/// <summary>
/// Modbot's own log: what the store is doing, and what is happening to the copy sent to Cloud.
/// </summary>
/// <param name="Storing">The sink is connected to the database and writing.</param>
/// <param name="StoredDropped">
/// Lines thrown away since this Modbot started because the queue was full — the database was slow
/// or unreachable for long enough to fill it. Shown because a log with gaps in it must say so.
/// </param>
/// <param name="StoreError">Why the last write failed, if one did. Null once a write succeeds.</param>
/// <param name="SendingToCloud">Sending the same lines to Modbot Cloud is on and allowed.</param>
/// <param name="CloudAllowed">False when <c>MODBOT_CLOUD_DISABLED</c> is set.</param>
/// <param name="CloudRegistered">This deployment has registered with Cloud.</param>
/// <param name="CloudSentAt">When the last batch reached Cloud.</param>
/// <param name="CloudWaiting">Lines stored but not yet sent.</param>
/// <param name="CloudDropped">Lines given up on because Cloud was unreachable for long, or refused them.</param>
/// <param name="CloudError">Why the last attempt to send failed. Null once one succeeds.</param>
public sealed record LogHealth(
    bool Storing,
    long StoredWritten,
    long StoredDropped,
    DateTimeOffset? StoredAt,
    string? StoreError,
    DateTimeOffset? StoreErrorAt,
    bool SendingToCloud,
    bool CloudAllowed,
    bool CloudRegistered,
    DateTimeOffset? CloudSentAt,
    long CloudWaiting,
    long CloudDropped,
    string? CloudError,
    DateTimeOffset? CloudErrorAt);

/// <summary>
/// What the rarer read is doing: the full user object, read about once a week per person.
/// </summary>
/// <param name="NeverRead">People whose user object has never been read.</param>
/// <param name="OldestReadAt">The least recent user read among people who have had one.</param>
/// <param name="ReadsInLastHour">Requests spent on the users lane in the last hour.</param>
/// <param name="LastRateLimitedAt">When the users lane last answered 429, in this process.</param>
public sealed record UserReadHealth(
    int NeverRead,
    DateTimeOffset? OldestReadAt,
    int ReadsInLastHour,
    DateTimeOffset? LastRateLimitedAt);

/// <summary>A moderation rule that paused itself and is waiting for an operator (design §13.2).</summary>
/// <param name="RuleKind"><c>termList</c> or <c>topic</c>.</param>
public sealed record PausedRule(
    string RuleKind,
    Guid RuleId,
    string RuleName,
    DateTimeOffset PausedAt,
    string? Reason);

/// <param name="MissingManageEvents">An event wants a Discord event and the bot does not hold Manage Events.</param>
public sealed record CalendarHealth(bool MissingManageEvents, IReadOnlyList<CalendarProblem> Problems);

/// <param name="Place"><c>vrchat</c>, <c>discordEvent</c>, <c>channelPost</c>, or <c>instance</c> for an instance that did not open.</param>
public sealed record CalendarProblem(Guid EventId, string Title, string Place, string Error, DateTimeOffset? At);

/// <summary>What AI calls have been doing over the last hour.</summary>
/// <param name="Calls">Calls made in the last hour, whatever came of them.</param>
/// <param name="Errors">Calls the provider refused or that could not be reached.</param>
/// <param name="TimedOut">Calls that ran past their feature's timeout.</param>
/// <param name="Fallbacks">Calls that went to the fallback model because the first one did not answer.</param>
/// <param name="AnsweringModel">
/// The model that answered most recently, when the fallback has answered in the last hour. Null
/// when the main model is doing the answering.
/// </param>
public sealed record AiCallsHealth(int Calls, int Errors, int TimedOut, int Fallbacks, string? AnsweringModel);

/// <summary>Emails waiting under the daily limit, and emails given up on.</summary>
/// <param name="Queued">Messages waiting for room or for their next try.</param>
/// <param name="Failed">Messages the relay refused too many times, in the last few days.</param>
/// <param name="NextSendAt">When the next queued message should go out, or null.</param>
public sealed record EmailHealth(int Queued, int Failed, DateTimeOffset? NextSendAt);

/// <summary>A limit for everyone or for one AI feature that is close to being reached, or reached.</summary>
/// <param name="AppliesTo"><c>everyone</c>, <c>feature</c>, or <c>tokens</c> for a token limit kept from before prices.</param>
/// <param name="Period"><c>day</c> or <c>month</c>.</param>
/// <param name="Unit"><c>money</c> (US dollars) or <c>tokens</c>.</param>
/// <param name="Estimate">The month-end estimate, for a monthly limit.</param>
/// <param name="Reached">Spent is at or over the limit.</param>
/// <param name="PartUnknown">Some of the spend is of a model with no price, so the real figure is higher.</param>
public sealed record AiSpendWarningView(
    string AppliesTo,
    string? Feature,
    string? Label,
    string Period,
    string Unit,
    decimal Limit,
    decimal Spent,
    decimal? Estimate,
    bool Reached,
    bool PartUnknown);

/// <summary>
/// A channel events are sent to that has something wrong with it.
/// </summary>
/// <param name="Name">The channel's name as the bot last saw it, or null when the bot has never seen it.</param>
/// <param name="Missing">Discord's names for the permissions the bot lacks there, of View Channel, Send Messages and Embed Links.</param>
/// <param name="Removed">The channel was deleted in Discord.</param>
/// <param name="LastError">Why the last post was refused, until a post goes through.</param>
public sealed record DiscordChannelProblem(
    string ChannelId,
    string? Name,
    IReadOnlyList<string> Missing,
    bool Removed,
    string? LastError,
    DateTimeOffset? LastErrorAt);

/// <summary>
/// How far the bot has read back through the Discord server's message history (M5 spec §5.1).
/// Null when no server is set.
/// </summary>
/// <param name="Channels">Channels and threads the bot has found it can read.</param>
/// <param name="Finished">Of those, how many are read back as far as they go.</param>
/// <param name="NoAccess">Of the finished, how many stopped because the bot may not read them.</param>
/// <param name="MessagesStored">Messages the read-back stored that were not stored before.</param>
/// <param name="LastError">The most recent problem reading a channel, as a sentence, or null.</param>
public sealed record DiscordReadBackHealth(
    int Channels,
    int Finished,
    int NoAccess,
    long MessagesStored,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    DateTimeOffset? UpdatedAt);
