using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Evidence;

/// <summary>Turns the stored rows into what the API sends.</summary>
internal static class EvidenceViews
{
    /// <summary>
    /// A file as one case file holds it: the file's own facts, with the name it was put on under,
    /// who put it on, and when it was taken off if it was.
    /// </summary>
    public static EvidenceObjectView Of(EvidenceBlob blob, EvidenceAttachment attachment) => new(
        blob.Hash,
        blob.ByteSize,
        blob.ContentType,
        attachment.FileName ?? blob.FileName,
        attachment.AttachedByName ?? blob.UploaderId,
        attachment.CaseId,
        blob.Origin.ToString(),
        blob.FirstStoredAt,
        blob.IsDestroyed,
        blob.DestroyedAt,
        blob.DestroyedBy,
        blob.DestroyedReason,
        attachment.AttachedAt,
        attachment.TakenOffAt,
        attachment.TakenOffByName,
        ClipOf(blob));

    /// <summary>A file on its own, with nothing said about where it is.</summary>
    public static EvidenceObjectView Of(EvidenceBlob blob) => new(
        blob.Hash,
        blob.ByteSize,
        blob.ContentType,
        blob.FileName,
        blob.UploaderId,
        null,
        blob.Origin.ToString(),
        blob.FirstStoredAt,
        blob.IsDestroyed,
        blob.DestroyedAt,
        blob.DestroyedBy,
        blob.DestroyedReason,
        Clip: ClipOf(blob));

    /// <summary>Where and when the file was saved as a clip, or null when it was not.</summary>
    private static EvidenceClipView? ClipOf(EvidenceBlob blob) => blob.ClipSavedAt is { } savedAt
        ? new EvidenceClipView(savedAt, blob.ClipWorldId, blob.ClipInstanceId, blob.ClipSavedById, blob.ClipSavedByName)
        : null;
}
