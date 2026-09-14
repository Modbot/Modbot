using System.Text.Json;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Evidence;

namespace Modbot.Api.Features.Cases;

// ── Ban reasons ─────────────────────────────────────────────────────────────────────────────

/// <param name="NeedsWrittenReason">Whether picking this one means the written reason cannot be empty.</param>
/// <param name="IsActive">Switched-off reasons stay on old case files and leave the buttons.</param>
public sealed record BanReasonView(
    Guid Id,
    string Label,
    string Description,
    int SortOrder,
    bool IsActive,
    bool NeedsWrittenReason);

/// <param name="CanEdit">Whether the caller may change the list (<c>EditClassifications</c>).</param>
public sealed record BanReasonListResponse(IReadOnlyList<BanReasonView> Reasons, bool CanEdit);

/// <param name="IsActive">Ignored on create; a new reason is always active.</param>
public sealed record BanReasonRequest(
    string Label,
    string? Description = null,
    bool NeedsWrittenReason = false,
    bool? IsActive = null);

/// <param name="Ids">Every reason id, in the order the buttons should appear.</param>
public sealed record BanReasonOrderRequest(IReadOnlyList<Guid> Ids);

// ── Case files ──────────────────────────────────────────────────────────────────────────────

/// <summary>A reason as it was picked on a case file, with its current label.</summary>
public sealed record CaseFileReason(Guid Id, string Label, bool IsActive);

/// <param name="DisplayName">The person's display name as stored now, or null when Modbot has none.</param>
/// <param name="EvidenceCount">Files attached and not destroyed. Zero when the caller may not view evidence, too -- the count is not a peek.</param>
public sealed record CaseFileSummary(
    Guid Id,
    string UserId,
    string? DisplayName,
    DateTimeOffset? BannedAt,
    string? AuditEntryId,
    string AuthorUsername,
    IReadOnlyList<CaseFileReason> Reasons,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Withdrawn,
    int EvidenceCount);

public sealed record CaseFileListResponse(
    IReadOnlyList<CaseFileSummary> Cases,
    int Total,
    int Offset,
    DateTimeOffset Now);

/// <summary>
/// The person as they were when the case file was written.
/// </summary>
/// <param name="Profile">The <c>vrchat_user</c> row at the time, or null when Modbot had never fetched one.</param>
/// <param name="Membership">The <c>group_member</c> row at the time, or null when no sweep had listed them.</param>
/// <param name="BanListEntry">The <c>group_ban</c> row at the time, or null when the ban list did not hold them yet.</param>
/// <param name="TakenAt">When the snapshot was taken, on Modbot's clock.</param>
/// <param name="ProfileRefreshedAt">When VRChat had last been asked about the person, as of the snapshot. The profile is only as current as this.</param>
/// <param name="ProfileAgeSecondsAtCapture">How old the profile was when captured. Null when it had never been fetched.</param>
/// <param name="RecapturedAt">Set once the one permitted recapture has been used.</param>
/// <param name="ProfileRefreshedNow">When VRChat was last asked about the person, right now -- for telling whether a newer profile has arrived.</param>
/// <param name="CanCaptureAgain">Whether the caller may press "refresh and capture again": a newer profile exists, it has not been done before, and the caller may edit.</param>
/// <param name="Explanation">"This is how the profile looked on …", written by the server.</param>
public sealed record CaseSnapshotView(
    JsonElement? Profile,
    JsonElement? Membership,
    JsonElement? BanListEntry,
    DateTimeOffset TakenAt,
    DateTimeOffset? ProfileRefreshedAt,
    double? ProfileAgeSecondsAtCapture,
    DateTimeOffset? RecapturedAt,
    DateTimeOffset? ProfileRefreshedNow,
    bool CanCaptureAgain,
    string Explanation);

/// <summary>
/// What the evidence store can do right now, for the attach control on the case file page --
/// the same sentences the Settings evidence card shows, so the two cannot disagree.
/// </summary>
/// <param name="Configured">Whether any backend is configured at all.</param>
/// <param name="UploadsAllowed">False while the store is locked or unreachable.</param>
/// <param name="StoreExplanation">The store's own one-sentence state.</param>
/// <param name="DirectDelivery">Whether the store hands the browser the bytes itself.</param>
/// <param name="DeliveryExplanation">Which way bytes travel, and why.</param>
public sealed record EvidenceDeliveryView(
    bool Configured,
    bool UploadsAllowed,
    string StoreExplanation,
    bool DirectDelivery,
    string DeliveryExplanation,
    long MaxFileBytes,
    IReadOnlyList<string> AcceptedTypes);

/// <param name="Evidence">Null when the caller may not view evidence; the list otherwise, destroyed items included.</param>
/// <param name="CanEdit">The author, or anyone holding <c>Ban</c>, while the case file is not withdrawn.</param>
/// <param name="CanAttach"><see cref="CanEdit"/> and <c>UploadEvidence</c>.</param>
public sealed record CaseFileView(
    Guid Id,
    string UserId,
    string? DisplayName,
    string? GroupId,
    string? AuditEntryId,
    long? BanFactId,
    DateTimeOffset? BannedAt,
    Person? BannedBy,
    Guid AuthorUserId,
    string AuthorUsername,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? UpdatedByUsername,
    IReadOnlyList<CaseFileReason> Reasons,
    string WrittenReason,
    bool Withdrawn,
    DateTimeOffset? WithdrawnAt,
    string? WithdrawnByUsername,
    string? WithdrawnNote,
    CaseSnapshotView Snapshot,
    IReadOnlyList<EvidenceObjectView>? Evidence,
    EvidenceDeliveryView EvidenceDelivery,
    bool CanEdit,
    bool CanAttach,
    bool CanViewEvidence,
    DateTimeOffset Now);

/// <param name="RefreshOutcome">What asking VRChat for a fresher profile came back with: Queued, Promoted, AlreadyQueued, FreshEnough or NotAvailable.</param>
/// <param name="RefreshExplanation">The same, in a sentence.</param>
public sealed record CaseFileCreatedResponse(
    CaseFileView Case,
    string RefreshOutcome,
    string RefreshExplanation);

/// <param name="UserId">The person who was banned. Opaque; never validated.</param>
/// <param name="AuditEntryId">The audit-log entry the ban came from, when writing from the recorded list. Optional.</param>
/// <param name="ReasonIds">At least one.</param>
/// <param name="WrittenReason">Markdown. Required when any picked reason needs it.</param>
public sealed record CreateCaseFileRequest(
    string UserId,
    string? AuditEntryId,
    IReadOnlyList<Guid> ReasonIds,
    string? WrittenReason);

public sealed record UpdateCaseFileRequest(IReadOnlyList<Guid> ReasonIds, string? WrittenReason);

/// <param name="Note">Required. Why the case file is being withdrawn.</param>
public sealed record WithdrawCaseFileRequest(string Note);

/// <summary>One ban with no case file yet.</summary>
/// <param name="BannedBefore">Set when the ban's time is a window rather than an instant.</param>
/// <param name="LiftedAt">When the ban was lifted, if a later unban was recorded. A lifted ban still deserves its write-up; it is shown, not hidden.</param>
public sealed record UnwrittenBan(
    string UserId,
    string? DisplayName,
    DateTimeOffset BannedAt,
    DateTimeOffset? BannedBefore,
    string? AuditEntryId,
    long FactId,
    Person? BannedBy,
    DateTimeOffset? LiftedAt);

/// <param name="Days">The window asked for.</param>
/// <param name="Since">Where the window starts.</param>
/// <param name="Total">Every unwritten ban in the window, whatever the page holds.</param>
public sealed record UnwrittenBanListResponse(
    IReadOnlyList<UnwrittenBan> Bans,
    int Total,
    int Days,
    DateTimeOffset Since,
    DateTimeOffset Now);

/// <summary>Whether a person has a case file, for the badge beside their name on a ban list.</summary>
/// <param name="CaseId">The newest case file that stands, or null.</param>
/// <param name="Count">Every case file for the person, withdrawn ones included.</param>
public sealed record CaseFileLookup(string UserId, Guid? CaseId, int Count);
