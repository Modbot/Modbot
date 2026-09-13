namespace Modbot.Core.Data.Entities;

/// <summary>Where a piece of evidence came from.</summary>
public enum EvidenceOriginKind : short
{
    /// <summary>A moderator chose a file in the web UI.</summary>
    Uploaded = 0,

    /// <summary>Modbot fetched it itself — a profile image at ban time, say.</summary>
    Captured = 1,
}

/// <summary>
/// One piece of evidence, as Postgres knows it. The bytes live in the store; this is everything
/// else.
/// </summary>
/// <remarks>
/// <para>
/// Evidence design §7. This record is not incidental bookkeeping — three things depend on it
/// and none of them work without it:
/// </para>
/// <list type="bullet">
///   <item>
///     <strong>Detecting a lost store.</strong> §8's whole scheme rests on being able to say "the
///     database believes objects exist and the store cannot find them". With no record there
///     is nothing to compare against, and an unmounted volume looks exactly like an empty one.
///   </item>
///   <item>
///     <strong>Refcounted deletion.</strong> Content addressing means two case files can share one
///     object, so deleting a report must not delete bytes another report still cites. Getting that
///     wrong is catastrophic in a quiet way: a moderator's evidence vanishes because somebody
///     tidied an unrelated report.
///   </item>
///   <item>
///     <strong>Rendering a case file without touching the store at all</strong> — which on S3
///     means listing evidence costs no request and no egress.
///   </item>
/// </list>
/// <para>
/// The row is keyed on <see cref="Hash"/> because the store is content-addressed: the same bytes
/// uploaded twice are one object and one row, cited by two reports.
/// </para>
/// </remarks>
public class EvidenceBlob
{
    /// <summary>SHA-256, lowercase hex. The object's name in the store.</summary>
    public string Hash { get; set; } = string.Empty;

    public long ByteSize { get; set; }

    /// <summary>
    /// As determined by Modbot from the bytes, never as claimed by the uploader.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Which backend held it when it was written.
    /// </summary>
    /// <remarks>
    /// Recorded because an operator can change backends. A row naming a backend the deployment no
    /// longer uses is how a half-finished migration becomes visible instead of becoming a
    /// mystery about missing files.
    /// </remarks>
    public short Backend { get; set; }

    public DateTimeOffset FirstStoredAt { get; set; }

    /// <summary>
    /// What the uploader called it. Display only, and hostile input — a person typed it, and it
    /// has no bearing on where the bytes live.
    /// </summary>
    public string? FileName { get; set; }

    public string? UploaderId { get; set; }

    /// <summary>Which case file cites it. Null for a blob nothing references yet.</summary>
    public string? ReportId { get; set; }

    public EvidenceOriginKind Origin { get; set; }

    /// <summary>
    /// When the bytes were destroyed, if they were. The row survives.
    /// </summary>
    /// <remarks>
    /// "This case had a video and an administrator deleted it on 4 March" has to stay answerable
    /// forever. The alternative is a case file that looks like it never had evidence at all —
    /// which is indistinguishable from a case nobody ever documented, and is exactly the ambiguity
    /// a purge receipt exists to prevent.
    /// </remarks>
    public DateTimeOffset? DestroyedAt { get; set; }

    /// <summary>Who destroyed it, and why. Kept alongside <see cref="DestroyedAt"/>.</summary>
    public string? DestroyedBy { get; set; }

    public string? DestroyedReason { get; set; }

    public bool IsDestroyed => DestroyedAt is not null;
}
