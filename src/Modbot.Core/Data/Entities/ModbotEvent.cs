namespace Modbot.Core.Data.Entities;

/// <summary>
/// One immutable fact. The table is <c>modbot_event</c>, partitioned monthly by
/// <see cref="OccurredAt"/>.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.1 through 5.3, and it is the one part of the design that cannot be retrofitted: a sync
/// that upserts current state destroys history, and no later release can recover what was never
/// written down.
/// </para>
/// <para>
/// <strong>Facts are never mutated and never updated in place.</strong> Rollups are recomputable
/// from facts, so a bug in a metric is a re-run rather than lost data; that invariant only holds
/// while nothing edits a row here. A correction is a new fact that supersedes an old one.
/// </para>
/// <para>
/// The primary key is <c>(id, occurred_at)</c>, not <c>id</c>: PostgreSQL requires the partition
/// key to be part of every unique constraint on a partitioned table.
/// </para>
/// </remarks>
public class ModbotEvent
{
    public long Id { get; set; }

    /// <summary>
    /// When it happened. The lower bound of the window when the time is not exactly known.
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// Null for an exact time; otherwise the upper bound of the window in which it happened.
    /// </summary>
    /// <remarks>
    /// Spec 5.3. When a sync diff notices a member is gone, all that is known is that they left
    /// between the previous sync and this one. Collapsing that into a single timestamp invents
    /// precision and produces fake spikes in hourly charts.
    /// </remarks>
    public DateTimeOffset? OccurredBefore { get; set; }

    /// <summary>
    /// When Modbot learned of it. Recorded by the server from <c>IModbotClock</c>, never taken
    /// from a client, and authoritative for ordering (spec 4.4).
    /// </summary>
    public DateTimeOffset ObservedAt { get; set; }

    public FactType Type { get; set; }

    public FactPlatform SubjectPlatform { get; set; }

    /// <summary>Who or what it happened to. An opaque string -- never parsed, never validated.</summary>
    public string SubjectId { get; set; } = string.Empty;

    public FactPlatform? ActorPlatform { get; set; }

    /// <summary>Who caused it. Null when nobody did, or when it is not known.</summary>
    public string? ActorId { get; set; }

    public string? WorldId { get; set; }

    /// <summary>
    /// Identifies one session of one world, and only within that world -- which is why
    /// <see cref="WorldId"/> is required alongside it. Arbitrary user-controlled text: treat it
    /// as hostile input wherever it is displayed.
    /// </summary>
    public string? InstanceId { get; set; }

    public FactSource Source { get; set; }

    /// <summary>
    /// Type-specific payload, as <c>jsonb</c>. Never null -- an empty object instead, so queries
    /// need no null branch.
    /// </summary>
    /// <remarks>
    /// Secrets are never the payload (spec 5.9.3). A config-change fact records which setting
    /// changed and by whom; for secret-bearing fields it records nothing else, not even a masked
    /// value, because the alternative is a credential history in a table people are encouraged
    /// to read.
    /// </remarks>
    public string Data { get; set; } = "{}";
}
