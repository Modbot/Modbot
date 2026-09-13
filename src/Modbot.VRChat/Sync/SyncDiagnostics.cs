using Modbot.Core.Time;

namespace Modbot.VRChat.Sync;

/// <summary>
/// An audit-log event type Modbot has no fact type for.
/// </summary>
/// <param name="Count">How many have been seen since this process started.</param>
/// <param name="SampleEntryId">
/// One entry id, so an operator can look the real thing up in VRChat rather than take Modbot's
/// word for the shape of it.
/// </param>
/// <param name="SampleDescription">VRChat's own human-readable line for one of them.</param>
public sealed record UnmappedAuditEvent(
    string EventType,
    int Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string? SampleEntryId,
    string? SampleDescription);

/// <summary>
/// The one-off walk through existing history went as far back as VRChat allows, rather than to
/// the end of the log.
/// </summary>
/// <param name="EntriesRead">How many entries the walk had read when it stopped.</param>
public sealed record HistoryHorizon(int EntriesRead, DateTimeOffset ReachedAt);

/// <summary>What the poll rate decided, and why.</summary>
/// <param name="Reason">A sentence for an operator, not a state name.</param>
public sealed record PollRateDecision(
    TimeSpan Interval,
    string Reason,
    int ConsecutiveQuietPolls,
    DateTimeOffset DecidedAt);

/// <summary>
/// The shape of the <c>vrchat_user</c> table, counted once a minute rather than on every pass.
/// </summary>
/// <param name="KnownUsers">Rows: everyone Modbot has ever seen.</param>
/// <param name="NeverRefreshed">Rows whose profile has never been fetched -- the backlog.</param>
/// <param name="NotFound">Rows VRChat answered 404 for.</param>
/// <param name="OldestRefreshedAt">The least recent successful refresh among people who have had one.</param>
public sealed record UserProfileCounts(
    int KnownUsers,
    int NeverRefreshed,
    int NotFound,
    DateTimeOffset? OldestRefreshedAt,
    DateTimeOffset MeasuredAt);

/// <summary>The last thing a producer did.</summary>
public sealed record SyncRunReport(
    SyncOutcome Outcome,
    DateTimeOffset At,
    TimeSpan Duration,
    string Summary);

/// <summary>
/// What the producers would tell an operator about themselves.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.2.3 and 4.3.3: the UI shows real last-sync times per data type and per-bucket health,
/// because an operator has to be able to see that Modbot is deliberately slow rather than broken.
/// An adaptive poll rate makes that worse before it makes it better -- a producer that decided on
/// its own to poll every five minutes is indistinguishable from a stuck one unless it says so.
/// So the decision and its reason are published, not merely logged.
/// </para>
/// <para>
/// The unmapped event types are here for a different reason: they are the one failure mode of the
/// audit-log producer that is otherwise completely silent. Everything looks healthy, facts are
/// being written, and a whole category of moderation action is missing from the log.
/// </para>
/// <para>
/// In-memory and process-scoped. None of it is history -- the history is the fact log -- and a
/// counter that survived a restart would only make "since when" a harder question.
/// </para>
/// </remarks>
public sealed class SyncDiagnostics
{
    private readonly IModbotClock _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, UnmappedAuditEvent> _unmapped = new(StringComparer.Ordinal);

    private PollRateDecision? _pollRate;
    private SyncRunReport? _auditLog;
    private SyncRunReport? _groupInfo;
    private SyncRunReport? _userProfile;
    private UserProfileCounts? _userProfileCounts;
    private DateTimeOffset? _userProfileLastRateLimitedAt;
    private readonly Queue<DateTimeOffset> _refreshTimes = new();
    private HistoryHorizon? _historyHorizon;

    public SyncDiagnostics(IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>The audit-log producer's current interval and the reason it chose it.</summary>
    public PollRateDecision? AuditLogPollRate
    {
        get { lock (_gate) return _pollRate; }
    }

    public SyncRunReport? LastAuditLogRun
    {
        get { lock (_gate) return _auditLog; }
    }

    public SyncRunReport? LastGroupInfoRun
    {
        get { lock (_gate) return _groupInfo; }
    }

    /// <summary>
    /// Set when the catch-up stopped at VRChat's offset cap rather than at the end of the log.
    /// Null when it has not, or when it ran to the end.
    /// </summary>
    /// <remarks>
    /// Published because an operator looking at "catch-up complete" is entitled to know whether
    /// that means the whole log or the most recent 7,500 entries of it. They look identical from
    /// the cursor.
    /// </remarks>
    public HistoryHorizon? HistoryHorizonReached
    {
        get { lock (_gate) return _historyHorizon; }
    }

    public void RecordHistoryHorizon(int entriesRead)
    {
        lock (_gate) _historyHorizon = new HistoryHorizon(entriesRead, _clock.UtcNow);
    }

    /// <summary>
    /// Every audit-log event type seen that Modbot cannot record, worst first.
    /// </summary>
    /// <remarks>
    /// A non-empty list is a bug report waiting to be filed, not an error state: it means either
    /// VRChat has an event type this project has not catalogued, or the name in
    /// <see cref="GroupAuditLogEvents"/> is wrong. Both are fixed in one place.
    /// </remarks>
    public IReadOnlyList<UnmappedAuditEvent> UnmappedAuditEvents
    {
        get
        {
            lock (_gate)
                return _unmapped.Values.OrderByDescending(e => e.Count).ThenBy(e => e.EventType).ToList();
        }
    }

    public void RecordPollRate(PollRateDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (_gate) _pollRate = decision;
    }

    public void RecordAuditLogRun(SyncRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate) _auditLog = report;
    }

    public void RecordGroupInfoRun(SyncRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate) _groupInfo = report;
    }

    public SyncRunReport? LastUserProfileRun
    {
        get { lock (_gate) return _userProfile; }
    }

    /// <summary>The table counts as last measured, or null before the first housekeeping pass.</summary>
    public UserProfileCounts? UserProfileCounts
    {
        get { lock (_gate) return _userProfileCounts; }
    }

    /// <summary>When the users lane last answered 429, in this process.</summary>
    public DateTimeOffset? UserProfileLastRateLimitedAt
    {
        get { lock (_gate) return _userProfileLastRateLimitedAt; }
    }

    /// <summary>
    /// How many profiles were fetched in the last hour -- the number that says whether the lane
    /// is being used, which "last run: quiet" cannot.
    /// </summary>
    public int UserProfileRefreshesInLastHour
    {
        get
        {
            lock (_gate)
            {
                Trim(_clock.UtcNow);
                return _refreshTimes.Count;
            }
        }
    }

    /// <param name="refreshed">Whether the pass spent a request on somebody.</param>
    public void RecordUserProfileRun(SyncRunReport report, bool refreshed)
    {
        ArgumentNullException.ThrowIfNull(report);

        lock (_gate)
        {
            _userProfile = report;

            if (report.Outcome == SyncOutcome.RateLimited)
                _userProfileLastRateLimitedAt = report.At;

            if (refreshed)
            {
                _refreshTimes.Enqueue(report.At);
                Trim(report.At);
            }
        }
    }

    public void RecordUserProfileCounts(UserProfileCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        lock (_gate) _userProfileCounts = counts;
    }

    /// <summary>Drops refresh timestamps older than an hour. The caller holds the lock.</summary>
    private void Trim(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromHours(1);
        while (_refreshTimes.Count > 0 && _refreshTimes.Peek() < cutoff)
            _refreshTimes.Dequeue();
    }

    /// <summary>
    /// Records an event type Modbot could not map.
    /// </summary>
    /// <returns>
    /// True the first time this process sees the type, so the caller can log it once and count it
    /// thereafter. A group that uses a feature Modbot does not know about would otherwise produce
    /// the same warning every eight seconds forever, which is how a real signal gets filtered out
    /// of a log by the person reading it.
    /// </returns>
    public bool RecordUnmappedEvent(string eventType, string? entryId, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        var now = _clock.UtcNow;

        lock (_gate)
        {
            if (_unmapped.TryGetValue(eventType, out var existing))
            {
                _unmapped[eventType] = existing with { Count = existing.Count + 1, LastSeen = now };
                return false;
            }

            _unmapped[eventType] = new UnmappedAuditEvent(eventType, 1, now, now, entryId, description);
            return true;
        }
    }
}
