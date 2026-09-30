namespace Modbot.Api.Features.Evidence;

/// <param name="Bucket">Railway calls this <c>BUCKET</c>.</param>
/// <param name="Endpoint">With scheme. Railway calls this <c>ENDPOINT</c>.</param>
/// <param name="AccessKeyId">Railway calls this <c>ACCESS_KEY_ID</c>.</param>
/// <param name="Region">SigV4 signing region.</param>
/// <param name="Prefix">Optional key prefix, so one bucket can hold two deployments.</param>
/// <param name="UsePathStyle">
/// Path-style URLs rather than virtual-hosted-style. Asked rather than guessed: Railway issues
/// virtual-hosted-style on new buckets and path-style on older ones, and only its credentials tab
/// says which.
/// </param>
public sealed record EvidenceS3View(
    string? Bucket,
    string? Endpoint,
    string? AccessKeyId,
    string? Region,
    string? Prefix,
    bool UsePathStyle);

/// <param name="Backend">"None", "S3", "Filesystem" or "Database".</param>
/// <param name="StoreId">The store marker written into the store when it was set up.</param>
/// <param name="Root">Filesystem backend only.</param>
/// <param name="SecretStored">
/// Whether an S3 secret is on file. <strong>The secret itself is never sent back</strong> — it is
/// stored encrypted and the settings page has no reason to see it again. An empty secret on save
/// means "keep the one you have", the same convention the Discord and SMTP secrets already use.
/// </param>
public sealed record EvidenceBackendView(
    string Backend,
    Guid? StoreId,
    string? Root,
    EvidenceS3View S3,
    bool SecretStored);

/// <param name="MaxFileBytes">Per file. Enforced three times (design §9.3).</param>
/// <param name="MaxReportBytes">Per report. Zero means no limit.</param>
/// <param name="MaxDeploymentBytes">Across the deployment. Zero means no limit.</param>
public sealed record EvidenceLimitsView(
    long MaxFileBytes,
    long MaxReportBytes,
    long MaxDeploymentBytes);

/// <summary>What the selected store can actually do, declared rather than discovered (§13.2).</summary>
/// <param name="DirectDeliveryAvailable">
/// Whether the store hands the browser a link. There is no switch for it: a store that can, does.
/// </param>
public sealed record EvidenceCapabilitiesView(
    bool PresignedRead,
    bool PresignedWrite,
    bool RangeRead,
    bool ServerSideCopy,
    bool DirectDeliveryAvailable);

/// <param name="State">"NotConfigured", "Healthy", "Unavailable" or "Unreachable".</param>
/// <param name="Explanation">One sentence, written by the server. The SPA never composes it.</param>
/// <param name="Locked">
/// Whether this is the §8.4 lock: proof — an absent, foreign or malformed store marker — that the
/// store is not the one Modbot's records describe. It does not clear itself; uploads stay refused
/// until an administrator has checked the store and saved it again.
/// </param>
public sealed record EvidenceHealthView(
    string State,
    string Explanation,
    Guid? ExpectedStoreId,
    Guid? FoundStoreId,
    DateTimeOffset? Since,
    int ConsecutiveFailures,
    bool UploadsAllowed,
    bool Locked,
    string StoreDescription);

/// <param name="Finding">"Durable", "Unproven" or "Unwritable".</param>
/// <param name="Message">The exact sentence to show. Stored verbatim when acknowledged.</param>
/// <param name="IsSuspicion">
/// Whether this rests on platform inference rather than on evidence. A suspicion never blocks; it
/// asks. Proof always blocks, and an unwritable directory is proof.
/// </param>
/// <param name="AcknowledgedBy">Who pressed "use anyway", if anyone.</param>
/// <param name="WarningShown">
/// What they were shown when they did, kept word for word. A reworded warning must not
/// retroactively change what somebody agreed to.
/// </param>
public sealed record EvidenceDurabilityView(
    string Finding,
    string Message,
    bool RequiresAcknowledgement,
    bool Acknowledged,
    bool CanProceed,
    bool IsSuspicion,
    string? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAt,
    string? WarningShown);

/// <param name="Count">Blobs the database believes exist.</param>
/// <param name="Bytes">Their total size. Measured from the blob record, never by walking a store.</param>
/// <param name="DestroyedCount">Blobs whose bytes were deliberately erased. The records remain.</param>
public sealed record EvidenceStoredView(long Count, long Bytes, long DestroyedCount);

/// <param name="Id">The wire value to send back.</param>
public sealed record EvidenceBackendOption(string Id, string Label, bool Recommended);

/// <summary>
/// Values found in the environment, offered as a pre-fill and nothing more (design §16.1).
/// </summary>
/// <remarks>
/// Offered at first configuration only. After a backend is saved the environment is never read
/// again: a later edit to a Railway variable does not repoint the store, because an implementation
/// that re-read it on every boot would let a variable edit silently move the evidence — which is
/// precisely the trap §8 exists to catch, introduced by the convenience meant to smooth setup.
/// </remarks>
public sealed record EvidenceEnvironmentHint(
    string? Bucket, string? Endpoint, string? Region, string? AccessKeyId, bool SecretAvailable);

/// <param name="Durability">Null unless the filesystem backend is selected.</param>
/// <param name="SwitchBlockedReason">
/// Why the backend cannot be changed, when it cannot. Changing backends does not move objects, and
/// until a migration job exists switching with objects present is refused rather than silently
/// stranding them (design §16).
/// </param>
public sealed record EvidenceSettingsResponse(
    EvidenceBackendView Backend,
    EvidenceLimitsView Limits,
    EvidenceCapabilitiesView Capabilities,
    EvidenceHealthView Health,
    EvidenceDurabilityView? Durability,
    EvidenceStoredView Stored,
    IReadOnlyList<string> AcceptedTypes,
    IReadOnlyList<EvidenceBackendOption> Backends,
    EvidenceEnvironmentHint? EnvironmentHint,
    string? SwitchBlockedReason);

/// <param name="Backend">"None", "S3", "Filesystem" or "Database".</param>
/// <param name="SecretAccessKey">
/// Omitted or empty means "keep the stored one", so re-saving after fixing a typo in the endpoint
/// does not silently clear the credential.
/// </param>
/// <param name="AcknowledgeWarning">
/// The durability warning the operator was shown, echoed back verbatim as their "use anyway".
/// Echoed rather than sent as a boolean so that what is recorded is what was on their screen.
/// </param>
public sealed record EvidenceBackendRequest(
    string Backend,
    string? Root = null,
    string? Bucket = null,
    string? Endpoint = null,
    string? AccessKeyId = null,
    string? SecretAccessKey = null,
    string? Region = null,
    string? Prefix = null,
    bool UsePathStyle = false,
    string? AcknowledgeWarning = null);

public sealed record EvidenceLimitsRequest(
    long MaxFileBytes,
    long MaxReportBytes,
    long MaxDeploymentBytes);

/// <param name="FailedStep">
/// Which step of the round trip failed — credentials, endpoint, URL style, permissions, or a read
/// that returned different bytes than were written. "Storage error" is not an answer anybody can
/// act on.
/// </param>
/// <param name="RequiresAcknowledgement">
/// Whether the only thing standing in the way is an unproven disk the operator has not yet said
/// "use anyway" to. Never a refusal on Modbot's part; a question.
/// </param>
public sealed record EvidenceSetupResponse(
    bool Succeeded,
    string? FailedStep,
    string Message,
    Guid? StoreId,
    bool RequiresAcknowledgement,
    EvidenceDurabilityView? Durability);

/// <param name="UploadId">Also the staging key.</param>
/// <param name="MaxBytes">The per-file cap in force for this upload.</param>
/// <param name="AcceptedTypes">So the client can filter before a byte moves.</param>
/// <param name="TransferUrl">
/// Where to send the bytes: a presigned PUT straight to the bucket where the store can do that,
/// otherwise Modbot's own transfer endpoint. A capability difference, never a failure.
/// </param>
/// <param name="Presigned">Whether <paramref name="TransferUrl"/> bypasses Modbot.</param>
public sealed record EvidenceUploadTicketView(
    string UploadId,
    long MaxBytes,
    IReadOnlyList<string> AcceptedTypes,
    string TransferUrl,
    bool Presigned);

public sealed record EvidenceBeginRequest(
    string? FileName = null,
    string? ContentType = null,
    long? Length = null,
    string? ReportId = null);

/// <param name="Hash">The content address, computed from the bytes actually stored.</param>
public sealed record EvidenceStagedView(string Hash, long ByteSize);

/// <param name="ExpectedHash">
/// What the client believes it uploaded, where it computed one. A mismatch fails the commit.
/// </param>
/// <param name="ReportId">
/// The case file to attach to, for an upload begun before the case file existed. An upload begun
/// for one case file cannot be committed to another.
/// </param>
public sealed record EvidenceCommitRequest(string? ExpectedHash = null, string? ReportId = null);

/// <param name="ContentType">Modbot's determination from the bytes. Never the client's claim.</param>
public sealed record EvidenceCommitResponse(
    string Hash, long ByteSize, string ContentType);

/// <summary>One evidence file, as one case file holds it (or held it).</summary>
/// <param name="UploaderId">
/// Who put it on this case file, as their username. On a file's own record (no case file), who
/// sent it.
/// </param>
/// <param name="ReportId">The case file it is on. Null on a file's own record, which says nothing about where it is.</param>
/// <param name="FileName">What it was put on under. Display only.</param>
/// <param name="Destroyed">Whether the bytes were deliberately erased. The record remains.</param>
/// <param name="AttachedAt">When it was put on this case file.</param>
/// <param name="TakenOffAt">When it was taken off this case file. Null while it is on.</param>
/// <param name="TakenOffBy">Who took it off, as their username.</param>
public sealed record EvidenceObjectView(
    string Hash,
    long ByteSize,
    string ContentType,
    string? FileName,
    string? UploaderId,
    string? ReportId,
    string Origin,
    DateTimeOffset FirstStoredAt,
    bool Destroyed,
    DateTimeOffset? DestroyedAt,
    string? DestroyedBy,
    string? DestroyedReason,
    DateTimeOffset? AttachedAt = null,
    DateTimeOffset? TakenOffAt = null,
    string? TakenOffBy = null);

/// <param name="Reason">Recorded permanently, alongside who did it and when.</param>
/// <param name="CaseId">
/// The case file it is being destroyed from. That case file lets go of it as part of the destroy;
/// any other case file still holding it stops the destroy.
/// </param>
public sealed record EvidenceDestroyRequest(string Reason, string? CaseId = null);

/// <param name="BlockedByReports">
/// Case files still holding these bytes, by id. Content addressing means two case files can share
/// one object, so destroying it would take evidence off a case file nobody was looking at.
/// </param>
/// <param name="Message">What to tell the person: on a refusal, which case files are in the way.</param>
public sealed record EvidenceDestroyResponse(
    bool Destroyed, IReadOnlyList<string> BlockedByReports, string Message);

/// <param name="CaseId">The case file the file is now off.</param>
/// <param name="Hash">The file.</param>
public sealed record EvidenceTakenOffResponse(string CaseId, string Hash);
