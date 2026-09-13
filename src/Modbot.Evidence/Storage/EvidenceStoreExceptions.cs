namespace Modbot.Evidence.Storage;

/// <summary>
/// The store has demonstrated that it is not the store Modbot's metadata describes, and the latch
/// is on (design section 8.4).
/// </summary>
/// <remarks>
/// Accepting an upload into a store that has just shown it loses everything is worse than refusing
/// it, so this is thrown at the beginning of an upload rather than discovered at the end of one.
/// It is deliberately <em>not</em> thrown by anything unrelated to evidence: bans, audit ingest,
/// Discord, the overlay and analytics all keep working while this state is latched.
/// </remarks>
public sealed class EvidenceStoreUnavailableException : Exception
{
    public EvidenceStoreUnavailableException(string message) : base(message) { }

    public EvidenceStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }

    public EvidenceStoreUnavailableException()
        : base("The evidence store is unavailable.") { }
}

/// <summary>
/// A commit was asked for and the staged bytes are not there.
/// </summary>
/// <remarks>
/// Ordinary rather than alarming: a moderator who closed the tab mid-upload, or a staging object
/// the sweep already took after its grace period. Nothing was attached to a report, because
/// nothing is attached until commit completes.
/// </remarks>
public sealed class EvidenceStagingNotFoundException : Exception
{
    public EvidenceStagingNotFoundException(string message) : base(message) { }

    public EvidenceStagingNotFoundException(string message, Exception innerException)
        : base(message, innerException) { }

    public EvidenceStagingNotFoundException()
        : base("The staged upload is no longer in the store.") { }
}
