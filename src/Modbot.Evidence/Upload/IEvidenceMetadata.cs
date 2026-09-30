using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;

namespace Modbot.Evidence.Upload;

/// <summary>Where a blob came from (design section 12.3).</summary>
public enum EvidenceOrigin
{
    /// <summary>A human chose a file and attached it.</summary>
    Uploaded,

    /// <summary>Modbot captured it — a profile or avatar image at action time.</summary>
    Captured,
}

/// <param name="Hash">The key, and the integrity claim.</param>
/// <param name="ByteSize">As stored, not as declared.</param>
/// <param name="ContentType">Modbot's determination from the bytes. Never the client's claim.</param>
/// <param name="Backend">Which store held it when it was committed.</param>
/// <param name="FirstStoredAt">From <c>IModbotClock</c>.</param>
/// <param name="FileName">The uploader's filename — metadata, displayed, never part of a key.</param>
/// <param name="UploaderId">Who sent it, as their username.</param>
/// <param name="Origin">Uploaded or captured.</param>
/// <remarks>
/// Says nothing about which case file holds the file. Putting a file on a case file is a separate
/// step, made after the bytes are recorded, because a file can be held by several case files and
/// taken off one of them.
/// </remarks>
public sealed record EvidenceBlobRecord(
    EvidenceHash Hash,
    long ByteSize,
    string ContentType,
    EvidenceBackend Backend,
    DateTimeOffset FirstStoredAt,
    string? FileName,
    string? UploaderId,
    EvidenceOrigin Origin);

/// <summary>
/// The Postgres side of evidence: the blob record and the attachment facts
/// (design section 7).
/// </summary>
/// <remarks>
/// <para>
/// An interface here because the blob record is a table and a table is a migration, and this
/// project deliberately owns no migrations. It is not incidental bookkeeping, though — it is what
/// makes the section 8 detection possible, what makes refcounted deletion possible, and what lets
/// an evidence list render without touching the store at all.
/// </para>
/// <para>
/// <strong>The ordering is the contract.</strong> <see cref="RecordAsync"/> is called only after
/// the store has confirmed the object is present, never before. A crash in that direction leaves
/// an object nobody references, which the sweep tidies. A crash in the other direction leaves a
/// report displaying evidence that does not exist — indistinguishable from the catastrophic loss
/// design section 8 is built to detect, which would poison the one signal that matters.
/// </para>
/// </remarks>
public interface IEvidenceMetadata
{
    /// <summary>
    /// Records a blob. Called after, and only after, the bytes are confirmed present in the store.
    /// </summary>
    Task RecordAsync(EvidenceBlobRecord record, CancellationToken ct = default);

    /// <summary>
    /// Which case files hold these bytes right now: the ones that have not taken the file off.
    /// </summary>
    /// <remarks>
    /// Deduplication means one object can be held by several case files, so destroying it for one
    /// must not take it from the others. Missing that is catastrophic in a quiet way: a moderator's
    /// evidence vanishes because somebody else tidied up an unrelated case file.
    /// </remarks>
    Task<IReadOnlyList<string>> ReferencesAsync(EvidenceHash hash, CancellationToken ct = default);

    /// <summary>
    /// The bytes this case file holds now, not counting <paramref name="except"/>. Destroyed files
    /// count for nothing, and the file being added is left out so that putting on one the case file
    /// already holds cannot push it over its limit.
    /// </summary>
    Task<long> BytesOnReportAsync(string reportId, EvidenceHash? except = null, CancellationToken ct = default);

    /// <summary>
    /// The bytes the deployment holds, not counting <paramref name="except"/>. Destroyed files
    /// count for nothing, and a file that is already stored adds nothing when it arrives again.
    /// </summary>
    Task<long> BytesStoredAsync(EvidenceHash? except = null, CancellationToken ct = default);

    /// <summary>
    /// Marks the blob destroyed, keeping everything except the bytes.
    /// </summary>
    /// <remarks>
    /// "This case had a video and an administrator deleted it on 4 March" must remain answerable
    /// forever. The alternative is a case file that looks like it never had evidence at all.
    /// </remarks>
    Task MarkDestroyedAsync(EvidenceHash hash, string actor, string reason, CancellationToken ct = default);
}
