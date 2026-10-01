using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Cases;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat;
using Modbot.VRChat.Moderation;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Users;
using Npgsql;
using Serilog;

namespace Modbot.Api.Features.Moderation;

/// <summary>Why an action was refused before anything was sent, with the status to answer with.</summary>
public sealed class ModerationRefused(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Kicking, banning and unbanning a person in the managed group, from Modbot.
/// </summary>
/// <remarks>
/// <para>
/// M4 §4. Modbot has only observed until now, and writing changes three things: interactive work
/// competes with background sync for the request budget, the action can fail, and this is where
/// the reason for it is captured. The rules that follow from that are all in here.
/// </para>
/// <para>
/// <strong>Nothing is recorded as done unless VRChat accepted it.</strong> The order is: claim the
/// key, ask VRChat, and only then write the fact, the case file and the change to what Modbot
/// stores. A refusal writes <see cref="FactType.ActionFailed"/>, which is not a claim that anything
/// happened, and the moderator is shown what VRChat said. A 429 is never retried (spec 4.3.1): it
/// means this action failed and pressing again sooner only lengthens the wait.
/// </para>
/// <para>
/// <strong>Acting twice from one confirmation is impossible.</strong> The browser generates a key
/// when the dialog opens; the row that carries it is claimed on a unique index before the outbound
/// call, so a second press — or a retry after a timeout — finds the first row and gets its answer
/// back rather than sending anything (M4 §4.3). The disabled button in the browser is a courtesy;
/// this is the guarantee.
/// </para>
/// <para>
/// <strong>What Modbot stores is updated, not fought over.</strong> A kick marks the member as
/// gone and a ban writes the ban-list row, so the page is right the moment it reloads; the member
/// and ban sweeps then confirm it on their next pass exactly as they confirm everything else. The
/// sweeps stay the only thing that decides what those tables mean.
/// </para>
/// <para>
/// <strong>A ban does not need a membership.</strong> VRChat's group ban takes a user id in the
/// body — not a membership id, not a member — so banning somebody who has never joined is an
/// ordinary request, and it is an ordinary thing to want: a moderator hears about a person from
/// another group, from Discord or from a flag, and keeps them out before they ever arrive. Nothing
/// here reads the member list before banning, and the ban-list row is written whether or not there
/// was ever a member row. A person Modbot has never seen is recorded as a person by the ban
/// itself, so the Bans page has somebody to name rather than a bare id.
/// </para>
/// <para>
/// <strong>Kick and unban are not the same question, and are not pre-refused either.</strong>
/// Kicking somebody who is not in the group and unbanning somebody who was never banned are both
/// meaningless, and VRChat answers both with a 404. Modbot does not refuse them itself from what
/// it has stored: the member list rests fifteen minutes between sweeps and the ban list thirty, so
/// a check against them would block a moderator from kicking somebody who joined a minute ago —
/// exactly when a kick matters most. VRChat decides, and the refusal is turned into a sentence a
/// moderator can read instead of the status line VRChat sends back.
/// </para>
/// </remarks>
public sealed class ModerationActionService
{
    public const string Kick = "kick";
    public const string Ban = "ban";
    public const string Unban = "unban";

    /// <summary>Let somebody in who asked to join (join requests design §4).</summary>
    public const string Approve = "approve";

    /// <summary>Turn a join request down.</summary>
    public const string Reject = "reject";

    public const int MaxKeyLength = 128;
    public const int MaxNoteLength = 20_000;

    /// <summary>What a moderator is told when VRChat says there is nothing there to act on.</summary>
    public const string Gone = "That request is no longer waiting. Somebody may have answered it in VRChat.";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly GroupModeration _vrchat;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly CaseFileService? _cases;
    private readonly GroupJoinRequests? _requests;
    private readonly VRChatUserProfiles? _profiles;
    private readonly ILinkedDiscordBans? _discord;

    public ModerationActionService(
        ModbotContext db,
        IModbotClock clock,
        GroupModeration vrchat,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        CaseFileService? cases = null,
        GroupJoinRequests? requests = null,
        VRChatUserProfiles? profiles = null,
        ILinkedDiscordBans? discord = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(vrchat);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _vrchat = vrchat;
        _facts = facts;
        _partitions = partitions;
        _cases = cases;
        _requests = requests;
        _profiles = profiles;
        _discord = discord;
    }

    public async Task<ModerationActionResult> RunAsync(
        string action, ModerationActionRequest request, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        var userId = (request.UserId ?? string.Empty).Trim();
        var key = (request.Key ?? string.Empty).Trim();
        var note = (request.Note ?? string.Empty).Trim();

        // Refusing a person with no VRChat id is not a formality. A ban needs somebody to ban, and
        // an empty id would reach VRChat as a request against the group itself.
        if (userId.Length == 0)
            throw new ModerationRefused(400, "Pick a person first: Modbot was given no VRChat id.");

        if (key.Length == 0)
            throw new ModerationRefused(400, "A key is required, so one confirmation cannot act twice.");

        if (key.Length > MaxKeyLength)
            throw new ModerationRefused(400, $"The key is too long (at most {MaxKeyLength} characters).");

        if (note.Length > MaxNoteLength)
            throw new ModerationRefused(400, $"The note is too long (at most {MaxNoteLength} characters).");

        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
            throw new ModerationRefused(409, "No VRChat group is set up yet, so there is nothing to act in.");

        // Modbot signs in as an account of its own and does everything as that account. Kicking or
        // banning it would take away the access every sync depends on, from inside the thing doing
        // the kicking, and no deployment recovers from that without a person editing the group by
        // hand. Ordinal: the id is opaque text and is compared as text (foundation §3.1.1).
        if (settings.VRChatSessionUserId is { Length: > 0 } self
            && string.Equals(self, userId, StringComparison.Ordinal))
        {
            throw new ModerationRefused(400, "That is the account Modbot signs in as. Modbot will not act on itself.");
        }

        var reasons = await ReasonsAsync(action, request.ReasonIds, note, settings.RequireModerationClassification, ct);

        // The key is claimed before anything is sent. Whatever happens next -- a timeout, a second
        // click, a browser that retried -- there is exactly one row and therefore one action.
        var (row, alreadyRan) = await ClaimAsync(action, key, userId, groupId, caller, reasons, note, ct);

        if (alreadyRan)
            return Describe(row, repeat: true, MissingPermissionOf(row, settings));

        return await SendAsync(row, action, userId, groupId, caller, reasons, note, settings, ct);
    }

    // ── The outbound call, and everything that follows from its answer ──────────────────────

    private async Task<ModerationActionResult> SendAsync(
        ModerationAction row,
        string action,
        string userId,
        string groupId,
        Caller caller,
        IReadOnlyList<BanReason> reasons,
        string note,
        Modbot.Core.Data.Entities.Settings settings,
        CancellationToken ct)
    {
        var (accepted, statusCode, message, rateLimited, missing) = await AskVRChatAsync(action, groupId, userId, settings, ct);

        var now = _clock.UtcNow;

        row.FinishedAt = now;
        row.Succeeded = accepted;
        row.StatusCode = statusCode;
        row.RateLimited = !accepted && rateLimited;
        row.FailureMessage = accepted ? null : message;

        if (!accepted)
        {
            await RecordFailureAsync(row, action, userId, caller, reasons, note, message, statusCode, now, ct);

            // "They are not banned" from VRChat is not a reason to leave a Discord ban standing:
            // a ban made through Modbot is lifted everywhere, and Discord answering "not banned"
            // is a harmless nothing to do. Any other refusal means nothing was changed, so
            // nothing is asked of Discord.
            if (action == Unban && statusCode == 404)
                await AlsoInDiscordAsync(row, action, userId, caller, reasons, causedByFactId: null, ct);

            return Describe(row, repeat: false, missing);
        }

        // The case file an unban lifts, found before the fact is written so the fact can name it.
        var lifting = action == Unban ? await CaseToLiftAsync(userId, ct) : null;

        var factId = await RecordSuccessAsync(row, action, userId, groupId, caller, reasons, note, now, lifting, ct);

        string? caseFileError = null;

        if (action == Ban)
        {
            await RememberPersonAsync(userId, ct);

            // The case file is written after the ban, outside its transaction, and its failure is
            // not allowed to unsay the ban: the ban happened in VRChat whether or not the write-up
            // saved.
            row.CaseFileId = await WriteCaseFileAsync(userId, factId, now, reasons, note, caller, ct);
        }
        else if (lifting is { } caseId)
        {
            // The same rule for the lift: the unban happened in VRChat, and a case file that could
            // not be marked is said beside the answer, never instead of it.
            (row.CaseFileId, caseFileError) = await LiftCaseFileAsync(caseId, factId, now, reasons, note, caller, ct);
        }

        await _db.SaveChangesAsync(ct);

        // Last, and after everything VRChat's half needed is saved: the ban above stands whatever
        // Discord says next.
        if (action is Ban or Unban)
            await AlsoInDiscordAsync(row, action, userId, caller, reasons, factId, ct);

        return Describe(row, repeat: false, missing: null, caseFileError);
    }

    /// <summary>
    /// The case file an unban lifts, or null when there is none to mark. Never throws: the unban is
    /// already done in VRChat by the time this is asked.
    /// </summary>
    private async Task<Guid?> CaseToLiftAsync(string userId, CancellationToken ct)
    {
        if (_cases is null)
            return null;

        try
        {
            return await _cases.CaseToLiftAsync(userId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Logger.ForContext(LogArea.Name, LogArea.Moderation)
                .Warning(e, "Could not look for the case file an unban lifts");
            return null;
        }
    }

    /// <summary>Marks the case file lifted. Returns its id when it was, or the sentence to show when it was not.</summary>
    private async Task<(Guid? CaseId, string? Error)> LiftCaseFileAsync(
        Guid caseId,
        long unbanFactId,
        DateTimeOffset at,
        IReadOnlyList<BanReason> reasons,
        string note,
        Caller caller,
        CancellationToken ct)
    {
        try
        {
            var lifted = await _cases!.LiftAsync(caseId, unbanFactId, at, reasons.Select(r => r.Id).ToList(), note, caller, ct);
            return (lifted ? caseId : null, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Logger.ForContext(LogArea.Name, LogArea.Moderation)
                .Warning(e, "Unbanned in VRChat, but could not mark case file {CaseId} as lifted", caseId);
            return (null, "Unbanned, but the case file could not be marked as lifted.");
        }
    }

    /// <summary>
    /// A ban or unban made here is made on the person's linked Discord account too, when Discord is
    /// set up and they are linked. Independent of the ban sync switches: those are about bans made
    /// somewhere else.
    /// </summary>
    /// <remarks>
    /// Never throws. VRChat has already said yes, and a Discord failure that took the answer down
    /// with it would tell a moderator a ban did not happen when it did.
    /// </remarks>
    private async Task AlsoInDiscordAsync(
        ModerationAction row,
        string action,
        string userId,
        Caller caller,
        IReadOnlyList<BanReason> reasons,
        long? causedByFactId,
        CancellationToken ct)
    {
        if (_discord is null)
            return;

        var why = reasons.Count == 0 ? null : string.Join(", ", reasons.Select(r => r.Label));

        LinkedDiscordOutcome outcome;

        try
        {
            outcome = action == Ban
                ? await _discord.BanAsync(userId, caller.Username, why, causedByFactId, ct)
                : await _discord.UnbanAsync(userId, caller.Username, why, causedByFactId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Logger.ForContext(LogArea.Name, LogArea.Moderation)
                .Warning(e, "Could not {Action} the linked Discord account after doing it in VRChat", action);
            outcome = LinkedDiscordOutcome.Failed("Modbot could not reach Discord.");
        }

        // Kept on the row, so a second press of the same key is told what the first press was.
        row.DiscordDone = outcome.Status == LinkedDiscordStatus.Done;
        row.DiscordError = outcome.Status == LinkedDiscordStatus.Failed ? outcome.Error : null;

        await _db.SaveChangesAsync(ct);
    }

    private async Task<(bool Accepted, int StatusCode, string? Message, bool RateLimited, MissingGroupPermission? Missing)> AskVRChatAsync(
        string action, string groupId, string userId, Modbot.Core.Data.Entities.Settings settings, CancellationToken ct)
    {
        switch (action)
        {
            case Kick:
            {
                var result = await _vrchat.KickAsync(groupId, userId, ct);
                return Read(result.Success, result.StatusCode, result.ErrorMessage, result.IsRateLimited, result.Kind, result.RawResponse);
            }

            case Ban:
            {
                var result = await _vrchat.BanAsync(groupId, userId, ct);
                return Read(result.Success, result.StatusCode, result.ErrorMessage, result.IsRateLimited, result.Kind, result.RawResponse);
            }

            case Unban:
            {
                var result = await _vrchat.UnbanAsync(groupId, userId, ct);
                return Read(result.Success, result.StatusCode, result.ErrorMessage, result.IsRateLimited, result.Kind, result.RawResponse);
            }

            case Approve or Reject:
            {
                if (_requests is null)
                    throw new ModerationRefused(503, "This deployment is not set up to answer join requests.");

                var result = action == Approve
                    ? await _requests.ApproveAsync(groupId, userId, ct)
                    : await _requests.RejectAsync(groupId, userId, ct);

                // A 404 here is the ordinary ending, not a fault: somebody answered the request
                // in VRChat, or the person withdrew it, between the list being read and the
                // button being pressed. Said in words, because "Not Found" in front of a
                // moderator reads as a bug in Modbot (join requests design §5).
                if (!result.Success && result.StatusCode == 404)
                    return (false, 404, Gone, false, null);

                return Read(result.Success, result.StatusCode, result.ErrorMessage, result.IsRateLimited, result.Kind, result.RawResponse);
            }

            default:
                throw new ModerationRefused(400, $"'{action}' is not something Modbot can do.");
        }

        (bool, int, string?, bool, MissingGroupPermission?) Read(
            bool success, int statusCode, string? error, bool rateLimited, VRChatFailureKind kind, string? body)
        {
            var waiting = rateLimited || kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting;

            // VRChat's own words before the gate's, which for a refusal is only the status line
            // ("Forbidden"). Never for a Cloudflare block, whose body is not VRChat's.
            var own = kind == VRChatFailureKind.WafBlocked ? null : VRChatRefusal.MessageOf(body);
            var said = PlainRefusal(action, statusCode) ?? own ?? error ?? "VRChat did not say why.";

            var missing = success
                ? null
                : VRChatGroupPermissions.Refusal(statusCode, kind, OperationOf(action), groupId, body, settings);

            return (success, statusCode, success ? null : said, waiting, missing);
        }
    }

    /// <summary>The operation name each action's VRChat call gives its endpoint.</summary>
    internal static string OperationOf(string action) => action switch
    {
        Kick => "KickGroupMember",
        Ban => "BanGroupMember",
        Unban => "UnbanGroupMember",
        _ => "RespondGroupJoinRequest",
    };

    /// <summary>
    /// The missing permission for a row answered earlier, for a second press of the same key. Read
    /// off the row, with what VRChat said kept there; the account's roles are as they are now.
    /// </summary>
    private static MissingGroupPermission? MissingPermissionOf(ModerationAction row, Modbot.Core.Data.Entities.Settings settings) =>
        row.Succeeded == false
            ? VRChatGroupPermissions.Refusal(
                row.StatusCode, VRChatFailureKind.Other, OperationOf(row.Action), row.GroupId, null, settings)
                is { } missing
                ? missing with { Said = row.FailureMessage }
                : null
            : null;

    /// <summary>
    /// The sentence for the one refusal VRChat cannot word for a moderator: a 404.
    /// </summary>
    /// <remarks>
    /// VRChat answers a kick of somebody who is not in the group, and an unban of somebody who is
    /// not banned, with a bare 404, which reaches the screen as "VRChat returned 404 for
    /// groups.moderate/…" — text that reads like a fault in Modbot rather than the plain fact that
    /// there was nothing to undo. What each 404 means is different for each action, which is the
    /// whole reason the three are not treated alike here:
    /// <list type="bullet">
    /// <item>a kick 404 means they are not in the group;</item>
    /// <item>an unban 404 means no ban stands against them;</item>
    /// <item>a ban 404 means VRChat could not find the person at all, since the group is the same
    /// one every other call reaches — so the id is the thing to doubt.</item>
    /// </list>
    /// Nothing else is rewritten. Every other refusal is VRChat's own words, because a sentence
    /// Modbot made up over a refusal it does not understand is how "I thought I banned them" starts.
    /// </remarks>
    private static string? PlainRefusal(string action, int statusCode) => statusCode != 404 ? null : action switch
    {
        Kick => "VRChat says they are not in the group.",
        Unban => "VRChat says they are not banned.",
        Ban => "VRChat has no account with that id.",
        _ => null,
    };

    // ── What Modbot records and stores once VRChat has said yes ─────────────────────────────

    /// <summary>
    /// The fact, and the change to what Modbot stores, in one transaction. Returns the fact's id,
    /// so a ban's case file can cite the ban it is the write-up of.
    /// </summary>
    private async Task<long> RecordSuccessAsync(
        ModerationAction row,
        string action,
        string userId,
        string groupId,
        Caller caller,
        IReadOnlyList<BanReason> reasons,
        string note,
        DateTimeOffset now,
        Guid? liftsCaseId,
        CancellationToken ct)
    {
        var type = action switch
        {
            Kick => FactType.ActionKick,
            Ban => FactType.ActionBan,
            Unban => FactType.ActionUnban,
            Approve => FactType.ActionJoinRequestApproved,
            Reject => FactType.ActionJoinRequestRejected,
            _ => throw new ModerationRefused(400, $"'{action}' is not something Modbot can do."),
        };

        var data = Payload(row, action, caller, reasons, note, groupId);

        // An unban names the case file whose ban it lifts. The reasons above are then the answer to
        // "why was this ban lifted", and the overturn rate is read from exactly this (M4 §9).
        if (liftsCaseId is { } caseId)
            data["caseId"] = caseId.ToString();

        await _partitions.EnsureForAsync(now, ct);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var written = await _facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = userId,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = caller.UserId.ToString(),
                Source = FactSource.Manual,
                Data = data,
            },
            ct);

        await ApplyToStoredStateAsync(action, userId, groupId, now, ct);

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return written.Id;
    }

    private async Task RecordFailureAsync(
        ModerationAction row,
        string action,
        string userId,
        Caller caller,
        IReadOnlyList<BanReason> reasons,
        string note,
        string? message,
        int statusCode,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var data = Payload(row, action, caller, reasons, note, row.GroupId);
        data["failed"] = true;
        data["statusCode"] = statusCode;
        data["vrchatSaid"] = message;
        data["description"] =
            $"{caller.Username} tried to {action} this person and VRChat refused: {message}";

        await _partitions.EnsureForAsync(now, ct);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.ActionFailed,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = userId,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = caller.UserId.ToString(),
                Source = FactSource.Manual,
                Data = data,
            },
            ct);

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Brings <c>group_member</c> and <c>group_ban</c> in line with what just happened, so the
    /// Members and Bans pages are right on the next load rather than on the next sweep.
    /// </summary>
    /// <remarks>
    /// Deliberately the same marks the sweeps use — <c>LeftAt</c> and <c>LiftedAt</c> — and nothing
    /// else. A sweep that lists the person again clears the mark by its own ordinary rules, which
    /// is what "let the sweep confirm it" means: if VRChat did not really do it, the tables go back
    /// to the truth without anybody intervening.
    /// </remarks>
    private async Task ApplyToStoredStateAsync(
        string action, string userId, string groupId, DateTimeOffset now, CancellationToken ct)
    {
        var member = await _db.GroupMembers
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, ct);

        var ban = await _db.GroupBans
            .FirstOrDefaultAsync(b => b.GroupId == groupId && b.UserId == userId, ct);

        switch (action)
        {
            case Kick:
                if (member is not null) member.LeftAt ??= now;
                break;

            case Ban:
                // A ban removes the membership too, which is why the member row is marked as well.
                if (member is not null) member.LeftAt ??= now;

                if (ban is null)
                {
                    _db.GroupBans.Add(new GroupBan
                    {
                        GroupId = groupId,
                        UserId = userId,
                        BannedAt = now,
                        FirstSeenAt = now,
                        LastSeenAt = now,
                    });
                }
                else
                {
                    ban.LiftedAt = null;
                    ban.BannedAt ??= now;
                    ban.LastSeenAt = now;
                }

                break;

            case Unban:
                if (ban is not null) ban.LiftedAt ??= now;
                break;

            // Approve and Reject deliberately change nothing. Neither table is what the Requests
            // screen reads -- that list comes from VRChat every time it is opened -- so there is
            // no stale page to patch up, and the member sweep lists an approved person on its
            // next pass exactly as it lists anybody else who joined (join requests design §3).
        }
    }

    /// <summary>
    /// Records the banned person as somebody Modbot knows about, and asks for their profile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ban can name somebody Modbot has never seen — a moderator acting on a warning from
    /// another group or a Discord message has an id and nothing else. Without this the Bans page
    /// would show that ban as a bare id until the ban sweep came round, up to half an hour later,
    /// and the People page would not list them at all.
    /// </para>
    /// <para>
    /// What is recorded is the id and that Modbot has now seen it; the display name, the picture
    /// and the rest arrive when the profile sync gets to the request, exactly as they do for
    /// anybody the ban sweep discovers. Nothing is fetched here — the write-up must never wait on
    /// VRChat (evidence design §12.2).
    /// </para>
    /// <para>
    /// Separate from the case file, which asks for the same refresh, because the case file is
    /// allowed to fail and this is not the thing that should disappear with it. A failure here
    /// disappears entirely: the ban has happened, and telling the moderator it did not because
    /// Modbot could not write down who they were would be the lie this whole service avoids.
    /// </para>
    /// </remarks>
    private async Task RememberPersonAsync(string userId, CancellationToken ct)
    {
        if (_profiles is null)
            return;

        try
        {
            await _profiles.RequestRefreshAsync(userId, RefreshReason.SeenInFactLog, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Deliberately swallowed. See the remarks above.
        }
    }

    /// <summary>
    /// Writes the ban's case file, or updates the one that already covers it.
    /// </summary>
    /// <remarks>
    /// A ban performed here has a fact id and a time of its own, so the case file cites them
    /// directly instead of waiting for the audit log to publish the ban and then guessing which
    /// ban it belongs to. Returns null when the host has no case-file writer, or when the write-up
    /// itself failed: that is a missing write-up, not a ban that did not happen, and the unwritten
    /// list is exactly the pipe that surfaces it.
    /// </remarks>
    private async Task<Guid?> WriteCaseFileAsync(
        string userId,
        long banFactId,
        DateTimeOffset bannedAt,
        IReadOnlyList<BanReason> reasons,
        string note,
        Caller caller,
        CancellationToken ct)
    {
        if (_cases is null)
            return null;

        var request = new CreateCaseFileRequest(userId, null, reasons.Select(r => r.Id).ToList(), note);

        try
        {
            var created = await _cases.CreateForActionAsync(request, caller, banFactId, bannedAt, ct);
            return created.Case.Id;
        }
        catch (CaseFileRefused refused) when (refused.ExistingCaseId is { } existing)
        {
            // Two bans of the same person in the same instant. Rare enough to be nearly
            // theoretical, and the honest answer is to put this ban's reasons on the case file
            // that already covers it rather than to write a second answer to one question.
            var updated = await _cases.UpdateAsync(
                existing, new UpdateCaseFileRequest(request.ReasonIds, request.WrittenReason), caller, ct);

            return updated.Id;
        }
        catch (CaseFileRefused)
        {
            return null;
        }
    }

    // ── Claiming the key ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes the key, or reports that somebody already has. The unique index does the deciding, not
    /// a read-then-write, because two clicks arrive at once often enough to matter.
    /// </summary>
    private async Task<(ModerationAction Row, bool AlreadyRan)> ClaimAsync(
        string action,
        string key,
        string userId,
        string groupId,
        Caller caller,
        IReadOnlyList<BanReason> reasons,
        string note,
        CancellationToken ct)
    {
        var row = new ModerationAction
        {
            Key = key,
            Action = action,
            UserId = userId,
            GroupId = groupId,
            ModeratorUserId = caller.UserId,
            ModeratorUsername = caller.Username,
            ReasonIds = JsonSerializer.Serialize(reasons.Select(r => r.Id).ToArray()),
            Note = note,
            StartedAt = _clock.UtcNow,
        };

        _db.ModerationActions.Add(row);

        try
        {
            await _db.SaveChangesAsync(ct);
            return (row, false);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _db.Entry(row).State = EntityState.Detached;

            var first = await _db.ModerationActions.AsNoTracking().FirstOrDefaultAsync(a => a.Key == key, ct)
                ?? throw new ModerationRefused(409, "That action has already been sent.");

            return (first, true);
        }
    }

    // ── Reasons ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The reasons picked, checked against the group's list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Friction scales with reversibility (foundation §5.8.1): a ban always needs a reason, because
    /// a ban with no classification is the case nobody can answer later. A kick, an unban or a
    /// rejected join request needs one only where the group has asked for it. Approving somebody
    /// never does, and takes none: there is no list of reasons to let a person in.
    /// </para>
    /// <para>
    /// Each reason is offered only on the actions it says (<see cref="ReasonUse"/>), and one sent
    /// for any other action is refused: "Harassment" is no answer to why a ban was lifted, and a
    /// client that sent it anyway would put exactly that classification in the record (M4 §9).
    /// Switched-off reasons are refused on a new action — they stay on old case files, they do not
    /// come back onto new ones.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<BanReason>> ReasonsAsync(
        string action, IReadOnlyList<Guid>? picked, string note, bool groupRequiresOne, CancellationToken ct)
    {
        var ids = (picked ?? []).Distinct().ToList();
        var use = ReasonUses.ForAction(action);
        var required = action == Ban || (groupRequiresOne && use != ReasonUse.None);

        if (ids.Count == 0)
        {
            if (required)
                throw new ModerationRefused(400, "Pick at least one reason.");

            return [];
        }

        if (use == ReasonUse.None)
            throw new ModerationRefused(400, "This action takes no reasons.");

        var all = await BanReasonList.AllAsync(_db, _clock, ct);
        var byId = all.ToDictionary(r => r.Id);
        var chosen = new List<BanReason>(ids.Count);

        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var reason))
                throw new ModerationRefused(400, "One of those reasons is not on the list.");

            if (!reason.IsActive)
                throw new ModerationRefused(400, $"\"{reason.Label}\" has been switched off.");

            if (!reason.UsedFor.HasFlag(use))
                throw new ModerationRefused(400, $"\"{reason.Label}\" is not a reason for {Noun(action)}.");

            chosen.Add(reason);
        }

        // "Other" and its like say nothing on their own, so the list marks them as needing the
        // written reason, on every action that offers them. Checked here, before anything is sent,
        // rather than left to the case file, which is written after the ban has already happened
        // -- a ban that went through and then quietly failed to be written up is the outcome the
        // whole feature exists to avoid.
        if (note.Length == 0 && chosen.FirstOrDefault(r => r.NeedsWrittenReason) is { } needs)
            throw new ModerationRefused(400, $"\"{needs.Label}\" needs a note saying what happened.");

        return chosen;
    }

    private static string Noun(string action) => action switch
    {
        Kick => "a kick",
        Ban => "a ban",
        Unban => "an unban",
        Reject => "turning a join request down",
        _ => action,
    };

    // ── Shaping ────────────────────────────────────────────────────────────────────────────

    private static JsonObject Payload(
        ModerationAction row,
        string action,
        Caller caller,
        IReadOnlyList<BanReason> reasons,
        string note,
        string groupId)
        => new()
        {
            ["action"] = action,
            ["actionId"] = row.Id.ToString(),
            ["groupId"] = groupId,
            ["actorDisplayName"] = caller.Username,
            ["reasonIds"] = new JsonArray(reasons.Select(r => (JsonNode?)r.Id.ToString()).ToArray()),
            ["reasonLabels"] = new JsonArray(reasons.Select(r => (JsonNode?)r.Label).ToArray()),
            ["note"] = note.Length == 0 ? null : note,
            ["description"] = Describe(action, caller.Username, reasons),
        };

    private static string Describe(string action, string username, IReadOnlyList<BanReason> reasons)
    {
        var verb = action switch
        {
            Kick => "kicked from the group",
            Ban => "banned from the group",
            Unban => "unbanned",
            Approve => "let into the group",
            Reject => "turned down for the group",
            _ => action,
        };

        var why = reasons.Count == 0 ? string.Empty : $": {string.Join(", ", reasons.Select(r => r.Label))}";

        return $"{verb} by {username} from Modbot{why}";
    }

    private static ModerationActionResult Describe(
        ModerationAction row, bool repeat, MissingGroupPermission? missing, string? caseFileError = null)
    {
        // Three states, not two. A key claimed a moment ago whose call has not come back yet is
        // neither done nor refused, and telling a moderator "VRChat refused it" when it is still
        // going would be the same lie in the other direction.
        var error = row.Succeeded switch
        {
            true => null,
            false => row.FailureMessage,
            null => "That action is still going.",
        };

        return new ModerationActionResult(
            row.Action,
            row.UserId,
            row.Succeeded == true,
            row.FinishedAt ?? row.StartedAt,
            row.CaseFileId,
            error,
            row.RateLimited,
            repeat,
            // Read off the row rather than held in a column of its own, so a second press of the
            // same key is told the same thing the first one was.
            Gone: row.Succeeded == false && row.StatusCode == 404,
            MissingGroupPermission: missing,
            DiscordDone: row.DiscordDone,
            DiscordError: row.DiscordError,
            // Said once, to the press that hit it: there is no column for it, and a second press
            // of the same key finds no case id on the row and says nothing about a case file.
            CaseFileError: caseFileError);
    }
}
