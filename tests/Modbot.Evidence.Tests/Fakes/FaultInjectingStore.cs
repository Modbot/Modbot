using Modbot.Evidence.Storage;

namespace Modbot.Evidence.Tests.Fakes;

/// <summary>
/// Wraps a real store so a test can make it stop answering, or answer wrongly, at a chosen moment.
/// </summary>
/// <remarks>
/// A decorator over a real backend rather than a hand-written substitute, so that the behaviour
/// under test is the real store's everywhere except the fault being injected.
/// </remarks>
public sealed class FaultInjectingStore : IEvidenceStore
{
    private readonly IEvidenceStore _inner;

    public FaultInjectingStore(IEvidenceStore inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>When set, every call that touches the store throws this instead.</summary>
    public Exception? Fault { get; set; }

    /// <summary>When set, <see cref="ProbeAsync"/> returns this instead of asking the store.</summary>
    public StoreProbe? ProbeOverride { get; set; }

    /// <summary>When true, a committed object reports as missing — a store that lost it.</summary>
    public bool PretendObjectsAreMissing { get; set; }

    public EvidenceStoreCapabilities Capabilities => _inner.Capabilities;

    public string Description => _inner.Description;

    public Task<StagedObject> StageAsync(
        EvidenceUploadId uploadId, Stream body, long maxBytes, long? declaredLength = null,
        CancellationToken ct = default)
        => Fault is null
            ? _inner.StageAsync(uploadId, body, maxBytes, declaredLength, ct)
            : Task.FromException<StagedObject>(Fault);

    public Task<CommitOutcome> CommitAsync(
        EvidenceUploadId uploadId, EvidenceHash hash, CancellationToken ct = default)
        => Fault is null
            ? _inner.CommitAsync(uploadId, hash, ct)
            : Task.FromException<CommitOutcome>(Fault);

    public Task<Stream?> OpenReadAsync(
        EvidenceHash hash, ByteRange? range = null, CancellationToken ct = default)
        => Fault is null ? _inner.OpenReadAsync(hash, range, ct) : Task.FromException<Stream?>(Fault);

    public Task<Stream?> OpenStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default)
        => Fault is null ? _inner.OpenStagedAsync(uploadId, ct) : Task.FromException<Stream?>(Fault);

    public Task<ObjectStat?> StatAsync(EvidenceHash hash, CancellationToken ct = default)
    {
        if (Fault is not null)
            return Task.FromException<ObjectStat?>(Fault);

        return PretendObjectsAreMissing ? Task.FromResult<ObjectStat?>(null) : _inner.StatAsync(hash, ct);
    }

    public Task DeleteAsync(EvidenceHash hash, CancellationToken ct = default)
        => Fault is null ? _inner.DeleteAsync(hash, ct) : Task.FromException(Fault);

    public Task DeleteStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default)
        => Fault is null ? _inner.DeleteStagedAsync(uploadId, ct) : Task.FromException(Fault);

    public Task<Uri?> TryCreatePresignedReadAsync(
        EvidenceHash hash, TimeSpan ttl, PresignedReadOptions? options = null, CancellationToken ct = default)
        => _inner.TryCreatePresignedReadAsync(hash, ttl, options, ct);

    public Task<Uri?> TryCreatePresignedWriteAsync(
        EvidenceUploadId uploadId, TimeSpan ttl, string? contentType = null, CancellationToken ct = default)
        => _inner.TryCreatePresignedWriteAsync(uploadId, ttl, contentType, ct);

    public Task WriteSentinelAsync(StoreSentinel sentinel, CancellationToken ct = default)
        => Fault is null ? _inner.WriteSentinelAsync(sentinel, ct) : Task.FromException(Fault);

    /// <summary>
    /// A store that is failing every other call does not answer a probe either — which is exactly
    /// the "did not answer" case, and must not be mistaken for a missing sentinel.
    /// </summary>
    public Task<StoreProbe> ProbeAsync(CancellationToken ct = default)
    {
        if (ProbeOverride is { } forced)
            return Task.FromResult(forced);

        return Fault is null
            ? _inner.ProbeAsync(ct)
            : Task.FromResult(StoreProbe.Unreachable(Description, Fault));
    }
}
