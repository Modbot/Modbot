using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Upload;

namespace Modbot.Api.Features.Evidence;

/// <summary>
/// The Postgres side of evidence storage: the blob record of evidence design §7.
/// </summary>
/// <remarks>
/// <para>
/// <c>Modbot.Evidence</c> deliberately owns no migrations, so it declares
/// <see cref="IEvidenceMetadata"/> and somebody else supplies the table. This is that.
/// </para>
/// <para>
/// The blob record is what makes three otherwise impossible things work: §8's detection of a lost
/// store (the database believes objects exist and the store cannot find them), refcounted deletion
/// (two case files can hold one object, so destroying it for one must not take the bytes another
/// still needs), and rendering a case file without touching the store at all — which on S3 means
/// listing evidence costs no request and no egress.
/// </para>
/// <para>
/// Which case files hold a file is <see cref="EvidenceAttachment"/>, one row per case file. This
/// class only reads those (to answer "who still holds it" and "how many bytes"); putting a file on
/// a case file and taking it off are <see cref="EvidenceAttachments"/>, which writes the fact that
/// says who did it in the same transaction.
/// </para>
/// </remarks>
public sealed class DatabaseEvidenceMetadata(ModbotContext db, IModbotClock clock) : IEvidenceMetadata
{
    /// <summary>
    /// Records a blob, after the bytes are confirmed present.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent by hash, because the store is content-addressed and the same bytes genuinely
    /// arrive twice: the same screenshot for two case files, or a commit retried after a timeout. A
    /// duplicate is the expected case, not an error.
    /// </para>
    /// <para>
    /// A re-record never overwrites <see cref="EvidenceBlob.FirstStoredAt"/> or the destruction
    /// columns. First-stored is a fact about the bytes rather than about who sent them this time,
    /// and silently resurrecting a destroyed row by re-uploading the same file would defeat the
    /// deletion it records.
    /// </para>
    /// </remarks>
    public async Task RecordAsync(EvidenceBlobRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var hash = record.Hash.ToString();

        if (await db.EvidenceBlobs.AnyAsync(b => b.Hash == hash, ct))
            return;

        db.EvidenceBlobs.Add(new EvidenceBlob
        {
            Hash = hash,
            ByteSize = record.ByteSize,
            ContentType = record.ContentType,
            Backend = (short)record.Backend,
            FirstStoredAt = record.FirstStoredAt,
            FileName = record.FileName,
            UploaderId = record.UploaderId,
            Origin = record.Origin is EvidenceOrigin.Captured
                ? EvidenceOriginKind.Captured
                : EvidenceOriginKind.Uploaded,
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Which case files hold these bytes right now.</summary>
    /// <remarks>
    /// A file that was taken off a case file is not held by it, and a destroyed file is held by
    /// nobody: its bytes are already gone, so it cannot be a reason to keep an object alive.
    /// Counting destroyed rows would make an object undeletable forever after the one case file
    /// holding it was erased.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ReferencesAsync(
        EvidenceHash hash,
        CancellationToken ct = default)
    {
        var key = hash.ToString();

        return await (
                from a in db.EvidenceAttachments.AsNoTracking()
                join b in db.EvidenceBlobs.AsNoTracking() on a.Hash equals b.Hash
                where a.Hash == key && a.TakenOffAt == null && b.DestroyedAt == null
                select a.CaseId)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>The bytes one case file holds, leaving out one file.</summary>
    public async Task<long> BytesOnReportAsync(
        string reportId,
        EvidenceHash? except = null,
        CancellationToken ct = default)
    {
        var skip = except?.ToString();

        var sizes = await (
                from a in db.EvidenceAttachments.AsNoTracking()
                join b in db.EvidenceBlobs.AsNoTracking() on a.Hash equals b.Hash
                where a.CaseId == reportId
                    && a.TakenOffAt == null
                    && b.DestroyedAt == null
                    && (skip == null || b.Hash != skip)
                select new { b.Hash, b.ByteSize })
            .Distinct()
            .ToListAsync(ct);

        return sizes.Sum(s => s.ByteSize);
    }

    /// <summary>The bytes the deployment holds, leaving out one file.</summary>
    public async Task<long> BytesStoredAsync(EvidenceHash? except = null, CancellationToken ct = default)
    {
        var skip = except?.ToString();

        return await db.EvidenceBlobs.AsNoTracking()
            .Where(b => b.DestroyedAt == null && (skip == null || b.Hash != skip))
            .SumAsync(b => b.ByteSize, ct);
    }

    /// <summary>Marks the blob destroyed, keeping everything except the bytes.</summary>
    /// <remarks>
    /// <para>
    /// The row survives so that "this case had a video and an administrator destroyed it on 4
    /// March" stays answerable forever. A case file that looks like it never had evidence is
    /// indistinguishable from one nobody ever documented, which is precisely the ambiguity a
    /// destruction record exists to prevent.
    /// </para>
    /// <para>
    /// The first destruction wins. Re-destroying does not overwrite who did it or when — that
    /// timestamp is the moment the bytes stopped existing, and a later call cannot make it a
    /// different moment.
    /// </para>
    /// </remarks>
    public async Task MarkDestroyedAsync(
        EvidenceHash hash,
        string actor,
        string reason,
        CancellationToken ct = default)
    {
        var key = hash.ToString();

        await db.EvidenceBlobs
            .Where(b => b.Hash == key && b.DestroyedAt == null)
            .ExecuteUpdateAsync(
                u => u.SetProperty(b => b.DestroyedAt, clock.UtcNow)
                      .SetProperty(b => b.DestroyedBy, actor)
                      .SetProperty(b => b.DestroyedReason, reason),
                ct);
    }
}
