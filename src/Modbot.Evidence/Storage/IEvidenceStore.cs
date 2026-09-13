namespace Modbot.Evidence.Storage;

/// <summary>A byte range, half-open at the end in the HTTP sense (first and last inclusive).</summary>
/// <remarks>
/// Range reads exist for one concrete reason: <c>&lt;video&gt;</c> seeking is HTTP range requests,
/// and a backend that cannot answer one produces a twenty-minute clip that only plays from the
/// start — which a moderator discovers at the worst possible moment (design section 10.5).
/// </remarks>
public readonly record struct ByteRange(long First, long? Last)
{
    public long? Length => Last is null ? null : Last.Value - First + 1;
}

/// <summary>What was learned while the bytes were being written (design section 13.1).</summary>
/// <param name="Hash">Computed while streaming, never by buffering and hashing afterwards.</param>
/// <param name="ByteSize">The count actually written, which is the only size anyone should trust.</param>
public sealed record StagedObject(EvidenceHash Hash, long ByteSize);

/// <summary>What the store knows about a committed object without opening it.</summary>
public sealed record ObjectStat(long ByteSize);

/// <summary>Whether a commit created the object or found it already there.</summary>
/// <remarks>
/// <para>
/// Content addressing makes the second case a no-op rather than an overwrite, which is design
/// section 6's append-only rule falling out almost tautologically — identical keys mean identical
/// bytes. Stating it as a result anyway is what keeps a future "re-upload to fix the rotation"
/// feature from being built on top of a silent replace.
/// </para>
/// <para>
/// <strong>This is for the refcount, not for the uploader.</strong> Design section 5.3: telling a
/// moderator "you have already uploaded this file" tells them something about a case file they may
/// have no right to see. The upload completes normally either way.
/// </para>
/// </remarks>
public enum CommitOutcome
{
    /// <summary>The bytes were promoted to their content-addressed key.</summary>
    Created,

    /// <summary>An object already lived at that key. Nothing was overwritten.</summary>
    AlreadyPresent,
}

/// <summary>
/// Optional response-header overrides for a presigned read (design section 10.3).
/// </summary>
/// <remarks>
/// A presigned GET can pin the disposition and the content type by signing them. It cannot add
/// <c>X-Content-Type-Options</c> or a CSP, because those are not response-override parameters in
/// the S3 API — so direct delivery is honestly a little weaker than proxied delivery, and the
/// allowlist is doing the work there rather than the headers.
/// </remarks>
public sealed record PresignedReadOptions(string? ContentType = null, string? FileName = null);

/// <summary>
/// A content-addressed, append-only object store. Three implementations, one contract
/// (design section 13).
/// </summary>
public interface IEvidenceStore
{
    /// <summary>Declared, never discovered by exception (design section 13.2).</summary>
    EvidenceStoreCapabilities Capabilities { get; }

    /// <summary>A name for this store, for messages an operator has to act on.</summary>
    string Description { get; }

    /// <summary>
    /// Writes to the staging key, hashing and counting as the bytes go past.
    /// </summary>
    /// <param name="uploadId">Determines the staging key, so a retry overwrites.</param>
    /// <param name="body">Read once, forwards only. Never buffered in full.</param>
    /// <param name="maxBytes">
    /// Aborts the moment it is exceeded. <c>Content-Length</c> is a claim and a chunked body does
    /// not make one at all, so the cap is enforced against bytes actually seen
    /// (design section 9.3).
    /// </param>
    /// <param name="declaredLength">
    /// The length, where the transport stated one. Some stores need it up front; passing a wrong
    /// one cannot defeat <paramref name="maxBytes"/>.
    /// </param>
    Task<StagedObject> StageAsync(
        EvidenceUploadId uploadId,
        Stream body,
        long maxBytes,
        long? declaredLength = null,
        CancellationToken ct = default);

    /// <summary>
    /// Promotes the staged object to <c>sha256/&lt;hash&gt;</c>, server-side where the store can.
    /// </summary>
    /// <remarks>
    /// Never overwrites. A commit onto an existing key returns
    /// <see cref="CommitOutcome.AlreadyPresent"/> and leaves the bytes alone.
    /// </remarks>
    Task<CommitOutcome> CommitAsync(EvidenceUploadId uploadId, EvidenceHash hash, CancellationToken ct = default);

    /// <summary>Opens a committed object. Null when there is no such object.</summary>
    Task<Stream?> OpenReadAsync(EvidenceHash hash, ByteRange? range = null, CancellationToken ct = default);

    /// <summary>Opens a staged object, for the read-back at commit. Null when absent.</summary>
    Task<Stream?> OpenStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default);

    /// <summary>Null when there is no such object.</summary>
    Task<ObjectStat?> StatAsync(EvidenceHash hash, CancellationToken ct = default);

    /// <summary>
    /// Removes the bytes. <strong>Final</strong> — Railway Buckets has no versioning, R2 and
    /// Wasabi have it only if enabled, and an unlink is an unlink. There is no trash can here
    /// because there is no backend that could implement one.
    /// </summary>
    Task DeleteAsync(EvidenceHash hash, CancellationToken ct = default);

    /// <summary>Removes a staging object. Tolerates one that is already gone.</summary>
    Task DeleteStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default);

    /// <summary>Non-null if and only if <see cref="EvidenceStoreCapabilities.PresignedRead"/>.</summary>
    Task<Uri?> TryCreatePresignedReadAsync(
        EvidenceHash hash,
        TimeSpan ttl,
        PresignedReadOptions? options = null,
        CancellationToken ct = default);

    /// <summary>Non-null if and only if <see cref="EvidenceStoreCapabilities.PresignedWrite"/>.</summary>
    Task<Uri?> TryCreatePresignedWriteAsync(
        EvidenceUploadId uploadId,
        TimeSpan ttl,
        string? contentType = null,
        CancellationToken ct = default);

    /// <summary>Writes the sentinel. Done once, when the backend is commissioned.</summary>
    Task WriteSentinelAsync(StoreSentinel sentinel, CancellationToken ct = default);

    /// <summary>
    /// Reads the sentinel (design section 8.3). Never throws for "absent"; absent is a result.
    /// </summary>
    Task<StoreProbe> ProbeAsync(CancellationToken ct = default);
}
