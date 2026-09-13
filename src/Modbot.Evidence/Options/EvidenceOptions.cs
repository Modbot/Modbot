namespace Modbot.Evidence.Options;

/// <summary>
/// Everything the evidence subsystem needs to be told, as plain records.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <strong>not</strong> read from <c>Settings</c> here. Foundation section 2.6 keeps
/// configuration in the database and the settings row is owned elsewhere; this project takes the
/// values already resolved so that nothing in it depends on a migration, and so a test can
/// construct a store without a database at all.
/// </para>
/// <para>
/// Mutable properties on purpose: these are bound by an <c>Action&lt;EvidenceOptions&gt;</c> at
/// registration, in the usual options-pattern shape.
/// </para>
/// </remarks>
public sealed record EvidenceOptions
{
    /// <summary>Which store holds the bytes. <see cref="EvidenceBackend.None"/> refuses uploads.</summary>
    public EvidenceBackend Backend { get; set; } = EvidenceBackend.None;

    /// <summary>
    /// The sentinel id written into the store when it was commissioned (design section 8.2).
    /// </summary>
    /// <remarks>
    /// This is the memory <em>outside</em> the store that lets absence be a finding rather than an
    /// ambiguity, which is the whole difference between this and
    /// <see cref="Core.Configuration.PersistenceProbe"/>. Null means the store has never been
    /// commissioned, and a probe cannot conclude anything until it has been.
    /// </remarks>
    public Guid? StoreId { get; set; }

    /// <summary>Per-file cap, enforced three times (design section 9.3). Default 100 MB.</summary>
    public long MaxFileBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>Total bytes one report may carry. Zero means no limit.</summary>
    public long MaxReportBytes { get; set; }

    /// <summary>Total bytes the deployment may hold. Zero means no limit.</summary>
    public long MaxDeploymentBytes { get; set; }

    /// <summary>
    /// Whether a capable store may hand the browser a presigned URL, or every byte is streamed
    /// through Modbot (design section 10.3).
    /// </summary>
    /// <remarks>
    /// Off means stronger response headers and a verified read, paid for in egress and latency.
    /// It has no effect on a backend without the capability; those stream regardless.
    /// </remarks>
    public bool DirectDeliveryEnabled { get; set; } = true;

    /// <summary>How long a presigned URL lives. Short, because it cannot be made single-use.</summary>
    public TimeSpan PresignedUrlTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an uncommitted staging object is left alone before the sweep takes it
    /// (design section 9.5). Long enough that a slow upload is never swept out from under itself.
    /// </summary>
    public TimeSpan StagingGrace { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long an unreferenced final object is left alone. Much longer than
    /// <see cref="StagingGrace"/>, because the harmless crash window of design section 7.2 lands here.
    /// </summary>
    public TimeSpan OrphanGrace { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How many consecutive "the store did not answer" probes before the operator is told
    /// (design section 8.3).
    /// </summary>
    /// <remarks>
    /// A thirty-second bucket blip must not raise the banner that means "your evidence is gone".
    /// The banner has exactly one job and a false alarm teaches operators to dismiss it.
    /// </remarks>
    public int TransientFailuresBeforeAlarm { get; set; } = 3;

    public FilesystemEvidenceOptions Filesystem { get; set; } = new();

    public S3EvidenceOptions S3 { get; set; } = new();

    public DatabaseEvidenceOptions Database { get; set; } = new();
}
