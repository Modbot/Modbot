using Modbot.Core.Time;
using Modbot.Evidence.Content;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;

namespace Modbot.Evidence.Upload;

/// <summary>A file was refused. Carries why, in terms a moderator can act on.</summary>
public sealed class EvidenceRejectedException : Exception
{
    public EvidenceRejectedException(ContentRejection reason, string message) : base(message)
        => Reason = reason;

    public EvidenceRejectedException(string message) : base(message) { }

    public EvidenceRejectedException(string message, Exception innerException)
        : base(message, innerException) { }

    public EvidenceRejectedException() : base("The file was refused.") { }

    public ContentRejection Reason { get; } = ContentRejection.NotAllowed;
}

/// <param name="FileName">What the uploader called it.</param>
/// <param name="DeclaredContentType">The client's claim. A courtesy check only.</param>
/// <param name="DeclaredLength">The client's claim about size, where it makes one.</param>
public sealed record BeginUploadRequest(
    string? FileName = null,
    string? DeclaredContentType = null,
    long? DeclaredLength = null,
    string? ReportId = null,
    string? UploaderId = null);

/// <param name="UploadId">Also the staging key.</param>
/// <param name="MaxBytes">The per-file cap in force for this upload.</param>
/// <param name="AcceptedTypes">The allowlist, so the client can filter before a byte moves.</param>
/// <param name="PresignedTarget">
/// Where to PUT the bytes, when the store can be written to directly. Null means send them to
/// Modbot instead — which is a capability difference, not a failure.
/// </param>
public sealed record UploadTicket(
    EvidenceUploadId UploadId,
    long MaxBytes,
    IReadOnlyList<string> AcceptedTypes,
    Uri? PresignedTarget);

/// <param name="Hash">The content address, computed from the bytes actually stored.</param>
/// <param name="ByteSize">As stored.</param>
/// <param name="ContentType">Modbot's determination.</param>
/// <param name="Outcome">
/// Whether these bytes were new to the store. <strong>Not for the uploader</strong> — telling a
/// moderator "you have already uploaded this file" tells them something about a case file they may
/// have no right to see.
/// </param>
public sealed record CommitResult(
    EvidenceHash Hash, long ByteSize, string ContentType, CommitOutcome Outcome);

/// <summary>
/// The three-phase upload of design section 9.1, which is the same on every backend.
/// </summary>
/// <remarks>
/// <para>
/// <c>BEGIN</c> hands out a target, <c>TRANSFER</c> moves the bytes there once, and <c>COMMIT</c>
/// reads them back, hashes them, decides their type from their own bytes, promotes them to their
/// content-addressed key, and only then writes the metadata. <strong>Nothing is attached to a
/// report until commit completes</strong>, so a moderator who closes the tab mid-upload leaves a
/// staging object and nothing else — never half-evidence on a case file.
/// </para>
/// <para>
/// Reading the bytes back at commit is what makes the presigned path safe. Handing out a presigned
/// PUT means Modbot does not see the bytes, and so cannot hash them, cannot enforce the cap and
/// cannot check the type — a hole that "just presign uploads" does not close on its own. The
/// read-back closes it, and closes it before anything is attached.
/// </para>
/// <para>
/// Nothing here transcodes, re-encodes or strips anything. The hash is a claim about the bytes the
/// moderator attested to; the moment Modbot rewrites them, the claim is about Modbot's output
/// instead. EXIF is disclosed by the layer above, never removed.
/// </para>
/// </remarks>
public sealed class EvidenceUploadService
{
    private readonly IEvidenceStore _store;
    private readonly EvidenceStoreMonitor _monitor;
    private readonly IEvidenceUploadRegistry _registry;
    private readonly IEvidenceMetadata _metadata;
    private readonly EvidenceOptions _options;
    private readonly IModbotClock _clock;

    public EvidenceUploadService(
        IEvidenceStore store,
        EvidenceStoreMonitor monitor,
        IEvidenceUploadRegistry registry,
        IEvidenceMetadata metadata,
        EvidenceOptions options,
        IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _monitor = monitor;
        _registry = registry;
        _metadata = metadata;
        _options = options;
        _clock = clock;
    }

    /// <summary>Phase 1. Rejects what can be rejected before a byte moves.</summary>
    public async Task<UploadTicket> BeginAsync(BeginUploadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequireHealthyStore();

        // The cheapest possible refusal. A declared size over the cap is refused here; a lie about
        // it is caught while streaming, and the stored size is checked again at commit.
        if (request.DeclaredLength is { } declared && declared > _options.MaxFileBytes)
            throw new EvidenceTooLargeException(_options.MaxFileBytes);

        if (request.DeclaredContentType is not null
            && !EvidenceContentType.IsPlausibleDeclaredType(request.DeclaredContentType))
        {
            throw new EvidenceRejectedException(
                ContentRejection.NotAllowed,
                $"'{request.DeclaredContentType}' is not an accepted evidence type. Evidence may be a "
                + "PNG, JPEG, WebP, GIF, MP4 or WebM.");
        }

        var uploadId = EvidenceUploadId.New();

        await _registry.SaveAsync(
            new EvidenceUpload(
                uploadId,
                request.FileName,
                request.DeclaredContentType,
                request.DeclaredLength,
                request.ReportId,
                request.UploaderId,
                _clock.UtcNow),
            ct).ConfigureAwait(false);

        // Branch on the capability, never on a caught exception.
        var target = _store.Capabilities.HasFlag(EvidenceStoreCapabilities.PresignedWrite)
            ? await _store.TryCreatePresignedWriteAsync(
                uploadId, _options.PresignedUrlTtl, request.DeclaredContentType, ct).ConfigureAwait(false)
            : null;

        return new UploadTicket(uploadId, _options.MaxFileBytes, EvidenceContentType.Allowed, target);
    }

    /// <summary>
    /// Phase 2, for the proxied path. The presigned path skips this and goes straight to the store.
    /// </summary>
    public async Task<StagedObject> ReceiveAsync(
        EvidenceUploadId uploadId,
        Stream body,
        long? contentLength = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        RequireHealthyStore();

        var upload = await RequireUploadAsync(uploadId, ct).ConfigureAwait(false);

        // Refuse on a stated length before reading anything. Content-Length is a claim, so this is
        // an optimisation rather than the enforcement; the enforcement is the counting stream
        // inside the store, which aborts mid-transfer.
        if (contentLength is { } length && length > _options.MaxFileBytes)
            throw new EvidenceTooLargeException(_options.MaxFileBytes);

        StagedObject staged;

        try
        {
            staged = await _store
                .StageAsync(uploadId, body, _options.MaxFileBytes, contentLength, ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not (OperationCanceledException or EvidenceTooLargeException))
        {
            // The store failed mid-upload. Re-probe before the next attempt, exactly as design
            // section 8.3 requires — this is the "first upload after any store failure" case.
            await _monitor.CheckAsync(ct).ConfigureAwait(false);
            throw;
        }

        await _registry.SaveAsync(
            upload with
            {
                Status = EvidenceUploadStatus.Staged,
                StagedHash = staged.Hash,
                StagedBytes = staged.ByteSize,
            },
            ct).ConfigureAwait(false);

        return staged;
    }

    /// <summary>
    /// Phase 3. Reads the staged bytes back, decides what they are, promotes them, and only then
    /// writes the metadata.
    /// </summary>
    /// <param name="expectedHash">
    /// What the client believes it uploaded, where it computed one. A mismatch fails the commit —
    /// the bytes are not the bytes anybody thinks they are, and an object whose contents do not
    /// hash to its name is detectably wrong.
    /// </param>
    public async Task<CommitResult> CommitAsync(
        EvidenceUploadId uploadId,
        EvidenceHash? expectedHash = null,
        CancellationToken ct = default)
    {
        RequireHealthyStore();

        var upload = await RequireUploadAsync(uploadId, ct).ConfigureAwait(false);

        // Idempotent on the upload id: a commit retried after a network failure returns the same
        // answer instead of attaching the evidence twice.
        if (upload is { Status: EvidenceUploadStatus.Committed, StagedHash: { } done })
        {
            return new CommitResult(
                done, upload.StagedBytes ?? 0, upload.CommittedContentType!, CommitOutcome.AlreadyPresent);
        }

        var staged = await _store.OpenStagedAsync(uploadId, ct).ConfigureAwait(false)
            ?? throw new EvidenceStagingNotFoundException(
                "The uploaded bytes are no longer in the store. Nothing was attached; upload the file again.");

        EvidenceHash hash;
        long size;
        ContentTypeVerdict verdict;

        await using (staged)
        {
            (hash, size, verdict) = await InspectAsync(staged, ct).ConfigureAwait(false);
        }

        if (expectedHash is { } expected && expected != hash)
        {
            await DiscardAsync(uploadId, ct).ConfigureAwait(false);
            throw new EvidenceRejectedException(
                ContentRejection.NotAllowed,
                "The stored bytes do not hash to what was expected, so they are not the file that was "
                + "sent. Nothing was attached.");
        }

        if (!verdict.Accepted)
        {
            await DiscardAsync(uploadId, ct).ConfigureAwait(false);
            throw new EvidenceRejectedException(verdict.Rejection, verdict.Detail);
        }

        // The authoritative size check. On the presigned path the first two were enforced by
        // somebody else's server, which is to say not at all.
        if (size > _options.MaxFileBytes)
        {
            await DiscardAsync(uploadId, ct).ConfigureAwait(false);
            throw new EvidenceTooLargeException(_options.MaxFileBytes);
        }

        var outcome = await _store.CommitAsync(uploadId, hash, ct).ConfigureAwait(false);

        // A row in the blob projection is a claim that the bytes were confirmed present at least
        // once, and design section 8 relies on that claim being true. So it is confirmed, here,
        // rather than assumed from a copy that returned without throwing.
        var stat = await _store.StatAsync(hash, ct).ConfigureAwait(false);
        if (stat is null || stat.ByteSize != size)
        {
            throw new EvidenceStoreUnavailableException(
                $"{_store.Description} accepted the object and then did not report it present at its "
                + "final key. Nothing has been attached.");
        }

        // Best effort, and deliberately before the metadata write: a crash here leaves an orphan
        // staging object, which the sweep takes. The other ordering would leave a report pointing
        // at bytes that are not there.
        await TryDeleteStagingAsync(uploadId, ct).ConfigureAwait(false);

        await _metadata.RecordAsync(
            new EvidenceBlobRecord(
                hash,
                size,
                verdict.ContentType!,
                _options.Backend,
                _clock.UtcNow,
                upload.FileName,
                upload.UploaderId,
                upload.ReportId,
                EvidenceOrigin.Uploaded),
            ct).ConfigureAwait(false);

        await _registry.SaveAsync(
            upload with
            {
                Status = EvidenceUploadStatus.Committed,
                StagedHash = hash,
                StagedBytes = size,
                CommittedContentType = verdict.ContentType,
            },
            ct).ConfigureAwait(false);

        return new CommitResult(hash, size, verdict.ContentType!, outcome);
    }

    /// <summary>
    /// Hashes, counts and sniffs in one pass, without holding the object in memory.
    /// </summary>
    private async Task<(EvidenceHash Hash, long Size, ContentTypeVerdict Verdict)> InspectAsync(
        Stream staged, CancellationToken ct)
    {
        await using var counting = new CountingHashStream(staged, _options.MaxFileBytes);

        var prefix = new byte[EvidenceContentType.SignatureBytes];
        var prefixLength = await counting
            .ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, ct)
            .ConfigureAwait(false);

        // Drain the rest so the hash covers the whole object. Nothing is retained.
        await counting.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);

        return (counting.Hash, counting.BytesRead, EvidenceContentType.Sniff(prefix.AsSpan(0, prefixLength)));
    }

    private void RequireHealthyStore()
    {
        var health = _monitor.Current;
        if (health.UploadsAllowed)
            return;

        throw new EvidenceStoreUnavailableException(health.Explanation);
    }

    private async Task<EvidenceUpload> RequireUploadAsync(EvidenceUploadId uploadId, CancellationToken ct)
        => await _registry.FindAsync(uploadId, ct).ConfigureAwait(false)
           ?? throw new EvidenceStagingNotFoundException($"Upload {uploadId} is not in flight.");

    private async Task DiscardAsync(EvidenceUploadId uploadId, CancellationToken ct)
    {
        await TryDeleteStagingAsync(uploadId, ct).ConfigureAwait(false);
        await _registry.RemoveAsync(uploadId, ct).ConfigureAwait(false);
    }

    private async Task TryDeleteStagingAsync(EvidenceUploadId uploadId, CancellationToken ct)
    {
        try
        {
            await _store.DeleteStagedAsync(uploadId, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The sweep will take it. Failing the caller over litter would turn a rejected file
            // into a stuck upload.
        }
    }
}
