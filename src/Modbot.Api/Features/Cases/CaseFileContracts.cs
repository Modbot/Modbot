using System.Text.Json;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Evidence;

namespace Modbot.Api.Features.Cases;

// ── Ban reasons ─────────────────────────────────────────────────────────────────────────────

/// <param name="NeedsWrittenReason">Whether picking this one means the written reason cannot be empty.</param>
/// <param name="IsActive">Switched-off reasons stay on old case files and leave the buttons.</param>
/// <param name="UsedFor">
/// The actions that offer it: any of <c>ban</c>, <c>kick</c>, <c>unban</c> and <c>reject</c>
/// (turning a join request down). Always at least one.
/// </param>
public sealed record BanReasonView(
    Guid Id,
    string Label,
    string Description,
    int SortOrder,
    bool IsActive,
    bool NeedsWrittenReason,
    IReadOnlyList<string> UsedFor);

/// <param name="CanEdit">Whether the caller may change the list (<c>EditClassifications</c>).</param>
/// <param name="ReasonAlwaysRequired">
/// Whether a kick, an unban and turning a join request down each need a reason too. A ban always
/// does.
/// </param>
public sealed record BanReasonListResponse(IReadOnlyList<BanReasonView> Reasons, bool CanEdit, bool ReasonAlwaysRequired);

/// <param name="IsActive">Ignored on create; a new reason is always active.</param>
/// <param name="UsedFor">
/// Any of <c>ban</c>, <c>kick</c>, <c>unban</c> and <c>reject</c>; at least one when given. Left
/// out: <c>ban</c>, <c>kick</c> and <c>reject</c> on create, and unchanged on an update.
/// </param>
public sealed record BanReasonRequest(
    string Label,
    string? Description = null,
    bool NeedsWrittenReason = false,
    bool? IsActive = null,
    IReadOnlyList<string>? UsedFor = null);

/// <param name="Required">True: a kick, an unban and turning a join request down each need a reason, as a ban does.</param>
public sealed record ReasonAlwaysRequiredRequest(bool Required);

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
    int EvidenceCount,
    DateTimeOffset? LiftedAt = null);

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
/// <param name="Explanation">When it was taken, and whether a newer profile exists, written by the server.</param>
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
public sealed record EvidenceDeliveryView(
    bool Configured,
    bool UploadsAllowed,
    string StoreExplanation,
    bool DirectDelivery,
    long MaxFileBytes,
    IReadOnlyList<string> AcceptedTypes);

/// <param name="Evidence">
/// Null when the caller may not view evidence; the list otherwise: each file on the case file, destroyed
/// ones included, and one line for each file that was taken off (<c>takenOffAt</c> set).
/// </param>
/// <param name="CanEdit">The author, or anyone holding <c>Ban</c>, while the case file is not withdrawn.</param>
/// <param name="CanAttach"><see cref="CanEdit"/> and <c>UploadEvidence</c>. It is also what lets a file be taken off.</param>
/// <param name="CanDestroyEvidence"><c>ViewEvidence</c> and <c>DestroyEvidence</c>, on any case file, withdrawn ones too.</param>
/// <param name="Lifted">Set once the ban was lifted from Modbot: when, by whom, and why.</param>
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
    DateTimeOffset Now,
    bool CanDestroyEvidence = false,
    CaseLiftView? Lifted = null,
    IReadOnlyList<SavedClipView>? Clips = null);

/// <summary>The unban that lifted a case file's ban.</summary>
/// <param name="At">When, on Modbot's clock.</param>
/// <param name="ByUsername">The moderator who pressed Unban, as their username was then.</param>
/// <param name="UnbanFactId">The <c>modbot.action.unban</c> fact, for the link to its audit log row.</param>
/// <param name="Reasons">Why it was lifted: the unban's reasons, with their current labels.</param>
/// <param name="Note">The note written on the unban, or null.</param>
public sealed record CaseLiftView(
    DateTimeOffset At,
    string? ByUsername,
    long? UnbanFactId,
    IReadOnlyList<CaseFileReason> Reasons,
    string? Note);

/// <summary>
/// A clip a moderator's companion said it saved while the person on this case file was in the same
/// instance, and that is not on this case file yet. Only the fingerprint ever reached the server;
/// the file is on that moderator's PC.
/// </summary>
/// <param name="Id">Send it back as <c>clipId</c> when attaching the file, so the server checks it is this clip.</param>
/// <param name="SavedAt">When they pressed Save. The clip is the few minutes before.</param>
/// <param name="WorldId">The world. Opaque.</param>
/// <param name="InstanceId">The instance, as VRChat named it. Display only.</param>
/// <param name="SavedById">The moderator's VRChat id.</param>
/// <param name="SavedBy">
/// The username of the Modbot account the reporting device was paired to: whose PC saved it. Taken
/// from the pairing, never from the report.
/// </param>
/// <param name="ByteSize">How big the file is, so the right one can be picked out of a folder.</param>
/// <param name="WorldName">The world's name, when Modbot has read the world's page.</param>
public sealed record SavedClipView(
    long Id,
    DateTimeOffset SavedAt,
    string WorldId,
    string InstanceId,
    string SavedById,
    string SavedBy,
    long ByteSize,
    string? WorldName);

/// <param name="RefreshOutcome">What asking VRChat for a fresher profile came back with: Queued, Promoted, AlreadyQueued, FreshEnough or NotAvailable.</param>
public sealed record CaseFileCreatedResponse(
    CaseFileView Case,
    string RefreshOutcome);

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
