using Modbot.Evidence.Health;
using Modbot.Evidence.Storage;

namespace Modbot.Evidence.Upload;

/// <param name="Destroyed">Whether the bytes are gone.</param>
/// <param name="BlockedByReports">
/// The case files still holding these bytes, by id. Empty when <paramref name="Destroyed"/>.
/// </param>
/// <param name="Message">What to tell the administrator.</param>
public sealed record DestroyResult(bool Destroyed, IReadOnlyList<string> BlockedByReports, string Message);

/// <summary>
/// Deletes evidence bytes, and refuses to when a case file still holds them
/// (design section 6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Refcounted, because deduplication makes it have to be.</strong> Two moderators
/// attaching the same clip to two case files store one object. If destroying it for one case file
/// deleted that object, the other moderator's evidence would vanish because somebody tidied up an
/// unrelated case file — easy to miss while writing the happy path, and catastrophic when
/// discovered by the person who needed it.
/// </para>
/// <para>
/// So the bytes go only when no case file holds them, and an administrator who tries sooner is told
/// which case files are in the way, by name, before anything happens. The remedy is to take it off
/// those case files first, which is a reversible act; this one is not.
/// </para>
/// <para>
/// The case file the destroy is being done from is the one exception: it is named in
/// <c>ignoreReports</c> and taken off by <c>beforeDelete</c>, after the check and before the first
/// byte goes, so a destroy from the case file that holds the file is one act rather than two.
/// </para>
/// <para>
/// <strong>There is no undo and Modbot does not pretend otherwise.</strong> Railway Buckets has no
/// object versioning, R2 and Wasabi have it only if enabled, and a filesystem unlink is a
/// filesystem unlink. A trash can Modbot could not implement on the backend the operator is
/// actually running would be a lie told at the worst moment.
/// </para>
/// <para>
/// What survives is everything except the bytes: the hash, the size, the content type, who
/// destroyed it and why. "This case had a video and an administrator deleted it on 4 March" has to
/// remain answerable forever, because the alternative is a case file that looks like it never had
/// evidence at all.
/// </para>
/// </remarks>
public sealed class EvidenceDestroyer
{
    private readonly IEvidenceStore _store;
    private readonly IEvidenceMetadata _metadata;
    private readonly EvidenceStoreMonitor _monitor;

    public EvidenceDestroyer(IEvidenceStore store, IEvidenceMetadata metadata, EvidenceStoreMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(monitor);

        _store = store;
        _metadata = metadata;
        _monitor = monitor;
    }

    /// <param name="hash">The bytes to destroy.</param>
    /// <param name="actor">Who is destroying them. Recorded permanently.</param>
    /// <param name="reason">Why. Also recorded permanently.</param>
    /// <param name="ct">Cancels the destroy.</param>
    /// <param name="ignoreReports">Case files whose hold does not count, because this destroy is taking the file off them.</param>
    /// <param name="beforeDelete">Runs once nothing else holds the file and before the bytes are deleted.</param>
    public async Task<DestroyResult> DestroyAsync(
        EvidenceHash hash,
        string actor,
        string reason,
        CancellationToken ct = default,
        IReadOnlyCollection<string>? ignoreReports = null,
        Func<CancellationToken, Task>? beforeDelete = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var health = _monitor.Current;
        if (health.State is EvidenceStoreState.Unavailable or EvidenceStoreState.Unreachable)
        {
            // Deleting on the authority of a database whose relationship to the store is exactly
            // what is in doubt is how a recoverable misconfiguration becomes an unrecoverable one.
            throw new EvidenceStoreUnavailableException(
                "Evidence cannot be destroyed while the store's state is unresolved: " + health.Explanation);
        }

        var references = (await _metadata.ReferencesAsync(hash, ct).ConfigureAwait(false))
            .Where(r => ignoreReports is null || !ignoreReports.Contains(r, StringComparer.Ordinal))
            .ToList();

        if (references.Count > 0)
        {
            return new DestroyResult(
                false,
                references,
                references.Count == 1
                    ? $"This file is still on case file {references[0]}."
                    : $"This file is still on {references.Count} case files "
                      + $"({string.Join(", ", references)}).");
        }

        if (beforeDelete is not null)
            await beforeDelete(ct).ConfigureAwait(false);

        await _store.DeleteAsync(hash, ct).ConfigureAwait(false);
        await _metadata.MarkDestroyedAsync(hash, actor, reason, ct).ConfigureAwait(false);

        return new DestroyResult(
            true,
            [],
            "The bytes were destroyed.");
    }
}
