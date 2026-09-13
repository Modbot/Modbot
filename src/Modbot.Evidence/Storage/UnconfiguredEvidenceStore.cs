namespace Modbot.Evidence.Storage;

/// <summary>
/// The store you get before an operator has chosen one.
/// </summary>
/// <remarks>
/// A real object rather than a null, so that every caller sees the same shape and the only thing
/// that changes is the answer. It declares no capabilities, so the capability branches take their
/// "stream it through Modbot" path rather than crashing, and it refuses writes with a sentence
/// somebody can act on instead of a <c>NullReferenceException</c>.
/// </remarks>
internal sealed class UnconfiguredEvidenceStore : IEvidenceStore
{
    private const string Message =
        "No evidence store has been configured. Choose one under Settings → Data before attaching "
        + "evidence to a report.";

    public EvidenceStoreCapabilities Capabilities => EvidenceStoreCapabilities.None;

    public string Description => "no configured store";

    public Task<StagedObject> StageAsync(
        EvidenceUploadId uploadId, Stream body, long maxBytes, long? declaredLength = null,
        CancellationToken ct = default)
        => throw new EvidenceStoreUnavailableException(Message);

    public Task<CommitOutcome> CommitAsync(
        EvidenceUploadId uploadId, EvidenceHash hash, CancellationToken ct = default)
        => throw new EvidenceStoreUnavailableException(Message);

    public Task<Stream?> OpenReadAsync(
        EvidenceHash hash, ByteRange? range = null, CancellationToken ct = default)
        => Task.FromResult<Stream?>(null);

    public Task<Stream?> OpenStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default)
        => Task.FromResult<Stream?>(null);

    public Task<ObjectStat?> StatAsync(EvidenceHash hash, CancellationToken ct = default)
        => Task.FromResult<ObjectStat?>(null);

    public Task DeleteAsync(EvidenceHash hash, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<Uri?> TryCreatePresignedReadAsync(
        EvidenceHash hash, TimeSpan ttl, PresignedReadOptions? options = null, CancellationToken ct = default)
        => Task.FromResult<Uri?>(null);

    public Task<Uri?> TryCreatePresignedWriteAsync(
        EvidenceUploadId uploadId, TimeSpan ttl, string? contentType = null, CancellationToken ct = default)
        => Task.FromResult<Uri?>(null);

    public Task WriteStoreMarkerAsync(StoreMarker marker, CancellationToken ct = default)
        => throw new EvidenceStoreUnavailableException(Message);

    public Task<StoreProbe> ProbeAsync(CancellationToken ct = default)
        => Task.FromResult(StoreProbe.Absent(Description));
}
