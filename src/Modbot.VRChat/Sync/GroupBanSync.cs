using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.Users;
using Serilog;
using VRChatGroupMember = VRChat.API.Model.GroupMember;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Sweeps the managed group's ban list, one page per pass, and records what changed.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="GroupMemberSync"/>, and the same rules: one page and one save
/// per pass, a cursor a restart resumes from, an empty page ends the sweep, the first sweep
/// writes a snapshot and no bans, and every ban or unban it infers waits for the audit log's
/// turn before it is written (member and ban sync design §4).
/// </para>
/// <para>
/// This is the table that makes the Bans page the group's ban list rather than Modbot's memory
/// of bans it watched happen. The audit log reaches back about thirty days (audit-log research
/// §8) and starts when Modbot did; the list is everyone banned now, whenever that was. The two
/// answer different questions and the page shows both.
/// </para>
/// <para>
/// <c>GET /groups/{id}/bans</c> needs a group role permission the bot account may not hold. A
/// 403 is reported as a failure with VRChat's reason and retried at the failure interval; it is
/// not a rate limit and is not treated as one.
/// </para>
/// </remarks>
public sealed class GroupBanSync
{
    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly VRChatUserProfiles _profiles;
    private readonly ListDiffFacts _diff;
    private readonly IModbotClock _clock;
    private readonly GroupBanSyncOptions _options;
    private readonly ILogger _log;

    public GroupBanSync(
        IVRChatGate gate,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        ModbotContext db,
        VRChatUserProfiles profiles,
        IModbotClock clock,
        GroupBanSyncOptions? options = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(clock);

        _gate = gate;
        _db = db;
        _profiles = profiles;
        _clock = clock;
        _options = (options ?? new GroupBanSyncOptions()).Clamped();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
        _diff = new ListDiffFacts(db, facts, partitions, clock, _log);
    }

    public async Task<SweepRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(settings.ManagedGroupId))
            return new SweepRunResult(SyncOutcome.NotConfigured, Message: "no managed group configured");

        var groupId = settings.ManagedGroupId;
        var now = _clock.UtcNow;
        var started = false;

        if (settings.BanSweepStartedAt is null)
        {
            if (settings.BanSweepCompletedAt is { } done && done + _options.RestBetweenSweeps > now)
            {
                return await RecordPollAsync(
                    settings,
                    new SweepRunResult(SyncOutcome.Quiet, RestUntil: done + _options.RestBetweenSweeps, Message: "resting between sweeps"),
                    ct).ConfigureAwait(false);
            }

            settings.BanSweepStartedAt = now;
            settings.BanSweepOffset = 0;
            settings.BanSweepSeenSoFar = 0;
            started = true;
        }

        var offset = settings.BanSweepOffset;

        // groups.bans: spec 4.2's budget, resource-scoped on the group (spec 4.3.1).
        var endpoint = new VRChatEndpoint(VRChatEndpointClass.GroupsBans, groupId, "GetGroupBans");

        var result = await _gate.ExecuteAsync(
            endpoint,
            (client, token) => client.Groups.GetGroupBansWithHttpInfoAsync(
                groupId, n: _options.PageSize, offset: offset, cancellationToken: token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        if (!result.Success)
            return await FailedAsync(settings, result, started, ct).ConfigureAwait(false);

        var page = result.Value ?? [];

        if (page.Count == 0)
            return await FinishSweepAsync(settings, groupId, now, started, ct).ConfigureAwait(false);

        var raw = GroupMemberSync.RawEntries(result.RawResponse);
        var (rowsChanged, waiting) = await UpsertPageAsync(settings, groupId, page, raw, now, ct).ConfigureAwait(false);

        var step = page.Count >= _options.PageSize
            ? Math.Max(1, page.Count - _options.PageOverlap)
            : page.Count;

        settings.BanSweepOffset = offset + step;
        settings.BanSweepSeenSoFar += page.Count;

        return await RecordPollAsync(
            settings,
            new SweepRunResult(
                rowsChanged > 0 || waiting > 0 ? SyncOutcome.Produced : SyncOutcome.Quiet,
                PagesRead: 1,
                RowsRead: page.Count,
                RowsChanged: rowsChanged,
                FactsWaiting: waiting,
                SweepStarted: started,
                Offset: settings.BanSweepOffset),
            ct).ConfigureAwait(false);
    }

    private async Task<(int RowsChanged, int Waiting)> UpsertPageAsync(
        Settings settings,
        string groupId,
        IReadOnlyList<VRChatGroupMember> page,
        IReadOnlyDictionary<string, JsonObject> raw,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var entries = page
            .Where(m => !string.IsNullOrWhiteSpace(m.UserId))
            .GroupBy(m => m.UserId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();

        var ids = entries.Select(m => m.UserId).ToArray();

        var existing = await _db.GroupBans
            .Where(b => b.GroupId == groupId && ids.Contains(b.UserId))
            .ToDictionaryAsync(b => b.UserId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var firstSweep = settings.BanSweepCompletedAt is null;
        var rowsChanged = 0;
        var waiting = 0;
        var sightings = new List<UserSighting>(entries.Count);

        foreach (var member in entries)
        {
            raw.TryGetValue(member.UserId, out var entry);

            var bannedAt = member.BannedAt is { } banned && banned != default
                ? AuditLogEntryMapper.ReadTimestamp(banned)
                : (DateTimeOffset?)null;

            var rawJson = entry?.ToJsonString() ?? member.ToJson();

            // The ban is the last thing recorded about them, and it is the sighting -- for the
            // same reason the member sweep uses the join date rather than "now".
            sightings.Add(new UserSighting(member.UserId, bannedAt ?? now, RefreshReason.SeenInFactLog));

            if (!existing.TryGetValue(member.UserId, out var row))
            {
                row = new GroupBan
                {
                    GroupId = groupId,
                    UserId = member.UserId,
                    FirstSeenAt = now,
                };

                _db.GroupBans.Add(row);
                rowsChanged++;

                if (!firstSweep)
                {
                    var missed = bannedAt is { } at
                        && settings.BanSweepPreviousStartedAt is { } previous
                        && at < previous;

                    if (missed)
                    {
                        _log.Debug(
                            "{UserId} is on the ban list for the first time but was banned {BannedAt}, before the last sweep; treated as missed, not new",
                            member.UserId, bannedAt);
                    }
                    else
                    {
                        row.WaitingFacts = WaitingFacts.Add(row.WaitingFacts, new WaitingFact(
                            WaitingFactKind.Ban, now, From: settings.BanSweepCompletedAt, At: bannedAt));
                        waiting++;
                    }
                }
            }
            else if (row.LiftedAt is not null)
            {
                // Back on the list. The same ban date means the lift was a page-boundary miss;
                // a later one means they were unbanned and banned again.
                var sameBan = bannedAt is { } at && row.BannedAt is { } before && at <= before;

                if (sameBan)
                {
                    row.WaitingFacts = WaitingFacts.Without(row.WaitingFacts, WaitingFactKind.Unban);
                    _log.Debug("{UserId} is on the ban list again with the same ban date; the lift was a miss and is dropped", member.UserId);
                }
                else
                {
                    row.WaitingFacts = WaitingFacts.Add(row.WaitingFacts, new WaitingFact(
                        WaitingFactKind.Ban, now, From: row.LiftedAt, At: bannedAt));
                    waiting++;
                }

                row.LiftedAt = null;
                rowsChanged++;
            }
            else if (row.BannedAt != bannedAt)
            {
                rowsChanged++;
            }

            row.BannedAt = bannedAt;
            row.LastSeenAt = now;
            row.Raw = rawJson;
        }

        await _profiles.RecordSeenAsync(sightings, ct).ConfigureAwait(false);

        return (rowsChanged, waiting);
    }

    private async Task<SweepRunResult> FinishSweepAsync(
        Settings settings,
        string groupId,
        DateTimeOffset now,
        bool started,
        CancellationToken ct)
    {
        var startedAt = settings.BanSweepStartedAt!.Value;
        var firstSweep = settings.BanSweepCompletedAt is null;

        var seen = await _db.GroupBans
            .CountAsync(b => b.GroupId == groupId && b.LiftedAt == null && b.LastSeenAt >= startedAt, ct)
            .ConfigureAwait(false);

        if (seen == 0 && settings.BanSweepCount > 0)
        {
            _log.Warning(
                "The ban sweep listed nobody, but the last full sweep listed {Count}. No ban is marked as lifted; the sweep will run again",
                settings.BanSweepCount);

            settings.BanSweepStartedAt = null;
            settings.BanSweepOffset = 0;
            settings.BanSweepSeenSoFar = 0;

            return await RecordPollAsync(
                settings,
                new SweepRunResult(SyncOutcome.Failed, PagesRead: 1, SweepStarted: started,
                    Message: $"listed nobody where the last sweep listed {settings.BanSweepCount}; not marking any ban as lifted"),
                ct).ConfigureAwait(false);
        }

        var gone = await _db.GroupBans
            .Where(b => b.GroupId == groupId && b.LiftedAt == null && b.LastSeenAt < startedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var row in gone)
        {
            row.LiftedAt = now;
            row.WaitingFacts = WaitingFacts.Add(row.WaitingFacts, new WaitingFact(
                WaitingFactKind.Unban, now, From: row.LastSeenAt));
        }

        if (firstSweep)
        {
            await _diff.WriteSnapshotAsync(
                FactType.BansSnapshot,
                groupId,
                new JsonObject
                {
                    ["banCount"] = seen,
                    ["sweepStartedAt"] = startedAt.ToString("O", CultureInfo.InvariantCulture),
                },
                now,
                ct).ConfigureAwait(false);

            _log.Information("First ban sweep finished: {Count} bans listed", seen);
        }

        settings.BanSweepPreviousStartedAt = startedAt;
        settings.BanSweepCompletedAt = now;
        settings.BanSweepCount = seen;
        settings.BanSweepStartedAt = null;
        settings.BanSweepOffset = 0;
        settings.BanSweepSeenSoFar = 0;

        var rows = await _db.GroupBans
            .Where(b => b.GroupId == groupId && b.WaitingFacts != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var written = 0;
        var deduplicated = 0;
        var stillWaiting = 0;

        foreach (var row in rows)
        {
            var settled = await _diff.SettleAsync(
                groupId, row.UserId, row.WaitingFacts, settings, now,
                _options.WaitForAuditLog, _options.AuditLogSilentAfter, ct).ConfigureAwait(false);

            row.WaitingFacts = settled.Remaining;
            written += settled.Written;
            deduplicated += settled.Deduplicated;
            stillWaiting += settled.StillWaiting;
        }

        if (!firstSweep)
        {
            _log.Information(
                "Ban sweep finished: {Count} bans, {Gone} lifted, {Written} changes recorded, {Deduplicated} already in the audit log, {Waiting} waiting",
                seen, gone.Count, written, deduplicated, stillWaiting + gone.Count);
        }

        return await RecordPollAsync(
            settings,
            new SweepRunResult(
                firstSweep || gone.Count > 0 || written > 0 ? SyncOutcome.Produced : SyncOutcome.Quiet,
                PagesRead: 1,
                RowsChanged: gone.Count,
                FactsWritten: written,
                FactsDeduplicated: deduplicated,
                FactsWaiting: stillWaiting + gone.Count,
                MarkedGone: gone.Count,
                SweepStarted: started,
                SweepComplete: true,
                FirstSweep: firstSweep),
            ct).ConfigureAwait(false);
    }

    private async Task<SweepRunResult> FailedAsync<T>(
        Settings settings,
        VRChatResult<T> result,
        bool started,
        CancellationToken ct)
    {
        if (result.Kind == VRChatFailureKind.RateLimited)
        {
            _log.Information(
                "Ban sweep is paused: {Reason}",
                result.ErrorMessage ?? "the groups.bans bucket is cold-stopped");

            return await RecordPollAsync(
                settings,
                new SweepRunResult(SyncOutcome.RateLimited, SweepStarted: started, Offset: settings.BanSweepOffset, Message: result.ErrorMessage),
                ct).ConfigureAwait(false);
        }

        _log.Warning(
            "Ban sweep could not read page at offset {Offset}: {Status} {Reason}",
            settings.BanSweepOffset,
            result.StatusCode,
            result.ErrorMessage ?? "no detail");

        return await RecordPollAsync(
            settings,
            new SweepRunResult(SyncOutcome.Failed, SweepStarted: started, Offset: settings.BanSweepOffset, Message: result.ErrorMessage),
            ct).ConfigureAwait(false);
    }

    private async Task<SweepRunResult> RecordPollAsync(Settings settings, SweepRunResult result, CancellationToken ct)
    {
        settings.BanSweepPolledAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return result;
    }
}
