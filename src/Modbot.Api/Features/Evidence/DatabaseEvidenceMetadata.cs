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
/// (two case files can cite one object, so deleting a report must not delete bytes another report
/// still needs), and rendering a case file without touching the store at all — which on S3 means
/// listing evidence costs no request and no egress.
/// </para>
/// </remarks>
public sealed class DatabaseEvidenceMetadata(ModbotContext db, IModbotClock clock) : IEvidenceMetadata
{
    /// <summary>
    /// Records a blob and its attachment, after the bytes are confirmed present.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent by hash, because the store is content-addressed and the same bytes genuinely
    /// arrive twice: the same screenshot attached to two reports, or a commit retried after a
    /// timeout. A duplicate is the expected case, not an error.
    /// </para>
    /// <para>
    /// A re-record never overwrites <see cref="EvidenceBlob.FirstStoredAt"/> or the destruction
    /// columns. First-stored is a fact about the bytes rather than about this attachment, and
    /// silently resurrecting a destroyed row by re-uploading the same file would defeat the
    /// deletion it records.
    /// </para>
    /// </remarks>
    public async Task RecordAsync(EvidenceBlobRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var hash = record.Hash.ToString();
        var existing = await db.EvidenceBlobs.FirstOrDefaultAsync(b => b.Hash == hash, ct);

        if (existing is not null)
        {
            // Attach it to this report if it was not attached to one before. Anything else about
            // the bytes is already true and is not this upload's to restate.
            if (existing.ReportId is null && record.ReportId is not null)
                existing.ReportId = record.ReportId;

            await db.SaveChangesAsync(ct);
            return;
        }

        db.EvidenceBlobs.Add(new EvidenceBlob
        {
            Hash = hash,
            ByteSize = record.ByteSize,
            ContentType = record.ContentType,
            Backend = (short)record.Backend,
            FirstStoredAt = record.FirstStoredAt,
            FileName = record.FileName,
            UploaderId = record.UploaderId,
            ReportId = record.ReportId,
            Origin = record.Origin is EvidenceOrigin.Captured
                ? EvidenceOriginKind.Captured
                : EvidenceOriginKind.Uploaded,
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Which reports currently cite these bytes.</summary>
    /// <remarks>
    /// Destroyed rows are excluded: their bytes are already gone, so they cannot be a reason to
    /// keep an object alive. Including them would make an object undeletable forever after the
    /// one report citing it was erased.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ReferencesAsync(
        EvidenceHash hash,
        CancellationToken ct = default)
    {
        var key = hash.ToString();

        return await db.EvidenceBlobs.AsNoTracking()
            .Where(b => b.Hash == key && b.ReportId != null && b.DestroyedAt == null)
            .Select(b => b.ReportId!)
            .Distinct()
            .ToListAsync(ct);
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
