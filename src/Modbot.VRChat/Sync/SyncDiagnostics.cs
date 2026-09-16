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
/// <param name="NeverUserRead">Rows whose full user object has never been read.</param>
/// <param name="OldestUserReadAt">The least recent user read among people who have had one.</param>
public sealed record UserProfileCounts(
    int KnownUsers,
    int NeverRefreshed,
    int NotFound,
    DateTimeOffset? OldestRefreshedAt,
    DateTimeOffset MeasuredAt,
    int NeverUserRead = 0,
    DateTimeOffset? OldestUserReadAt = null);

/// <summary>
/// Where a member or ban sweep has got to, in this process.
/// </summary>
/// <param name="Phase">
/// A word for an operator: <c>sweeping</c>, <c>resting</c>, <c>cold-stopped</c>, <c>retrying</c>,
/// <c>idle</c>. A sweep that is deliberately resting for fifteen minutes and one that is stuck look
/// identical from "last ran 9 minutes ago" alone.
/// </param>
/// <param name="PagesWalked">Requests spent on the sweep in progress, or on the last one if none is.</param>
/// <param name="RowsChanged">Rows inserted, updated or marked gone by that sweep.</param>
/// <param name="FactsWritten">Inferred facts recorded since this process started.</param>
/// <param name="FactsDeduplicated">Inferred facts dropped because the audit log got there first, since this process started.</param>
/// <param name="NextPassAt">When the producer will next do something, on the server's clock.</param>
public sealed record SweepProgress(
    string Phase,
    int PagesWalked,
    int RowsChanged,
    int FactsWritten,
    int FactsDeduplicated,
    DateTimeOffset? NextPassAt,
    DateTimeOffset? LastCompletedAt);

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
    private SyncRunReport? _memberSweep;
    private SyncRunReport? _banSweep;
    private SweepProgress _memberProgress = new("idle", 0, 0, 0, 0, null, null);
    private SweepProgress _banProgress = new("idle", 0, 0, 0, 0, null, null);
    private UserProfileCounts? _userProfileCounts;
    private DateTimeOffset? _userProfileLastRateLimitedAt;
    private readonly Queue<DateTimeOffset> _refreshTimes = new();
    private SyncRunReport? _userRead;
    private DateTimeOffset? _userReadLastRateLimitedAt;
    private readonly Queue<DateTimeOffset> _userReadTimes = new();
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

    public SyncRunReport? LastMemberSweepRun
    {
        get { lock (_gate) return _memberSweep; }
    }

    public SyncRunReport? LastBanSweepRun
    {
        get { lock (_gate) return _banSweep; }
    }

    /// <summary>Where the member sweep has got to, and what it is doing next.</summary>
    public SweepProgress MemberSweep
    {
        get { lock (_gate) return _memberProgress; }
    }

    public SweepProgress BanSweep
    {
        get { lock (_gate) return _banProgress; }
    }

    /// <summary>
    /// Records one pass of the member sweep: the run report, and the running counts for the
    /// sweep it belongs to. A completed sweep resets the per-sweep counts; the fact counts run
    /// for the life of the process.
    /// </summary>
    public void RecordMemberSweepRun(SyncRunReport report, SweepRunResult result)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            _memberSweep = report;
            _memberProgress = Advance(_memberProgress, result, report.At);
        }
    }

    public void RecordBanSweepRun(SyncRunReport report, SweepRunResult result)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            _banSweep = report;
            _banProgress = Advance(_banProgress, result, report.At);
        }
    }

    /// <summary>What the service decided to do next, so the screen can say when rather than only how long ago.</summary>
    public void RecordMemberSweepNext(string phase, DateTimeOffset nextPassAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        lock (_gate) _memberProgress = _memberProgress with { Phase = phase, NextPassAt = nextPassAt };
    }

    public void RecordBanSweepNext(string phase, DateTimeOffset nextPassAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        lock (_gate) _banProgress = _banProgress with { Phase = phase, NextPassAt = nextPassAt };
    }

    private static SweepProgress Advance(SweepProgress progress, SweepRunResult result, DateTimeOffset at)
    {
        // A pass that starts a new sweep begins the per-sweep counts again. A completed sweep
        // keeps its final numbers on screen until the next one starts, so "walked 51 pages" is
        // readable during the rest rather than flashing to zero.
        var pages = result.SweepStarted ? 0 : progress.PagesWalked;
        var rows = result.SweepStarted ? 0 : progress.RowsChanged;

        return progress with
        {
            PagesWalked = pages + result.PagesRead,
            RowsChanged = rows + result.RowsChanged,
            FactsWritten = progress.FactsWritten + result.FactsWritten,
            FactsDeduplicated = progress.FactsDeduplicated + result.FactsDeduplicated,
            LastCompletedAt = result.SweepComplete ? at : progress.LastCompletedAt,
        };
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
                Trim(_refreshTimes, _clock.UtcNow);
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
                Trim(_refreshTimes, report.At);
            }
        }
    }

    public void RecordUserProfileCounts(UserProfileCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        lock (_gate) _userProfileCounts = counts;
    }

    // ── The rarer read: the full user object, on its own budget and its own lane ──────────

    /// <summary>What the last read of a full user object did, or null if none has run yet.</summary>
    public SyncRunReport? LastUserReadRun
    {
        get { lock (_gate) return _userRead; }
    }

    /// <summary>When the users lane last answered 429, in this process.</summary>
    public DateTimeOffset? UserReadLastRateLimitedAt
    {
        get { lock (_gate) return _userReadLastRateLimitedAt; }
    }

    /// <summary>
    /// How many user objects were read in the last hour. Its own count, because the two reads
    /// have their own budgets and a number that mixed them would hide one behind the other.
    /// </summary>
    public int UserReadsInLastHour
    {
        get
        {
            lock (_gate)
            {
                Trim(_userReadTimes, _clock.UtcNow);
                return _userReadTimes.Count;
            }
        }
    }

    /// <param name="read">Whether a request was actually spent on somebody.</param>
    public void RecordUserRead(SyncRunReport report, bool read)
    {
        ArgumentNullException.ThrowIfNull(report);

        lock (_gate)
        {
            _userRead = report;

            if (report.Outcome == SyncOutcome.RateLimited)
                _userReadLastRateLimitedAt = report.At;

            if (read)
            {
                _userReadTimes.Enqueue(report.At);
                Trim(_userReadTimes, report.At);
            }
        }
    }

    /// <summary>Drops timestamps older than an hour. The caller holds the lock.</summary>
    private static void Trim(Queue<DateTimeOffset> times, DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromHours(1);
        while (times.Count > 0 && times.Peek() < cutoff)
            times.Dequeue();
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
