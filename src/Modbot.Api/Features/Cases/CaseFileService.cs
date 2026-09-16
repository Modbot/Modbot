using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Evidence;
using Modbot.Api.Features.Reviews;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Evidence.Content;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Users;

namespace Modbot.Api.Features.Cases;

/// <summary>Who is asking, and what they hold. Decides what a view shows and what a write may do.</summary>
/// <param name="UserId">The Modbot account.</param>
/// <param name="Username">Their username now, recorded on anything they write.</param>
public sealed record Caller(Guid UserId, string Username, ModbotPermissions Held)
{
    public bool Has(ModbotPermissions flag)
        => Held.HasFlag(ModbotPermissions.Administrator) || Held.HasFlag(flag);
}

/// <summary>Why a write was refused, with the status the endpoint should answer.</summary>
public sealed class CaseFileRefused(int status, string message, Guid? existingCaseId = null) : Exception(message)
{
    public int Status { get; } = status;

    /// <summary>For a 409: the case file that already covers this ban.</summary>
    public Guid? ExistingCaseId { get; } = existingCaseId;
}

/// <summary>
/// Everything the case file endpoints do: write one up, edit it, withdraw it, take the profile
/// snapshot again, and read them back.
/// </summary>
/// <remarks>
/// <para>
/// Built per request from what the host registered, the way <c>BanList</c> and the review
/// endpoints are. The fact writer, the partition maintainer and the profile sync's recorder are
/// optional: a host that maps the API without them still reads case files and says plainly that
/// it cannot write one, rather than failing to resolve a service mid-request.
/// </para>
/// <para>
/// <strong>The row and the fact commit together.</strong> A case file with no record of who wrote
/// it, or an edit with no record of what it changed, is the failure spec 5.8 exists to prevent --
/// so every write runs in one transaction with the fact that describes it.
/// </para>
/// </remarks>
public sealed class CaseFileService
{
    public const int MaxWrittenReasonLength = 20_000;
    public const int MaxWithdrawNoteLength = 2000;
    public const int DefaultMissingDays = 30;
    public const int MaxMissingDays = 3650;
    public const int MaxListLimit = 500;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter? _facts;
    private readonly EventPartitionMaintainer? _partitions;
    private readonly VRChatUserProfiles? _profiles;
    private readonly EvidenceOptions? _evidenceOptions;
    private readonly IEvidenceStore? _store;
    private readonly EvidenceStoreMonitor? _monitor;

    public CaseFileService(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter? facts = null,
        EventPartitionMaintainer? partitions = null,
        VRChatUserProfiles? profiles = null,
        EvidenceOptions? evidenceOptions = null,
        IEvidenceStore? store = null,
        EvidenceStoreMonitor? monitor = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _profiles = profiles;
        _evidenceOptions = evidenceOptions;
        _store = store;
        _monitor = monitor;
    }

    // ── Writing ────────────────────────────────────────────────────────────────────────────

    public Task<CaseFileCreatedResponse> CreateAsync(
        CreateCaseFileRequest request, Caller caller, CancellationToken ct)
        => CreateAsync(request, caller, known: null, ct);

    /// <summary>
    /// Writes up the ban Modbot has just performed, citing the fact that recorded it rather than
    /// hunting the audit log for a ban VRChat has not published yet (M4 §7).
    /// </summary>
    /// <remarks>
    /// The ordinary path finds the ban by looking for a <c>vrchat.group.member.ban</c> fact, which
    /// the audit-log sync writes minutes later. A ban issued through Modbot knows its own fact id
    /// at the moment it succeeds, so it hands it over instead of writing a case file that names no
    /// ban and hoping the two are matched up afterwards.
    /// </remarks>
    /// <param name="banFactId">The <c>modbot.action.ban</c> fact this case file is the write-up of.</param>
    /// <param name="bannedAt">When the ban was accepted, on Modbot's clock.</param>
    public Task<CaseFileCreatedResponse> CreateForActionAsync(
        CreateCaseFileRequest request, Caller caller, long banFactId, DateTimeOffset bannedAt, CancellationToken ct)
        => CreateAsync(request, caller, new BanReference(null, banFactId, bannedAt), ct);

    private async Task<CaseFileCreatedResponse> CreateAsync(
        CreateCaseFileRequest request, Caller caller, BanReference? known, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        if (string.IsNullOrWhiteSpace(request.UserId))
            throw new CaseFileRefused(400, "userId is required: who was banned.");

        RequireWriter();

        var userId = request.UserId.Trim();
        var (reasons, writtenReason) = await ValidateContentAsync(request.ReasonIds, request.WrittenReason, ct);

        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var groupId = settings?.ManagedGroupId;

        var ban = known ?? await FindBanAsync(userId, groupId, request.AuditEntryId?.Trim(), ct);
        await RefuseDuplicateAsync(userId, ban, ct);

        var now = _clock.UtcNow;
        var snapshot = await ProfileSnapshot.CaptureAsync(_db, userId, groupId, ct);

        var row = new CaseFile
        {
            UserId = userId,
            GroupId = groupId,
            AuditEntryId = ban.AuditEntryId,
            BanFactId = ban.FactId,
            BannedAt = ban.BannedAt,
            AuthorUserId = caller.UserId,
            AuthorUsername = caller.Username,
            ReasonIds = Json(reasons.Select(r => r.Id)),
            WrittenReason = writtenReason,
            CreatedAt = now,
            UpdatedAt = now,
            ProfileAtBan = snapshot.Profile,
            MembershipAtBan = snapshot.Membership,
            BanListEntryAtBan = snapshot.BanListEntry,
            SnapshotTakenAt = now,
            ProfileRefreshedAt = snapshot.ProfileRefreshedAt,
        };

        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            _db.CaseFiles.Add(row);
            await _db.SaveChangesAsync(ct);

            await RecordAsync(row, caller, FactType.ReportCreated, new JsonObject
            {
                ["reasonIds"] = new JsonArray(reasons.Select(r => (JsonNode?)r.Id.ToString()).ToArray()),
                ["reasonLabels"] = new JsonArray(reasons.Select(r => (JsonNode?)r.Label).ToArray()),
                ["writtenReason"] = writtenReason,
                ["auditEntryId"] = ban.AuditEntryId,
                ["banFactId"] = ban.FactId,
                ["bannedAt"] = Time(ban.BannedAt),
                ["snapshotTakenAt"] = Time(now),
                ["profileRefreshedAt"] = Time(snapshot.ProfileRefreshedAt),
                ["profileCaptured"] = snapshot.Profile is not null,
                ["description"] = $"Case file written by {caller.Username}: {string.Join(", ", reasons.Select(r => r.Label))}",
            }, ct);

            await transaction.CommitAsync(ct);
        }

        // Ask VRChat for a fresher profile, after the snapshot and outside the transaction. The
        // snapshot is what Modbot held at the moment of writing; if a newer profile lands, the
        // moderator can capture it again once. Never before the write, never blocking it
        // (evidence design §12.2: the write-up must not wait on a lookup).
        var outcome = await RequestRefreshAsync(userId, ct);

        return new CaseFileCreatedResponse(await ViewAsync(row.Id, caller, ct) ?? throw new InvalidOperationException(), outcome);
    }

    public async Task<CaseFileView> UpdateAsync(
        Guid id, UpdateCaseFileRequest request, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        RequireWriter();

        var row = await _db.CaseFiles.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new CaseFileRefused(404, "No such case file.");

        RequireEditable(row, caller);

        var (reasons, writtenReason) = await ValidateContentAsync(request.ReasonIds, request.WrittenReason, ct);
        var reasonIds = Json(reasons.Select(r => r.Id));

        if (reasonIds == row.ReasonIds && writtenReason == row.WrittenReason)
            return await ViewAsync(id, caller, ct) ?? throw new InvalidOperationException();

        var before = new JsonObject
        {
            ["reasonIds"] = JsonNode.Parse(row.ReasonIds),
            ["writtenReason"] = row.WrittenReason,
        };

        var now = _clock.UtcNow;
        row.ReasonIds = reasonIds;
        row.WrittenReason = writtenReason;
        row.UpdatedAt = now;
        row.UpdatedByUserId = caller.UserId;
        row.UpdatedByUsername = caller.Username;

        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            await _db.SaveChangesAsync(ct);

            await RecordAsync(row, caller, FactType.ReportUpdated, new JsonObject
            {
                ["before"] = before,
                ["after"] = new JsonObject
                {
                    ["reasonIds"] = JsonNode.Parse(reasonIds),
                    ["writtenReason"] = writtenReason,
                },
                ["reasonLabels"] = new JsonArray(reasons.Select(r => (JsonNode?)r.Label).ToArray()),
                ["description"] = $"Case file edited by {caller.Username}",
            }, ct);

            await transaction.CommitAsync(ct);
        }

        return await ViewAsync(id, caller, ct) ?? throw new InvalidOperationException();
    }

    public async Task<CaseFileView> WithdrawAsync(
        Guid id, WithdrawCaseFileRequest request, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        RequireWriter();

        var note = request.Note?.Trim() ?? string.Empty;
        if (note.Length == 0)
            throw new CaseFileRefused(400, "A note is required: say why the case file is being withdrawn.");
        if (note.Length > MaxWithdrawNoteLength)
            throw new CaseFileRefused(400, $"The note is too long (at most {MaxWithdrawNoteLength} characters).");

        var row = await _db.CaseFiles.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new CaseFileRefused(404, "No such case file.");

        RequireEditable(row, caller);

        var now = _clock.UtcNow;
        row.WithdrawnAt = now;
        row.WithdrawnByUserId = caller.UserId;
        row.WithdrawnByUsername = caller.Username;
        row.WithdrawnNote = note;
        row.UpdatedAt = now;

        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            await _db.SaveChangesAsync(ct);

            await RecordAsync(row, caller, FactType.ReportWithdrawn, new JsonObject
            {
                ["note"] = note,
                ["description"] = $"Case file withdrawn by {caller.Username}: {note}",
            }, ct);

            await transaction.CommitAsync(ct);
        }

        return await ViewAsync(id, caller, ct) ?? throw new InvalidOperationException();
    }

    /// <summary>
    /// The one permitted recapture: replaces the snapshot with the person's rows as they stand
    /// now, keeping the snapshot it replaces in the fact.
    /// </summary>
    public async Task<CaseFileView> CaptureAgainAsync(Guid id, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        RequireWriter();

        var row = await _db.CaseFiles.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new CaseFileRefused(404, "No such case file.");

        RequireEditable(row, caller);

        if (row.SnapshotRecapturedAt is not null)
            throw new CaseFileRefused(409, "The snapshot has already been taken again once. It does not change after that.");

        var current = await _db.VRChatUsers.AsNoTracking()
            .Where(u => u.UserId == row.UserId)
            .Select(u => u.LastRefreshedAt)
            .FirstOrDefaultAsync(ct);

        if (!NewerProfileExists(row.ProfileRefreshedAt, current))
        {
            throw new CaseFileRefused(409,
                "VRChat has not answered with a newer profile since the snapshot was taken, so "
                + "capturing again would copy the same thing.");
        }

        var previous = new JsonObject
        {
            ["profile"] = Parse(row.ProfileAtBan),
            ["membership"] = Parse(row.MembershipAtBan),
            ["banListEntry"] = Parse(row.BanListEntryAtBan),
            ["takenAt"] = Time(row.SnapshotTakenAt),
            ["profileRefreshedAt"] = Time(row.ProfileRefreshedAt),
        };

        var now = _clock.UtcNow;
        var snapshot = await ProfileSnapshot.CaptureAsync(_db, row.UserId, row.GroupId, ct);

        row.ProfileAtBan = snapshot.Profile;
        row.MembershipAtBan = snapshot.Membership;
        row.BanListEntryAtBan = snapshot.BanListEntry;
        row.ProfileRefreshedAt = snapshot.ProfileRefreshedAt;
        row.SnapshotTakenAt = now;
        row.SnapshotRecapturedAt = now;
        row.UpdatedAt = now;

        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            await _db.SaveChangesAsync(ct);

            await RecordAsync(row, caller, FactType.ReportSnapshotRecaptured, new JsonObject
            {
                ["previous"] = previous,
                ["snapshotTakenAt"] = Time(now),
                ["profileRefreshedAt"] = Time(snapshot.ProfileRefreshedAt),
                ["description"] = $"Profile snapshot taken again by {caller.Username}",
            }, ct);

            await transaction.CommitAsync(ct);
        }

        return await ViewAsync(id, caller, ct) ?? throw new InvalidOperationException();
    }

    // ── Reading ────────────────────────────────────────────────────────────────────────────

    public async Task<CaseFileView?> ViewAsync(Guid id, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var row = await _db.CaseFiles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null)
            return null;

        var now = _clock.UtcNow;
        var reasons = await ReasonsOfAsync([row], ct);

        var user = await _db.VRChatUsers.AsNoTracking()
            .Where(u => u.UserId == row.UserId)
            .Select(u => new { u.DisplayName, u.LastRefreshedAt })
            .FirstOrDefaultAsync(ct);

        Person? bannedBy = null;
        if (row.BanFactId is { } factId)
        {
            var fact = await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == factId, ct);
            if (fact?.ActorId is { Length: > 0 } actorId)
            {
                bannedBy = new Person(
                    (fact.ActorPlatform ?? FactPlatform.VRChat).ToString().ToLowerInvariant(),
                    actorId,
                    AuditJson.Text(AuditJson.Parse(fact.Data), "actorDisplayName"));
            }
        }

        var canEdit = CanEdit(row, caller);
        var canViewEvidence = caller.Has(ModbotPermissions.ViewEvidence);

        IReadOnlyList<EvidenceObjectView>? evidence = null;
        if (canViewEvidence)
        {
            var key = row.Id.ToString();
            var blobs = await _db.EvidenceBlobs.AsNoTracking()
                .Where(b => b.ReportId == key)
                .OrderBy(b => b.FirstStoredAt)
                .ToListAsync(ct);

            evidence = blobs.Select(Describe).ToList();
        }

        var newer = NewerProfileExists(row.ProfileRefreshedAt, user?.LastRefreshedAt);

        return new CaseFileView(
            row.Id,
            row.UserId,
            user?.DisplayName,
            row.GroupId,
            row.AuditEntryId,
            row.BanFactId,
            row.BannedAt,
            bannedBy,
            row.AuthorUserId,
            row.AuthorUsername,
            row.CreatedAt,
            row.UpdatedAt,
            row.UpdatedByUsername,
            reasons[row.Id],
            row.WrittenReason,
            row.IsWithdrawn,
            row.WithdrawnAt,
            row.WithdrawnByUsername,
            row.WithdrawnNote,
            new CaseSnapshotView(
                Element(row.ProfileAtBan),
                Element(row.MembershipAtBan),
                Element(row.BanListEntryAtBan),
                row.SnapshotTakenAt,
                row.ProfileRefreshedAt,
                row.ProfileRefreshedAt is { } refreshed ? (row.SnapshotTakenAt - refreshed).TotalSeconds : null,
                row.SnapshotRecapturedAt,
                user?.LastRefreshedAt,
                canEdit && row.SnapshotRecapturedAt is null && newer,
                ExplainSnapshot(row, newer, canEdit)),
            evidence,
            DescribeDelivery(),
            canEdit,
            canEdit && caller.Has(ModbotPermissions.UploadEvidence),
            canViewEvidence,
            now);
    }

    public async Task<CaseFileListResponse> ListAsync(
        string? userId, bool includeWithdrawn, int offset, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxListLimit);
        offset = Math.Max(0, offset);

        var query = _db.CaseFiles.AsNoTracking();

        if (userId is { Length: > 0 })
            query = query.Where(c => c.UserId == userId);

        if (!includeWithdrawn)
            query = query.Where(c => c.WithdrawnAt == null);

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Skip(offset).Take(limit).ToListAsync(ct);

        return new CaseFileListResponse(await SummariseAsync(rows, ct), total, offset, _clock.UtcNow);
    }

    /// <summary>
    /// Bans in the last <paramref name="days"/> with no case file, newest first. The pipe the
    /// accountability "bans without a report" signal reads from.
    /// </summary>
    /// <remarks>
    /// A ban is covered when a case file that stands names its audit entry, or was written for
    /// the same person on or after the ban with no audit entry of its own (one written from the
    /// ban list, which has no entry ids). A withdrawn case file covers nothing: withdrawing it
    /// is saying it should not have been written, and the ban is unwritten again.
    /// </remarks>
    public async Task<UnwrittenBanListResponse> MissingAsync(int days, int limit, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, MaxMissingDays);
        limit = Math.Clamp(limit, 1, MaxListLimit);

        var now = _clock.UtcNow;
        var since = now.AddDays(-days);

        var bans = await _db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.MemberBanned && e.OccurredAt >= since)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(5000)
            .ToListAsync(ct);

        if (bans.Count == 0)
            return new UnwrittenBanListResponse([], 0, days, since, now);

        var subjects = bans.Select(b => b.SubjectId).Distinct().ToList();

        var cases = await _db.CaseFiles.AsNoTracking()
            .Where(c => subjects.Contains(c.UserId) && c.WithdrawnAt == null)
            .Select(c => new { c.UserId, c.AuditEntryId, c.BanFactId, c.CreatedAt })
            .ToListAsync(ct);

        var unbans = await _db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.MemberUnbanned && e.OccurredAt >= since && subjects.Contains(e.SubjectId))
            .Select(e => new { e.SubjectId, e.OccurredAt })
            .ToListAsync(ct);

        var unwritten = new List<(ModbotEvent Fact, string? EntryId, DateTimeOffset? LiftedAt)>();

        foreach (var fact in bans)
        {
            var entryId = AuditJson.Text(AuditJson.Parse(fact.Data), "auditEntryId");

            var covered = cases.Any(c =>
                c.UserId == fact.SubjectId
                && ((entryId is not null && c.AuditEntryId == entryId)
                    || c.BanFactId == fact.Id
                    || (c.AuditEntryId is null && c.BanFactId is null && c.CreatedAt >= fact.OccurredAt)));

            if (covered)
                continue;

            var lifted = unbans
                .Where(u => u.SubjectId == fact.SubjectId && u.OccurredAt > fact.OccurredAt)
                .Select(u => (DateTimeOffset?)u.OccurredAt)
                .Min();

            unwritten.Add((fact, entryId, lifted));
        }

        var page = unwritten.Take(limit).ToList();
        var names = await PeopleNames.LookupAsync(
            _db,
            page.Select(p => p.Fact.SubjectId).Concat(page.Select(p => p.Fact.ActorId ?? string.Empty)).ToList(),
            ct);

        return new UnwrittenBanListResponse(
            page.Select(p => new UnwrittenBan(
                p.Fact.SubjectId,
                names.GetValueOrDefault(p.Fact.SubjectId),
                p.Fact.OccurredAt,
                p.Fact.OccurredBefore,
                p.EntryId,
                p.Fact.Id,
                p.Fact.ActorId is { Length: > 0 } actor
                    ? new Person(
                        (p.Fact.ActorPlatform ?? FactPlatform.VRChat).ToString().ToLowerInvariant(),
                        actor,
                        names.GetValueOrDefault(actor) ?? AuditJson.Text(AuditJson.Parse(p.Fact.Data), "actorDisplayName"))
                    : null,
                p.LiftedAt)).ToList(),
            unwritten.Count,
            days,
            since,
            now);
    }

    /// <summary>For each person: the newest case file that stands, and how many there are in all.</summary>
    public async Task<IReadOnlyList<CaseFileLookup>> LookupAsync(IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var ids = userIds.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Take(500).ToList();
        if (ids.Count == 0)
            return [];

        var rows = await _db.CaseFiles.AsNoTracking()
            .Where(c => ids.Contains(c.UserId))
            .Select(c => new { c.Id, c.UserId, c.CreatedAt, c.WithdrawnAt })
            .ToListAsync(ct);

        return ids.Select(id =>
        {
            var mine = rows.Where(r => r.UserId == id).ToList();
            var standing = mine.Where(r => r.WithdrawnAt == null).OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            return new CaseFileLookup(id, standing?.Id, mine.Count);
        }).ToList();
    }

    // ── Pieces ─────────────────────────────────────────────────────────────────────────────

    private void RequireWriter()
    {
        if (_facts is null || _partitions is null)
            throw new CaseFileRefused(503, "Case files cannot be written in this process: the fact log is not available here.");
    }

    private static void RequireEditable(CaseFile row, Caller caller)
    {
        if (!caller.Has(ModbotPermissions.Ban) && row.AuthorUserId != caller.UserId)
            throw new CaseFileRefused(403, "Only the author, or somebody who may ban, can change a case file.");

        if (row.IsWithdrawn)
            throw new CaseFileRefused(409, "This case file has been withdrawn. Write a new one instead of changing this one.");
    }

    private static bool CanEdit(CaseFile row, Caller caller)
        => !row.IsWithdrawn && (caller.Has(ModbotPermissions.Ban) || row.AuthorUserId == caller.UserId);

    /// <summary>The reasons must exist and be active; the written reason is required when any of them says so.</summary>
    private async Task<(IReadOnlyList<BanReason> Reasons, string WrittenReason)> ValidateContentAsync(
        IReadOnlyList<Guid>? reasonIds, string? writtenReason, CancellationToken ct)
    {
        var all = await BanReasonList.AllAsync(_db, _clock, ct);
        var wanted = (reasonIds ?? []).Distinct().ToList();

        if (wanted.Count == 0)
            throw new CaseFileRefused(400, "Pick at least one reason.");

        var reasons = wanted.Select(id => all.FirstOrDefault(r => r.Id == id)).ToList();
        if (reasons.Any(r => r is null))
            throw new CaseFileRefused(400, "One of those reasons is not on the list.");

        if (reasons.Any(r => !r!.IsActive))
            throw new CaseFileRefused(400, "One of those reasons has been switched off. Pick from the current list.");

        var text = (writtenReason ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

        if (text.Length > MaxWrittenReasonLength)
            throw new CaseFileRefused(400, $"The written reason is too long (at most {MaxWrittenReasonLength:N0} characters).");

        var needsText = reasons.Where(r => r!.NeedsWrittenReason).Select(r => r!.Label).ToList();
        if (text.Length == 0 && needsText.Count > 0)
            throw new CaseFileRefused(400, $"\"{needsText[0]}\" needs a written reason: say what happened.");

        return (reasons.Select(r => r!).OrderBy(r => r.SortOrder).ToList(), text);
    }

    /// <param name="AuditEntryId">VRChat's id for the audit entry, when the ban was recorded from the log.</param>
    /// <param name="FactId">The ban fact, when Modbot recorded one.</param>
    private readonly record struct BanReference(string? AuditEntryId, long? FactId, DateTimeOffset? BannedAt);

    /// <summary>
    /// The ban this case file is about: the named audit entry, else the newest recorded ban for
    /// the person, else the ban list's row. A person with no ban anywhere can still be written
    /// up -- Modbot's window is shorter than the group's history -- and the case file then
    /// carries no ban time.
    /// </summary>
    private async Task<BanReference> FindBanAsync(string userId, string? groupId, string? auditEntryId, CancellationToken ct)
    {
        var facts = await _db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.MemberBanned && e.SubjectId == userId)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(50)
            .ToListAsync(ct);

        var withIds = facts
            .Select(f => (Fact: f, EntryId: AuditJson.Text(AuditJson.Parse(f.Data), "auditEntryId")))
            .ToList();

        if (auditEntryId is { Length: > 0 })
        {
            var named = withIds.FirstOrDefault(f => f.EntryId == auditEntryId);
            if (named.Fact is null)
                throw new CaseFileRefused(404, "No recorded ban of this person has that audit entry id.");

            return new BanReference(auditEntryId, named.Fact.Id, named.Fact.OccurredAt);
        }

        if (withIds.Count > 0)
        {
            var newest = withIds[0];
            return new BanReference(newest.EntryId, newest.Fact.Id, newest.Fact.OccurredAt);
        }

        var listed = groupId is null
            ? null
            : await _db.GroupBans.AsNoTracking()
                .Where(b => b.GroupId == groupId && b.UserId == userId)
                .Select(b => b.BannedAt)
                .FirstOrDefaultAsync(ct);

        return new BanReference(null, null, listed);
    }

    /// <summary>One case file per ban. A second one for the same ban is refused, naming the first.</summary>
    private async Task RefuseDuplicateAsync(string userId, BanReference ban, CancellationToken ct)
    {
        var standing = await _db.CaseFiles.AsNoTracking()
            .Where(c => c.UserId == userId && c.WithdrawnAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new { c.Id, c.AuditEntryId, c.BanFactId, c.CreatedAt })
            .ToListAsync(ct);

        var existing = standing.FirstOrDefault(c =>
            (ban.AuditEntryId is not null && c.AuditEntryId == ban.AuditEntryId)
            || (ban.FactId is not null && c.BanFactId == ban.FactId)
            || (ban.BannedAt is { } at ? c.CreatedAt >= at : c.AuditEntryId is null && c.BanFactId is null));

        if (existing is not null)
        {
            throw new CaseFileRefused(
                409,
                "This ban already has a case file.",
                existing.Id);
        }
    }

    private async Task<string> RequestRefreshAsync(string userId, CancellationToken ct)
    {
        if (_profiles is null)
            return "NotAvailable";

        // The "opened in Modbot" tier: behind only people a client is seeing in an instance right
        // now, which is the tier the profile card already uses when the write-up form is opened.
        // Tier 1 is reserved for instance sightings and would misreport on the sync health page.
        var asked = await _profiles.RequestRefreshAsync(userId, RefreshReason.OpenedInModbot, ct);

        return asked.Outcome.ToString();
    }

    private async Task RecordAsync(CaseFile row, Caller caller, string type, JsonObject data, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        data["caseId"] = row.Id.ToString();
        data["userId"] = row.UserId;
        data["actorDisplayName"] = caller.Username;

        await _partitions!.EnsureForAsync(now, ct);
        await _facts!.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = row.UserId,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = caller.UserId.ToString(),
                Source = FactSource.Manual,
                Data = data,
            },
            ct);
    }

    private async Task<IReadOnlyList<CaseFileSummary>> SummariseAsync(IReadOnlyList<CaseFile> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return [];

        var reasons = await ReasonsOfAsync(rows, ct);
        var names = await PeopleNames.LookupAsync(_db, rows.Select(r => r.UserId).ToList(), ct);

        var keys = rows.Select(r => r.Id.ToString()).ToList();
        var counts = await _db.EvidenceBlobs.AsNoTracking()
            .Where(b => b.ReportId != null && keys.Contains(b.ReportId) && b.DestroyedAt == null)
            .GroupBy(b => b.ReportId!)
            .Select(g => new { ReportId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ReportId, g => g.Count, ct);

        return rows.Select(r => new CaseFileSummary(
            r.Id,
            r.UserId,
            names.GetValueOrDefault(r.UserId),
            r.BannedAt,
            r.AuditEntryId,
            r.AuthorUsername,
            reasons[r.Id],
            r.CreatedAt,
            r.UpdatedAt,
            r.IsWithdrawn,
            counts.GetValueOrDefault(r.Id.ToString()))).ToList();
    }

    /// <summary>The picked reasons for each case file, with their current labels; an id no longer on the list shows as its id.</summary>
    private async Task<Dictionary<Guid, IReadOnlyList<CaseFileReason>>> ReasonsOfAsync(IReadOnlyList<CaseFile> rows, CancellationToken ct)
    {
        var all = await _db.BanReasons.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);

        return rows.ToDictionary(
            r => r.Id,
            r => (IReadOnlyList<CaseFileReason>)ParseIds(r.ReasonIds)
                .Select(id => all.TryGetValue(id, out var reason)
                    ? new CaseFileReason(id, reason.Label, reason.IsActive)
                    : new CaseFileReason(id, id.ToString(), false))
                .ToList());
    }

    private EvidenceDeliveryView DescribeDelivery()
    {
        if (_evidenceOptions is null || _store is null || _monitor is null)
        {
            return new EvidenceDeliveryView(
                false, false,
                "Evidence storage is not running in this process.",
                false,
                0,
                EvidenceContentType.Allowed);
        }

        var health = _monitor.Current;
        var capabilities = EvidenceSettingsService.Describe(_store.Capabilities);

        return new EvidenceDeliveryView(
            _evidenceOptions.Backend is not EvidenceBackend.None,
            health.UploadsAllowed,
            health.Explanation,
            capabilities.DirectDeliveryAvailable,
            _evidenceOptions.MaxFileBytes,
            EvidenceContentType.Allowed);
    }

    private static bool NewerProfileExists(DateTimeOffset? captured, DateTimeOffset? now)
        => now is { } fresh && (captured is null || fresh > captured);

    private static string ExplainSnapshot(CaseFile row, bool newer, bool canEdit)
    {
        var taken = row.SnapshotTakenAt.ToString("d MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);

        var offerNewer = newer && canEdit && row.SnapshotRecapturedAt is null;

        if (row.ProfileAtBan is null)
        {
            var none = $"No profile had been fetched as of {taken}.";
            return offerNewer ? none + " A newer profile is available." : none;
        }

        var refreshed = row.ProfileRefreshedAt is { } at
            ? $" Profile fetched {Age(row.SnapshotTakenAt - at)} earlier."
            : string.Empty;

        var sentence = row.SnapshotRecapturedAt is { } again
            ? $"Taken {taken}, captured again {again.ToString("d MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture)}.{refreshed}"
            : $"Taken {taken}.{refreshed}";

        if (offerNewer)
            sentence += " A newer profile is available.";

        return sentence;
    }

    private static string Age(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return "under a minute";
        if (span < TimeSpan.FromHours(1)) return $"{Math.Round(span.TotalMinutes)} minutes";
        if (span < TimeSpan.FromDays(1)) return $"{Math.Round(span.TotalHours, 1)} hours";
        return $"{Math.Round(span.TotalDays, 1)} days";
    }

    private static EvidenceObjectView Describe(EvidenceBlob blob) => new(
        blob.Hash,
        blob.ByteSize,
        blob.ContentType,
        blob.FileName,
        blob.UploaderId,
        blob.ReportId,
        blob.Origin.ToString(),
        blob.FirstStoredAt,
        blob.IsDestroyed,
        blob.DestroyedAt,
        blob.DestroyedBy,
        blob.DestroyedReason);

    private static string Json(IEnumerable<Guid> ids)
        => JsonSerializer.Serialize(ids.Select(id => id.ToString()).ToList(), Web);

    private static List<Guid> ParseIds(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json, Web) ?? [])
                .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Time(DateTimeOffset? at) => at?.ToString("O", CultureInfo.InvariantCulture);

    private static JsonNode? Parse(string? json) => string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);

    private static JsonElement? Element(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json).RootElement.Clone();
}
