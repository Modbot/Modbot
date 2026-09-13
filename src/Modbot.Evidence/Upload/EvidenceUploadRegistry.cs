using System.Collections.Concurrent;
using Modbot.Evidence.Storage;

namespace Modbot.Evidence.Upload;

/// <summary>Where an upload has got to.</summary>
public enum EvidenceUploadStatus
{
    /// <summary>Phase 1 done. A target has been handed out; no bytes yet.</summary>
    Begun,

    /// <summary>Phase 2 done. Bytes are in the store under the staging key, attached to nothing.</summary>
    Staged,

    /// <summary>Phase 3 done. The object is committed and the metadata is written.</summary>
    Committed,
}

/// <param name="Id">Also the staging key.</param>
/// <param name="FileName">What the uploader called it. Displayed, never part of a key.</param>
/// <param name="DeclaredContentType">The client's claim. Checked at commit against the bytes.</param>
/// <param name="DeclaredLength">The client's claim about size. Also checked against the bytes.</param>
/// <param name="ReportId">The case file this will hang off.</param>
/// <param name="UploaderId">Who is uploading.</param>
/// <param name="BegunAt">From <c>IModbotClock</c>. What the staging sweep ages against.</param>
public sealed record EvidenceUpload(
    EvidenceUploadId Id,
    string? FileName,
    string? DeclaredContentType,
    long? DeclaredLength,
    string? ReportId,
    string? UploaderId,
    DateTimeOffset BegunAt,
    EvidenceUploadStatus Status = EvidenceUploadStatus.Begun,
    EvidenceHash? StagedHash = null,
    long? StagedBytes = null,
    string? CommittedContentType = null);

/// <summary>The in-flight uploads: what was begun, what has been staged, what was committed.</summary>
public interface IEvidenceUploadRegistry
{
    Task SaveAsync(EvidenceUpload upload, CancellationToken ct = default);

    Task<EvidenceUpload?> FindAsync(EvidenceUploadId id, CancellationToken ct = default);

    Task RemoveAsync(EvidenceUploadId id, CancellationToken ct = default);

    /// <summary>Uploads begun before <paramref name="cutoff"/> and never committed.</summary>
    Task<IReadOnlyList<EvidenceUpload>> FindAbandonedAsync(
        DateTimeOffset cutoff, CancellationToken ct = default);
}

/// <summary>
/// Keeps in-flight uploads in memory.
/// </summary>
/// <remarks>
/// <para>
/// Losing this on a restart costs exactly what design section 9.4 already says a restart between
/// transfer and commit costs: an orphaned staging object, swept later, and a moderator who
/// re-uploads. Nothing half-attached can result, because nothing is attached to a report until
/// commit completes.
/// </para>
/// <para>
/// A durable table would make commit survive a restart, which is a real improvement and a real
/// migration. Until then this is the honest version rather than a queue pretending to hold
/// evidence — design section 9.4 rejects that for the same reason.
/// </para>
/// </remarks>
public sealed class InMemoryEvidenceUploadRegistry : IEvidenceUploadRegistry
{
    private readonly ConcurrentDictionary<string, EvidenceUpload> _uploads = new(StringComparer.Ordinal);

    public Task SaveAsync(EvidenceUpload upload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        _uploads[upload.Id.Value] = upload;
        return Task.CompletedTask;
    }

    public Task<EvidenceUpload?> FindAsync(EvidenceUploadId id, CancellationToken ct = default)
        => Task.FromResult(_uploads.GetValueOrDefault(id.Value));

    public Task RemoveAsync(EvidenceUploadId id, CancellationToken ct = default)
    {
        _uploads.TryRemove(id.Value, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EvidenceUpload>> FindAbandonedAsync(
        DateTimeOffset cutoff, CancellationToken ct = default)
    {
        IReadOnlyList<EvidenceUpload> abandoned =
        [
            .. _uploads.Values.Where(u => u.Status != EvidenceUploadStatus.Committed && u.BegunAt < cutoff),
        ];

        return Task.FromResult(abandoned);
    }
}
