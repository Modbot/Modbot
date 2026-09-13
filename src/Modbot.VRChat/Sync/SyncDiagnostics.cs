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

/// <summary>What the cadence decided, and why.</summary>
/// <param name="Reason">A sentence for an operator, not a state name.</param>
public sealed record CadenceDecision(
    TimeSpan Interval,
    string Reason,
    int ConsecutiveQuietPolls,
    DateTimeOffset DecidedAt);

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
/// An adaptive cadence makes that worse before it makes it better -- a producer that decided on
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

    private CadenceDecision? _cadence;
    private SyncRunReport? _auditLog;
    private SyncRunReport? _groupInfo;

    public SyncDiagnostics(IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>The audit-log producer's current interval and the reason it chose it.</summary>
    public CadenceDecision? AuditLogCadence
    {
        get { lock (_gate) return _cadence; }
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

    public void RecordCadence(CadenceDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (_gate) _cadence = decision;
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
