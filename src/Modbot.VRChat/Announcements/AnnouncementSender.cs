using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Announcements;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.Posts;
using Serilog;

namespace Modbot.VRChat.Announcements;

/// <param name="Sent">Announcements VRChat accepted this pass.</param>
/// <param name="Failed">Announcements that turned Refused or Failed this pass.</param>
public sealed record AnnouncementPass(int Sent, int Failed);

/// <summary>
/// Sends messages to everyone in a group instance: at once when a person presses Send, and at
/// their time for the scheduled ones, one per pass of the calendar's loop.
/// </summary>
/// <remarks>
/// <para>
/// <strong>At most once, and never again by itself.</strong> A row is claimed (Sending, with the
/// time) and saved before VRChat is asked, and whatever happens next ends it: Sent, Refused with
/// VRChat's status and words, or Failed with the reason. A 429, an answer that was not clear, a
/// call the gate would not send, a process that stopped mid-send: each is written down and nothing
/// is tried again (foundation §4.3.1). Sending again is a person's decision.
/// </para>
/// <para>
/// <strong>Checked before VRChat is asked:</strong> the instance is still open and in the managed
/// group, and Modbot's VRChat account holds Create Instance Announcement as last read. A check
/// that fails makes no request. Whether VRChat also wants the account to be standing in the
/// instance is not known; if it does, its refusal is shown and kept like any other.
/// </para>
/// <para>
/// Every outcome is a fact about the group: who pressed Send, or who scheduled it.
/// </para>
/// </remarks>
public sealed class AnnouncementSender
{
    private readonly InstanceAnnounce _announce;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly ILogger _log;

    public AnnouncementSender(
        InstanceAnnounce announce,
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(announce);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _announce = announce;
        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>
    /// Sends a new announcement now, for a person who pressed Send. The row is saved claimed before
    /// VRChat is asked, and comes back with what happened written on it.
    /// </summary>
    public async Task<VRChatAnnouncement> SendNowAsync(VRChatAnnouncement announcement, Guid actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(announcement);

        var now = _clock.UtcNow;
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        announcement.State = VRChatAnnouncementStates.Sending;
        announcement.SentAt = now;
        announcement.UpdatedAt = now;
        announcement.Version++;

        _db.VRChatAnnouncements.Add(announcement);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await SendClaimedAsync(announcement, settings, actor, VRChatCallPriority.Interactive, now, ct).ConfigureAwait(false);
        return announcement;
    }

    /// <summary>One pass: what was cut off mid-send and what is too late turn Failed, then one that is due goes.</summary>
    public async Task<AnnouncementPass> RunOnceAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var pass = new Counts();

        await FailStuckAsync(now, pass, ct).ConfigureAwait(false);
        await FailLateAsync(now, pass, ct).ConfigureAwait(false);
        await SendOneAsync(now, pass, ct).ConfigureAwait(false);

        return new AnnouncementPass(pass.Sent, pass.Failed);
    }

    // ── The pass ─────────────────────────────────────────────────────────────────────────

    private async Task FailStuckAsync(DateTimeOffset now, Counts pass, CancellationToken ct)
    {
        var before = now - AnnouncementRules.StuckSendingAfter;

        var stuck = await _db.VRChatAnnouncements
            .Where(a => a.State == VRChatAnnouncementStates.Sending && (a.SentAt == null || a.SentAt < before))
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var announcement in stuck)
        {
            await EndAsync(announcement, VRChatAnnouncementStates.Failed, AnnouncementRules.CutOff, null, null, actor: null, now, ct)
                .ConfigureAwait(false);
            pass.Failed++;
        }
    }

    private async Task FailLateAsync(DateTimeOffset now, Counts pass, CancellationToken ct)
    {
        var tooOld = now - AnnouncementRules.LateLimit;

        var late = await _db.VRChatAnnouncements
            .Where(a => a.State == VRChatAnnouncementStates.Scheduled && a.SendAt < tooOld)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var announcement in late.Where(a => AnnouncementRules.IsLate(a, now)))
        {
            await EndAsync(announcement, VRChatAnnouncementStates.Failed, AnnouncementRules.NotSentOnTime, null, null, actor: null, now, ct)
                .ConfigureAwait(false);
            pass.Failed++;
        }
    }

    private async Task SendOneAsync(DateTimeOffset now, Counts pass, CancellationToken ct)
    {
        // The soonest due first. Tracked: the claim saves it as it was read.
        var next = await _db.VRChatAnnouncements
            .Where(a => a.State == VRChatAnnouncementStates.Scheduled && a.SendAt <= now)
            .OrderBy(a => a.SendAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (next is null || AnnouncementRules.IsLate(next, now))
            return;

        next.State = VRChatAnnouncementStates.Sending;
        next.SentAt = now;
        next.UpdatedAt = now;
        next.Version++;

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody cancelled it since it was read. Nothing is sent.
            _db.ChangeTracker.Clear();
            return;
        }

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        var state = await SendClaimedAsync(next, settings, actor: null, VRChatCallPriority.Background, now, ct).ConfigureAwait(false);

        if (state == VRChatAnnouncementStates.Sent)
            pass.Sent++;
        else
            pass.Failed++;
    }

    // ── One send ─────────────────────────────────────────────────────────────────────────

    /// <summary>Checks, asks VRChat once, and writes what happened. The row is already saved as Sending.</summary>
    private async Task<string> SendClaimedAsync(
        VRChatAnnouncement announcement, Settings settings, Guid? actor, VRChatCallPriority priority, DateTimeOffset now, CancellationToken ct)
    {
        var groupId = settings.ManagedGroupId?.Trim() ?? string.Empty;

        if (groupId.Length == 0 || !string.Equals(groupId, announcement.GroupId, StringComparison.Ordinal))
            return await EndAsync(announcement, VRChatAnnouncementStates.Failed, AnnouncementRules.NotOurGroup, null, null, actor, now, ct).ConfigureAwait(false);

        var open = await _db.VRChatInstances.AsNoTracking()
            .AnyAsync(i => i.Id == announcement.InstanceId && i.ClosedAt == null && i.GroupId == groupId, ct)
            .ConfigureAwait(false);

        if (!open)
            return await EndAsync(announcement, VRChatAnnouncementStates.Failed, AnnouncementRules.InstanceClosed, null, null, actor, now, ct).ConfigureAwait(false);

        // Known to be missing as last read: said plainly, and VRChat is not asked.
        if (VRChatGroupPermissions.Holds(settings.VRChatAccountPermissions, VRChatGroupPermissions.CreateInstanceAnnouncement) == false)
        {
            var missing = VRChatGroupPermissions.Sentence(new MissingGroupPermission(
                VRChatGroupPermissions.CreateInstanceAnnouncement, groupId, null, null));

            return await EndAsync(
                    announcement, VRChatAnnouncementStates.Failed, missing, null,
                    VRChatGroupPermissions.CreateInstanceAnnouncement, actor, now, ct)
                .ConfigureAwait(false);
        }

        var result = await _announce
            .SendAsync(groupId, announcement.Location, announcement.Title, announcement.Message, priority, ct)
            .ConfigureAwait(false);

        var (state, error, status, missingPermission) = Outcome(result, groupId, settings);

        if (state != VRChatAnnouncementStates.Sent)
        {
            _log.Warning(
                "Announcement {AnnouncementId} did not go to VRChat ({Status}): {Error}",
                announcement.Id, result.StatusCode, error);
        }

        return await EndAsync(announcement, state, error, status, missingPermission, actor, now, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What one answer means: Sent; Refused, with VRChat's status and words, when VRChat said no;
    /// Failed otherwise. Nothing here leads to sending again.
    /// </summary>
    public static (string State, string? Error, int? Status, string? MissingPermission) Outcome(
        VRChatResult<string> result, string groupId, Settings? settings)
    {
        int? status = result.StatusCode == 0 ? null : result.StatusCode;

        if (result.Success)
            return (VRChatAnnouncementStates.Sent, null, status, null);

        var said = Reason(result);

        if (result.IsRateLimited)
            return (VRChatAnnouncementStates.Failed, $"{said} It is not sent again.", status, null);

        // Nothing reached VRChat: a cold stop, a sign-in that is waiting or was refused, no account.
        if (PostVRChatSender.NothingMade(result))
            return (VRChatAnnouncementStates.Failed, said, status, null);

        // May have gone: a 5xx, a 408, a timeout, a lost connection, or a Cloudflare page.
        if (PostVRChatSender.Unclear(result) || result.Kind == VRChatFailureKind.WafBlocked)
        {
            var what = status is { } s ? $"VRChat gave no clear answer ({s})." : "VRChat gave no clear answer.";
            return (VRChatAnnouncementStates.Failed, $"{what} It may have gone out. {said}", status, null);
        }

        // VRChat answered and said no.
        var missing = VRChatGroupPermissions.Refusal(
            result.StatusCode, result.Kind, InstanceAnnounce.Operation, groupId, result.RawResponse, settings)?.Permission;

        return (VRChatAnnouncementStates.Refused, said, status, missing);
    }

    /// <summary>VRChat's own words when it gave any, otherwise the gate's.</summary>
    private static string Reason(VRChatResult<string> result) =>
        (result.Kind == VRChatFailureKind.WafBlocked ? null : VRChatRefusal.MessageOf(result.RawResponse))
        ?? result.ErrorMessage
        ?? $"VRChat answered {result.StatusCode}.";

    private async Task<string> EndAsync(
        VRChatAnnouncement announcement,
        string state,
        string? error,
        int? status,
        string? missingPermission,
        Guid? actor,
        DateTimeOffset now,
        CancellationToken ct)
    {
        announcement.State = state;
        announcement.Error = Trim(error);
        announcement.StatusCode = status;
        announcement.MissingPermission = missingPermission;
        announcement.UpdatedAt = now;
        announcement.Version++;

        // The fact and the row land together.
        await using (var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            var data = Payload(announcement);
            data["state"] = state;

            if (status is not null)
                data["status"] = status;

            if (announcement.Error is not null)
                data["error"] = announcement.Error;

            if (missingPermission is not null)
                data["missingPermission"] = missingPermission;

            await WriteFactAsync(
                    state == VRChatAnnouncementStates.Sent ? FactType.GroupAnnouncementSent : FactType.GroupAnnouncementFailed,
                    announcement, actor, now, data, ct)
                .ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        return state;
    }

    /// <summary>What every announcement fact carries.</summary>
    public static JsonObject Payload(VRChatAnnouncement announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);

        return new JsonObject
        {
            ["announcementId"] = announcement.Id.ToString(),
            ["instanceId"] = announcement.InstanceId.ToString(),
            ["location"] = announcement.Location,
            ["title"] = announcement.Title,
            ["message"] = announcement.Message,
            ["scheduledBy"] = announcement.CreatedByUserId?.ToString(),
        };
    }

    private async Task WriteFactAsync(
        string type, VRChatAnnouncement announcement, Guid? actor, DateTimeOffset now, JsonObject data, CancellationToken ct)
    {
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = announcement.GroupId,
                ActorPlatform = actor is null ? null : FactPlatform.Modbot,
                ActorId = actor?.ToString(),
                Source = actor is null ? FactSource.Modbot : FactSource.Manual,
                Data = data,
            },
            ct).ConfigureAwait(false);
    }

    private static string? Trim(string? error) =>
        error is null ? null : error.Length <= 1024 ? error : error[..1024];

    private sealed class Counts
    {
        public int Sent { get; set; }

        public int Failed { get; set; }
    }
}
