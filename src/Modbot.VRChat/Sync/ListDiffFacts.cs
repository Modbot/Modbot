using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.VRChat.Sync;

/// <summary>The kinds of change a sweep can notice. Stored as text in <c>waiting_facts</c>.</summary>
public static class WaitingFactKind
{
    public const string Join = "join";
    public const string Leave = "leave";
    public const string RoleAssign = "role-assign";
    public const string RoleUnassign = "role-unassign";
    public const string Ban = "ban";
    public const string Unban = "unban";
}

/// <summary>
/// A change a sweep noticed and has not yet recorded as a fact.
/// </summary>
/// <param name="Kind">One of <see cref="WaitingFactKind"/>.</param>
/// <param name="NoticedAt">When the sweep saw it. The end of the window, and the moment the audit log has to have polled past.</param>
/// <param name="From">
/// The start of the window when the time is inferred: the previous sighting of the row, or the
/// previous sweep's completion. Null when VRChat stated the time.
/// </param>
/// <param name="At">The time VRChat stated -- <c>joinedAt</c>, <c>bannedAt</c> -- when it did.</param>
/// <param name="RoleId">The role, for a role change.</param>
public sealed record WaitingFact(
    string Kind,
    DateTimeOffset NoticedAt,
    DateTimeOffset? From = null,
    DateTimeOffset? At = null,
    string? RoleId = null);

/// <summary>Reads and writes the <c>waiting_facts</c> column.</summary>
public static class WaitingFacts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The waiting changes on a row. Anything unreadable resolves to none, and is logged by the caller.</summary>
    public static List<WaitingFact> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<WaitingFact>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The column value for these changes: SQL null when there are none.</summary>
    public static string? Write(IReadOnlyList<WaitingFact> facts) =>
        facts.Count == 0 ? null : JsonSerializer.Serialize(facts, Json);

    public static string? Add(string? json, WaitingFact fact)
    {
        var facts = Read(json);
        facts.Add(fact);
        return Write(facts);
    }

    /// <summary>The column without any change of this kind. For a leave that turned out not to be one.</summary>
    public static string? Without(string? json, string kind)
    {
        var facts = Read(json);
        facts.RemoveAll(f => string.Equals(f.Kind, kind, StringComparison.Ordinal));
        return Write(facts);
    }

    public static bool Has(string? json, string kind) =>
        Read(json).Any(f => string.Equals(f.Kind, kind, StringComparison.Ordinal));
}

/// <summary>What settling one row's waiting changes did.</summary>
/// <param name="Remaining">The column value afterwards: what is still waiting, or null.</param>
public sealed record SettleResult(string? Remaining, int Written, int Deduplicated, int StillWaiting);

/// <summary>
/// Turns the changes a sweep noticed into facts -- after the audit log has had its turn, and only
/// for what the audit log did not record.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The audit log is authoritative and records first.</strong> It states the actor and the
/// exact time; a list diff can state neither. When both see the same join, the audit log's fact
/// is the one that should exist, and the daily totals must not count the join twice. The audit
/// log deduplicates on VRChat's entry id, which an inferred fact does not carry, so the
/// deduplication has to happen here, on the sweep's side, before it writes (member and ban sync
/// design §4).
/// </para>
/// <para>
/// That needs the audit log to have <em>had its turn</em>: to have polled at least
/// <c>WaitForAuditLog</c> after the moment the change was noticed. Until then the change waits
/// on the row. A join noticed at 14:00 is checked on the next pass after the audit log has polled
/// past 14:02, and recorded only if no audit-log join for that person is found from around the
/// time VRChat says they joined. An audit log that is switched off, or has been silent for an
/// hour, is not waited for.
/// </para>
/// <para>
/// Waiting also fixes the one false inference offset paging makes on its own: a member skipped
/// between two pages is marked gone and reappears on the next sweep with the same join date.
/// By then the sweep knows the leave never happened and drops it before it was ever written.
/// </para>
/// </remarks>
public sealed class ListDiffFacts
{
    /// <summary>
    /// How far before the inferred time the audit log is searched. The audit entry for a join is
    /// timestamped at the join, and VRChat's <c>joinedAt</c> is the same moment, but "the same
    /// moment" across two systems deserves an hour of slack rather than none.
    /// </summary>
    public static readonly TimeSpan Slack = TimeSpan.FromHours(1);

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public ListDiffFacts(
        ModbotContext db,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IModbotClock clock,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>
    /// Whether the audit log has polled late enough that anything it was going to record about
    /// this change is already in the log.
    /// </summary>
    public static bool AuditLogHasHadItsTurn(
        Settings settings,
        WaitingFact fact,
        DateTimeOffset now,
        TimeSpan waitFor,
        TimeSpan silentAfter)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fact);

        // Never polled, or silent for a long time: not running, or given up. Waiting on it would
        // hold every inferred fact forever, and a fact log with no joins in it because the audit
        // log's bucket alerted is worse than one with inferred joins.
        if (settings.AuditLogPolledAt is not { } polledAt || now - polledAt > silentAfter)
            return true;

        return polledAt >= fact.NoticedAt + waitFor;
    }

    /// <summary>
    /// Settles one row's waiting changes: records the ones the audit log missed, drops the ones it
    /// recorded, keeps the ones it has not had its turn on.
    /// </summary>
    public async Task<SettleResult> SettleAsync(
        string groupId,
        string userId,
        string? waitingJson,
        Settings settings,
        DateTimeOffset now,
        TimeSpan waitFor,
        TimeSpan silentAfter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var waiting = WaitingFacts.Read(waitingJson);
        if (waiting.Count == 0)
            return new SettleResult(null, 0, 0, 0);

        var remaining = new List<WaitingFact>();
        var written = 0;
        var deduplicated = 0;

        foreach (var fact in waiting)
        {
            if (!AuditLogHasHadItsTurn(settings, fact, now, waitFor, silentAfter))
            {
                remaining.Add(fact);
                continue;
            }

            if (await AlreadyRecordedAsync(groupId, userId, fact, ct).ConfigureAwait(false))
            {
                deduplicated++;
                continue;
            }

            var record = Build(groupId, userId, fact);

            await _partitions.EnsureForAsync(record.OccurredAt, ct).ConfigureAwait(false);
            await _facts.WriteAsync(record, ct).ConfigureAwait(false);
            written++;
        }

        return new SettleResult(WaitingFacts.Write(remaining), written, deduplicated, remaining.Count);
    }

    /// <summary>
    /// The first sweep's one fact: a headcount with a date, about the group. Not a join per member,
    /// which would record thousands of arrivals on the day Modbot was installed.
    /// </summary>
    public async Task WriteSnapshotAsync(string type, string groupId, JsonObject data, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(data);

        data["source"] = "list-diff";

        var fact = new FactRecord
        {
            Type = type,
            OccurredAt = now,
            OccurredBefore = null,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = groupId,
            ActorPlatform = null,
            ActorId = null,
            Source = FactSource.SyncDiff,
            Data = data,
        };

        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);
        await _facts.WriteAsync(fact, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the audit log already holds this event: a fact of a matching type, about this
    /// person, from around the inferred time onwards.
    /// </summary>
    /// <remarks>
    /// A leave is also matched by an audit-log removal or ban, and by a row on the ban list from
    /// the same window: somebody who vanished from the member list because they were banned did
    /// not leave, and the ban is the fact that should exist. Role changes are matched on the role
    /// id the audit entry carries, so two different roles assigned in one window are two facts.
    /// </remarks>
    private async Task<bool> AlreadyRecordedAsync(string groupId, string userId, WaitingFact fact, CancellationToken ct)
    {
        var lower = (fact.At ?? fact.From ?? fact.NoticedAt) - Slack;
        var types = TypesFor(fact.Kind);

        if (types.Length == 0)
            return false;

        if (fact.Kind == WaitingFactKind.Leave)
        {
            var banned = await _db.GroupBans.AsNoTracking()
                .AnyAsync(b => b.GroupId == groupId
                    && b.UserId == userId
                    && ((b.BannedAt != null && b.BannedAt >= lower) || b.FirstSeenAt >= lower), ct)
                .ConfigureAwait(false);

            if (banned)
                return true;
        }

        var rows = await _db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat
                && e.SubjectId == userId
                && e.Source == FactSource.AuditLog
                && types.Contains(e.Type)
                && e.OccurredAt >= lower)
            .Select(e => new { e.Type, e.Data })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (fact.RoleId is null)
            return rows.Count > 0;

        foreach (var row in rows)
        {
            var roleId = ReadRoleId(row.Data);
            if (roleId is null || string.Equals(roleId, fact.RoleId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string[] TypesFor(string kind) => kind switch
    {
        WaitingFactKind.Join => [FactType.MemberJoined],
        WaitingFactKind.Leave => [FactType.MemberLeft, FactType.MemberKicked, FactType.MemberBanned],
        WaitingFactKind.RoleAssign => [FactType.RoleGranted],
        WaitingFactKind.RoleUnassign => [FactType.RoleRevoked],
        WaitingFactKind.Ban => [FactType.MemberBanned],
        WaitingFactKind.Unban => [FactType.MemberUnbanned],
        _ => [],
    };

    private static string? ReadRoleId(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return null;

        try
        {
            return JsonNode.Parse(data) is JsonObject o
                && o.TryGetPropertyValue("roleId", out var id)
                && id is JsonValue value
                && value.TryGetValue<string>(out var text)
                ? text
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The fact for one inferred change. Exact when VRChat stated the time, a window when it did
    /// not (spec 5.3), no actor ever -- a list cannot say who did it -- and marked as inferred
    /// from a list diff so a reader can weigh it against an audit-log fact.
    /// </summary>
    private FactRecord Build(string groupId, string userId, WaitingFact fact)
    {
        var noticedAt = fact.NoticedAt;

        DateTimeOffset occurredAt;
        DateTimeOffset? occurredBefore;

        if (fact.At is { } stated)
        {
            occurredAt = stated;
            occurredBefore = null;
        }
        else if (fact.From is { } from && from < noticedAt)
        {
            occurredAt = from;
            occurredBefore = noticedAt;
        }
        else
        {
            occurredAt = noticedAt;
            occurredBefore = null;
        }

        var data = new JsonObject
        {
            ["source"] = "list-diff",
            ["groupId"] = groupId,
            ["noticedAt"] = noticedAt.ToString("O", CultureInfo.InvariantCulture),
            ["timeStatedByVRChat"] = fact.At is not null,
        };

        if (fact.RoleId is not null)
            data["roleId"] = fact.RoleId;

        _log.Debug("Recording {Kind} for {UserId} from the list diff", fact.Kind, userId);

        return new FactRecord
        {
            Type = fact.Kind switch
            {
                WaitingFactKind.Join => FactType.MemberJoined,
                WaitingFactKind.Leave => FactType.MemberLeft,
                WaitingFactKind.RoleAssign => FactType.RoleGranted,
                WaitingFactKind.RoleUnassign => FactType.RoleRevoked,
                WaitingFactKind.Ban => FactType.MemberBanned,
                WaitingFactKind.Unban => FactType.MemberUnbanned,
                _ => throw new InvalidOperationException($"Unknown waiting fact kind '{fact.Kind}'."),
            },
            OccurredAt = occurredAt,
            OccurredBefore = occurredBefore,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = userId,
            ActorPlatform = null,
            ActorId = null,
            Source = FactSource.SyncDiff,
            Data = data,
        };
    }
}
