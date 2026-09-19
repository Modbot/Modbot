namespace Modbot.Core.Data.Entities;

/// <summary>Where an import is in its run. Persisted as smallint. Never renumber a member.</summary>
public enum ImportStatus : short
{
    Queued = 1,
    Running = 2,
    Done = 3,
    Failed = 4,
}

/// <summary>
/// One upload of old data: what was sent, who sent it, and what the job made of it.
/// </summary>
/// <remarks>
/// <para>
/// Import design §7. The upload's bytes are kept in <see cref="Body"/> until the job has read
/// them, then cleared. That is what lets a queued import survive a restart without a file store:
/// the row is the queue.
/// </para>
/// <para>
/// The counts are written as the job goes, so a page asking for progress sees it move.
/// </para>
/// </remarks>
public class Import
{
    public const int MaxSourceLength = 64;
    public const int MaxFileNameLength = 256;

    /// <summary>How many rejection reasons are kept. The count is kept in full.</summary>
    public const int MaxRejectionsKept = 50;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The label every record is filed under for idempotency (import design §6).</summary>
    public string Source { get; set; } = string.Empty;

    public string? FileName { get; set; }

    /// <summary>True for a run that validates and counts without writing (import design §4.3).</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Whether this import skipped records Modbot already had a fact for from somewhere else
    /// (import design §6.1). True unless the upload asked for it off.
    /// </summary>
    /// <remarks>
    /// Not the same thing as the re-upload check, which is the <see cref="ImportRecord"/> key
    /// (§6) and runs whatever this says. Off means "write the record even though Modbot has the
    /// event from another source", never "import this file twice".
    /// </remarks>
    public bool Dedup { get; set; } = true;

    /// <summary>
    /// The source every record in this upload is filed under unless the record names its own
    /// (import design §5). Never <see cref="FactSource.Import"/>.
    /// </summary>
    public FactSource SeenBy { get; set; } = FactSource.Manual;

    public ImportStatus Status { get; set; } = ImportStatus.Queued;

    /// <summary>Records read from the file so far, well-formed or not.</summary>
    public int Received { get; set; }

    /// <summary>Facts written; for a dry run, facts that would have been.</summary>
    public int Imported { get; set; }

    /// <summary>Records already imported before, left alone.</summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Records Modbot already had a fact for from somewhere else, left alone (import design §6.1).
    /// </summary>
    public int AlreadyKnown { get; set; }

    /// <summary>Records refused as malformed.</summary>
    public int Rejected { get; set; }

    /// <summary>The first <see cref="MaxRejectionsKept"/> rejections, as <c>[{ line, reason }]</c> JSON.</summary>
    public string Rejections { get; set; } = "[]";

    /// <summary>Why it failed, when it did.</summary>
    public string? Error { get; set; }

    public Guid StartedByUserId { get; set; }

    /// <summary>The uploader's username at the time, so the list needs no join.</summary>
    public string StartedByName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>The upload, until the job has parsed it. Null afterwards.</summary>
    public byte[]? Body { get; set; }
}

/// <summary>
/// One record that has been imported, keyed the way import design §6 says, so the same record
/// is never written twice.
/// </summary>
/// <remarks>
/// A side table rather than a unique index on the fact log because the log is partitioned by
/// <c>occurred_at</c>, and PostgreSQL requires the partition key in every unique constraint on
/// it. The subject columns are here so purge-user can erase a person's rows from this table too.
/// </remarks>
public class ImportRecord
{
    public const int MaxKeyLength = 256;

    public string Source { get; set; } = string.Empty;

    /// <summary><c>id:</c> plus the record's external id, or <c>hash:</c> plus its content hash.</summary>
    public string Key { get; set; } = string.Empty;

    public long FactId { get; set; }

    public Guid ImportId { get; set; }

    public FactPlatform SubjectPlatform { get; set; }

    public string SubjectId { get; set; } = string.Empty;

    public DateTimeOffset ImportedAt { get; set; }
}
