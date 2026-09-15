using System.Text.Json;
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
/// Sweeps the managed group's member list, one page per pass, and records what changed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One page per pass.</strong> The hosted service calls <see cref="RunOnceAsync"/> once
/// per tick, and each call reads one page and saves once -- the rows, the sightings, and the
/// cursor on the settings row together. A restart resumes from the cursor rather than from the
/// front (spec 4.2.4), and a 5,000-member sweep is fifty short transactions rather than one long
/// one. The sweep ends at the first empty page (audit-log research §7.1: no offset cap), and
/// that pass does the end-of-sweep work: marking rows not seen this sweep as left, writing the
/// first-ever sweep's snapshot, and settling changes that were waiting for the audit log.
/// </para>
/// <para>
/// <strong>Facts are inferred, and the audit log records first.</strong> Everything this class
/// notices -- a join, a leave, a role change -- goes onto the row as a waiting change and is
/// written by <see cref="ListDiffFacts"/> only after the audit log has polled past the moment it
/// was noticed and turned out not to have the event. The first sweep writes no joins at all; it
/// writes one snapshot fact with the headcount (member and ban sync design §4).
/// </para>
/// <para>
/// <strong>Nothing here retries a 429</strong> (spec 4.3.1). A rate-limited page leaves the
/// cursor where it is and reports the outcome; the service waits out the cold stop, and the next
/// pass reads the same page. The overlap between pages is what makes stopping mid-sweep safe.
/// </para>
/// <para>
/// The list never includes the account doing the asking (VRChat documents this), so the bot's
/// own membership is never a row here and is never marked as left.
/// </para>
/// </remarks>
public sealed class GroupMemberSync
{
    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly VRChatUserProfiles _profiles;
    private readonly ListDiffFacts _diff;
    private readonly IModbotClock _clock;
    private readonly GroupMemberSyncOptions _options;
    private readonly ILogger _log;

    public GroupMemberSync(
        IVRChatGate gate,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        ModbotContext db,
        VRChatUserProfiles profiles,
        IModbotClock clock,
        GroupMemberSyncOptions? options = null,
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
        _options = (options ?? new GroupMemberSyncOptions()).Clamped();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
        _diff = new ListDiffFacts(db, facts, partitions, clock, _log);
    }

    public async Task<SweepRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        // Presence only, never shape: VRChat ids follow no structure (spec 3.1.1).
        if (string.IsNullOrWhiteSpace(settings.ManagedGroupId))
            return new SweepRunResult(SyncOutcome.NotConfigured, Message: "no managed group configured");

        var groupId = settings.ManagedGroupId;
        var now = _clock.UtcNow;
        var started = false;

        if (settings.MemberSweepStartedAt is null)
        {
            // Between sweeps. Checked here as well as in the service so that a process restarting
            // in a loop cannot start a fresh sweep on every boot -- the rest is a property of the
            // stored cursor, not of the service's memory.
            if (settings.MemberSweepCompletedAt is { } done && done + _options.RestBetweenSweeps > now)
            {
                return await RecordPollAsync(
                    settings,
                    new SweepRunResult(SyncOutcome.Quiet, RestUntil: done + _options.RestBetweenSweeps, Message: "resting between sweeps"),
                    ct).ConfigureAwait(false);
            }

            settings.MemberSweepStartedAt = now;
            settings.MemberSweepOffset = 0;
            settings.MemberSweepSeenSoFar = 0;
            started = true;
        }

        var offset = settings.MemberSweepOffset;

        // groups.members has spec 4.2's budget (one request per 2 seconds), resource-scoped on
        // the group because VRChat's limits are sometimes per-resource (spec 4.3.1).
        var endpoint = new VRChatEndpoint(VRChatEndpointClass.GroupsMembers, groupId, "GetGroupMembers");

        var result = await _gate.ExecuteAsync(
            endpoint,
            (client, token) => client.Groups.GetGroupMembersWithHttpInfoAsync(
                groupId, n: _options.PageSize, offset: offset, cancellationToken: token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        if (!result.Success)
            return await FailedAsync(settings, result, started, ct).ConfigureAwait(false);

        var page = result.Value ?? [];

        if (page.Count == 0)
            return await FinishSweepAsync(settings, groupId, now, started, ct).ConfigureAwait(false);

        var raw = RawEntries(result.RawResponse);
        var (rowsChanged, waiting) = await UpsertPageAsync(settings, groupId, page, raw, now, ct).ConfigureAwait(false);

        // Step back by the overlap so a departure between two pages cannot hide anyone -- but
        // always forward by at least one, so a page of nothing but overlap cannot stall.
        var step = page.Count >= _options.PageSize
            ? Math.Max(1, page.Count - _options.PageOverlap)
            : page.Count;

        settings.MemberSweepOffset = offset + step;
        settings.MemberSweepSeenSoFar += page.Count;

        return await RecordPollAsync(
            settings,
            new SweepRunResult(
                rowsChanged > 0 || waiting > 0 ? SyncOutcome.Produced : SyncOutcome.Quiet,
                PagesRead: 1,
                RowsRead: page.Count,
                RowsChanged: rowsChanged,
                FactsWaiting: waiting,
                SweepStarted: started,
                Offset: settings.MemberSweepOffset),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one page: new rows, changed rows, the changes that are now waiting, and a sighting
    /// of everyone on it so the profile sync fetches their profiles in due course.
    /// </summary>
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

        var existing = await _db.GroupMembers
            .Where(m => m.GroupId == groupId && ids.Contains(m.UserId))
            .ToDictionaryAsync(m => m.UserId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var firstSweep = settings.MemberSweepCompletedAt is null;
        var rowsChanged = 0;
        var waiting = 0;
        var sightings = new List<UserSighting>(entries.Count);

        foreach (var member in entries)
        {
            raw.TryGetValue(member.UserId, out var entry);

            var roles = RolesJson(member.RoleIds);
            var joinedAt = member.JoinedAt is { } joined && joined != default
                ? AuditLogEntryMapper.ReadTimestamp(joined)
                : (DateTimeOffset?)null;

            var status = Text(entry, "membershipStatus") ?? member.MembershipStatus.ToString().ToLowerInvariant();
            var visibility = Text(entry, "visibility") ?? Blank(member.Visibility);
            var notes = Blank(member.ManagerNotes);
            var rawJson = entry?.ToJsonString() ?? member.ToJson();

            // The last thing VRChat says this person did is join, and that is the sighting --
            // not "now". Stamping 5,000 people as seen this minute on every sweep would push
            // them all to the front of the profile queue and drown the people who are actually
            // active (user profile sync design §3.2). A genuinely new member's joinedAt is recent
            // and lands in the recent window on its own.
            sightings.Add(new UserSighting(member.UserId, joinedAt ?? now, RefreshReason.SeenInFactLog));

            if (!existing.TryGetValue(member.UserId, out var row))
            {
                row = new GroupMember
                {
                    GroupId = groupId,
                    UserId = member.UserId,
                    FirstSeenAt = now,
                };

                _db.GroupMembers.Add(row);
                rowsChanged++;

                if (!firstSweep)
                {
                    // Listed for the first time. New, unless VRChat dates the join before the
                    // last sweep started -- then the last sweep should have listed them and did
                    // not, which is a miss on Modbot's side, not a join.
                    var missed = joinedAt is { } at
                        && settings.MemberSweepPreviousStartedAt is { } previous
                        && at < previous;

                    if (missed)
                    {
                        _log.Debug(
                            "{UserId} is listed for the first time but joined {JoinedAt}, before the last sweep; treated as missed, not new",
                            member.UserId, joinedAt);
                    }
                    else
                    {
                        row.WaitingFacts = WaitingFacts.Add(row.WaitingFacts, new WaitingFact(
                            WaitingFactKind.Join, now, From: settings.MemberSweepCompletedAt, At: joinedAt));
                        waiting++;
                    }
                }
            }
            else
            {
                var changed = false;

                if (row.LeftAt is not null)
                {
                    // Back on the list. Same join date as before means VRChat says they never
                    // left: the leave was a page-boundary miss and is dropped before it is ever
                    // written. A later join date means they really left and came back.
                    var sameMembership = joinedAt is { } at && row.JoinedAt is { } before && at <= before;

                    if (sameMembership)
                    {
                        row.WaitingFacts = WaitingFacts.Without(row.WaitingFacts, WaitingFactKind.Leave);
                        _log.Debug("{UserId} is listed again with the same join date; the leave was a miss and is dropped", member.UserId);
                    }
                    else
                    {
                        row.WaitingFacts = WaitingFacts.Add(row.WaitingFacts, new WaitingFact(
                            WaitingFactKind.Join, now, From: row.LeftAt, At: joinedAt));
                        waiting++;
                    }

                    row.LeftAt = null;
                    changed = true;
                }
                else if (!string.Equals(row.Roles, roles, StringComparison.Ordinal))
                {
                    var before = RoleIds(row.Roles);
                    var after = RoleIds(roles);

                    foreach (var added in after.Except(before, StringComparer.Ordinal))
                    {
                        row.WaitingFacts = WaitingFacts.Add(row.WaitingFacts, new WaitingFact(
                            WaitingFactKind.RoleAssign, now, From: row.LastSeenAt, RoleId: added));
                        waiting++;
                    }

                    foreach (var removed in before.Except(after, StringComparer.Ordinal))
                    {
                        row.WaitingFacts = WaitingFacts.Add(row.WaitingFacts, new WaitingFact(
                            WaitingFactKind.RoleUnassign, now, From: row.LastSeenAt, RoleId: removed));
                        waiting++;
                    }
                }

                changed |= !string.Equals(row.Roles, roles, StringComparison.Ordinal)
                    || row.JoinedAt != joinedAt
                    || !string.Equals(row.MembershipStatus, status, StringComparison.Ordinal)
                    || !string.Equals(row.Visibility, visibility, StringComparison.Ordinal)
                    || row.IsRepresenting != member.IsRepresenting
                    || !string.Equals(row.ManagerNotes, notes, StringComparison.Ordinal);

                if (changed)
                    rowsChanged++;
            }

            row.MembershipId = Blank(member.Id);
            row.Roles = roles;
            row.JoinedAt = joinedAt;
            row.MembershipStatus = status;
            row.Visibility = visibility;
            row.IsRepresenting = member.IsRepresenting;
            row.ManagerNotes = notes;
            row.LastSeenAt = now;
            row.Raw = rawJson;
        }

        await _profiles.RecordSeenAsync(sightings, ct).ConfigureAwait(false);

        return (rowsChanged, waiting);
    }

    /// <summary>
    /// The empty page: the sweep is over. Anyone not listed this sweep has left; the first sweep
    /// records a headcount; and changes the audit log has had its turn on are recorded.
    /// </summary>
    private async Task<SweepRunResult> FinishSweepAsync(
        Settings settings,
        string groupId,
        DateTimeOffset now,
        bool started,
        CancellationToken ct)
    {
        var startedAt = settings.MemberSweepStartedAt!.Value;
        var firstSweep = settings.MemberSweepCompletedAt is null;

        var seen = await _db.GroupMembers
            .CountAsync(m => m.GroupId == groupId && m.LeftAt == null && m.LastSeenAt >= startedAt, ct)
            .ConfigureAwait(false);

        if (seen == 0 && settings.MemberSweepCount > 0)
        {
            // A list that went from thousands to nobody between two sweeps is far more likely to
            // be VRChat answering an empty page it should not have than a group emptying out.
            // Marking everyone as left on that evidence would write thousands of false leaves;
            // starting over costs a rest.
            _log.Warning(
                "The member sweep listed nobody, but the last full sweep listed {Count}. Nobody is marked as left; the sweep will run again",
                settings.MemberSweepCount);

            settings.MemberSweepStartedAt = null;
            settings.MemberSweepOffset = 0;
            settings.MemberSweepSeenSoFar = 0;

            return await RecordPollAsync(
                settings,
                new SweepRunResult(SyncOutcome.Failed, PagesRead: 1, SweepStarted: started,
                    Message: $"listed nobody where the last sweep listed {settings.MemberSweepCount}"),
                ct).ConfigureAwait(false);
        }

        var gone = await _db.GroupMembers
            .Where(m => m.GroupId == groupId && m.LeftAt == null && m.LastSeenAt < startedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var row in gone)
        {
            row.LeftAt = now;
            row.WaitingFacts = WaitingFacts.Add(row.WaitingFacts, new WaitingFact(
                WaitingFactKind.Leave, now, From: row.LastSeenAt));
        }

        if (firstSweep)
        {
            await _diff.WriteSnapshotAsync(
                FactType.MembersSnapshot,
                groupId,
                new JsonObject
                {
                    ["memberCount"] = seen,
                    ["sweepStartedAt"] = startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                },
                now,
                ct).ConfigureAwait(false);

            _log.Information("First member sweep finished: {Count} members listed", seen);
        }

        settings.MemberSweepPreviousStartedAt = startedAt;
        settings.MemberSweepCompletedAt = now;
        settings.MemberSweepCount = seen;
        settings.MemberSweepStartedAt = null;
        settings.MemberSweepOffset = 0;
        settings.MemberSweepSeenSoFar = 0;

        var (written, deduplicated, stillWaiting) = await SettleAsync(settings, groupId, now, ct).ConfigureAwait(false);

        if (!firstSweep)
        {
            _log.Information(
                "Member sweep finished: {Count} members, {Gone} no longer listed, {Written} changes recorded, {Deduplicated} already in the audit log, {Waiting} waiting",
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

    /// <summary>Every row with a change waiting, given its turn.</summary>
    private async Task<(int Written, int Deduplicated, int StillWaiting)> SettleAsync(
        Settings settings,
        string groupId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Rows marked gone a moment ago are tracked with their new waiting change in memory but
        // still null in the database, so this query does not return them -- correctly: a change
        // noticed this instant cannot have had the audit log's turn yet.
        var rows = await _db.GroupMembers
            .Where(m => m.GroupId == groupId && m.WaitingFacts != null)
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

        return (written, deduplicated, stillWaiting);
    }

    private async Task<SweepRunResult> FailedAsync<T>(
        Settings settings,
        VRChatResult<T> result,
        bool started,
        CancellationToken ct)
    {
        if (result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting)
        {
            // Never retried, and not logged as an error: a cold stop is the design working
            // (spec 4.3.1). The cursor stays; the next pass reads the same page.
            _log.Information(
                "Member sweep is paused: {Reason}",
                result.ErrorMessage ?? "the groups.members bucket is cold-stopped");

            return await RecordPollAsync(
                settings,
                new SweepRunResult(SyncOutcome.RateLimited, SweepStarted: started, Offset: settings.MemberSweepOffset, Message: result.ErrorMessage),
                ct).ConfigureAwait(false);
        }

        _log.Warning(
            "Member sweep could not read page at offset {Offset}: {Status} {Reason}",
            settings.MemberSweepOffset,
            result.StatusCode,
            result.ErrorMessage ?? "no detail");

        return await RecordPollAsync(
            settings,
            new SweepRunResult(SyncOutcome.Failed, SweepStarted: started, Offset: settings.MemberSweepOffset, Message: result.ErrorMessage),
            ct).ConfigureAwait(false);
    }

    /// <summary>One save per pass: the rows, the sightings' companion updates, and the cursor together (spec 4.2.4).</summary>
    private async Task<SweepRunResult> RecordPollAsync(Settings settings, SweepRunResult result, CancellationToken ct)
    {
        settings.MemberSweepPolledAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>The response body as VRChat sent it, one object per user id. Empty when there was no body.</summary>
    internal static Dictionary<string, JsonObject> RawEntries(string? body)
    {
        var entries = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(body))
            return entries;

        try
        {
            if (JsonNode.Parse(body) is not JsonArray array)
                return entries;

            foreach (var node in array)
            {
                if (node is JsonObject o && Text(o, "userId") is { } id)
                    entries[id] = o;
            }
        }
        catch (JsonException)
        {
            // Not JSON after all. The typed objects still carry everything the row needs.
        }

        return entries;
    }

    internal static string RolesJson(IEnumerable<string>? roleIds) =>
        JsonSerializer.Serialize(
            (roleIds ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList());

    /// <summary>The role ids in a row's <c>roles</c> column. Public because the API resolves them to names.</summary>
    public static List<string> RoleIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Text(JsonObject? o, string key) =>
        o is not null
        && o.TryGetPropertyValue(key, out var value)
        && value is JsonValue v
        && v.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
