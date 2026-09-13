namespace Modbot.VRChat.Sync;

/// <summary>
/// How one pass of a producer ended.
/// </summary>
/// <remarks>
/// The poll rate reads this, and every member exists because it implies a different next interval.
/// "Nothing happened because the group is quiet" and "nothing happened because the bucket is
/// cold-stopped" look identical in a count of facts written and could not be more different: one
/// says poll less often, the other says stop until the penalty expires (spec 4.3.1).
/// </remarks>
public enum SyncOutcome
{
    /// <summary>The poll succeeded and there was nothing new. Back off.</summary>
    Quiet,

    /// <summary>The poll succeeded and produced facts. Stay fast; more may be arriving.</summary>
    Produced,

    /// <summary>
    /// No VRChat account, or no managed group, has been configured yet. Not an error -- a fresh
    /// deployment sits here until somebody finishes the wizard.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// A bucket is cold-stopped, or VRChat returned 429. <strong>Nothing is retried</strong>
    /// (spec 4.3.1); the producer goes as slow as it is allowed to until the stop lifts.
    /// </summary>
    RateLimited,

    /// <summary>VRChat, the network, or the database said no. Retry at a reduced rate.</summary>
    Failed,
}

/// <summary>What one audit-log pass read and wrote.</summary>
/// <param name="PagesRead">Requests spent. Each one is a <c>groups.auditlog</c> token.</param>
/// <param name="EntriesRead">Audit entries VRChat returned, duplicates included.</param>
/// <param name="FactsWritten">Entries that became new facts.</param>
/// <param name="AlreadyRecorded">
/// Entries an earlier pass had already recorded. Expected and healthy -- the window deliberately
/// overlaps -- but a number that stays near <see cref="EntriesRead"/> means the overlap is wider
/// than it needs to be.
/// </param>
/// <param name="Unmapped">Entries whose event type Modbot has no fact type for (spec 5.3).</param>
/// <param name="Unusable">Entries missing a target or a timestamp, which cannot become a fact.</param>
/// <param name="Backfilling">True while the one-off walk through existing history is still running.</param>
/// <param name="Drained">
/// True when the pass read its window to the end. The cursor only moves on a drained pass, and a
/// pass that was not drained is the signal that there is more to read right now -- which is what
/// decides whether there is any attention left over for history.
/// </param>
public sealed record AuditLogRunResult(
    SyncOutcome Outcome,
    int PagesRead = 0,
    int EntriesRead = 0,
    int FactsWritten = 0,
    int AlreadyRecorded = 0,
    int Unmapped = 0,
    int Unusable = 0,
    bool Backfilling = false,
    bool Drained = false,
    DateTimeOffset? SyncedThrough = null,
    string? Message = null)
{
    /// <summary>Folds a history page into the live pass that preceded it, for one report.</summary>
    public AuditLogRunResult Plus(AuditLogRunResult other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return this with
        {
            // The worse of the two, in the order the poll rate cares about: a failure or a cold stop
            // on either half is the thing that should decide the next interval, not the half that
            // happened to work.
            Outcome = other.Outcome is SyncOutcome.RateLimited or SyncOutcome.Failed
                ? other.Outcome
                : Outcome is SyncOutcome.Produced || other.Outcome is SyncOutcome.Produced
                    ? SyncOutcome.Produced
                    : Outcome,
            PagesRead = PagesRead + other.PagesRead,
            EntriesRead = EntriesRead + other.EntriesRead,
            FactsWritten = FactsWritten + other.FactsWritten,
            AlreadyRecorded = AlreadyRecorded + other.AlreadyRecorded,
            Unmapped = Unmapped + other.Unmapped,
            Unusable = Unusable + other.Unusable,
            Backfilling = other.Backfilling,
            SyncedThrough = other.SyncedThrough ?? SyncedThrough,
            Message = other.Message ?? Message,
        };
    }
}

/// <summary>What one group-info pass observed.</summary>
/// <param name="Changed">
/// The field names that differed from the last recorded snapshot. Empty on a poll that found the
/// group exactly as it was left, which is the common case and deliberately writes nothing.
/// </param>
/// <param name="Baseline">
/// True when this was the first observation and the fact records the whole snapshot rather than a
/// diff -- the headcount the member series is counted forward from.
/// </param>
public sealed record GroupInfoRunResult(
    SyncOutcome Outcome,
    IReadOnlyList<string>? Changed = null,
    bool Baseline = false,
    string? Message = null)
{
    public IReadOnlyList<string> Changed { get; } = Changed ?? [];
}
