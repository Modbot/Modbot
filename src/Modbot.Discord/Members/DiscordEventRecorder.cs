using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Members;

/// <summary>
/// Turns what happens to the Discord server's members into facts (M5 spec §5): joins and leaves,
/// nickname and role changes, bans, kicks, timeouts, voice sessions, and messages a moderator
/// removed -- whether seen live, or found afterwards by comparing the member list or reading the
/// audit log.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One source for each moderation action.</strong> Only the audit log says who banned, kicked,
/// timed out or changed somebody's roles. When the bot may read it, those facts come from it and
/// nothing else, live and after a disconnect alike, so the same action is not recorded twice with and
/// without a moderator. When it may not, they come from the gateway's own events, with no actor.
/// </para>
/// <para>
/// <strong>Nothing is recorded twice.</strong> An audit entry already recorded -- its id is kept on the
/// fact -- is skipped, and so is one the gateway already recorded without an id within two minutes,
/// which happens when the permission to read the audit log was given after the event.
/// </para>
/// <para>
/// <strong>Times.</strong> Something seen live happened now. Something found by comparing lists after
/// the bot was away happened somewhere between the last moment the bot was listening and now, and
/// the fact says so with <c>occurred_before</c> rather than inventing a time (spec 5.3).
/// </para>
/// </remarks>
public sealed class DiscordEventRecorder
{
    /// <summary>How close a gateway fact must be to an audit entry to be the same action.</summary>
    public static readonly TimeSpan SameActionWindow = TimeSpan.FromMinutes(2);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IDailyTotalCounter _dailyTotals;

    public DiscordEventRecorder(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IDailyTotalCounter dailyTotals)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(dailyTotals);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _dailyTotals = dailyTotals;
    }

    // ── Live ─────────────────────────────────────────────────────────────────────────────────

    public async Task MemberJoinedAsync(string guildId, DiscordMemberSnapshot member, int? memberCount, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var row = await RowAsync(guildId, member.UserId, ct).ConfigureAwait(false);

        Apply(row, member, now);
        row.LeftAt = null;

        await WriteAsync(Fact(FactType.DiscordMemberJoined, member.UserId, now, data: Named(member)), ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await MemberCountAsync(memberCount, ct).ConfigureAwait(false);
    }

    public async Task MemberLeftAsync(string guildId, string userId, int? memberCount, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var row = await _db.DiscordMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId, ct).ConfigureAwait(false);

        var data = new JsonObject();
        if (row is not null)
        {
            data["displayName"] = row.DisplayName;
            row.LeftAt = now;
            row.VoiceChannelId = null;
            row.VoiceSince = null;
            row.UpdatedAt = now;
        }

        await WriteAsync(Fact(FactType.DiscordMemberLeft, userId, now, data: data), ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await MemberCountAsync(memberCount, ct).ConfigureAwait(false);
    }

    /// <summary>A member's nickname, roles or timeout changed.</summary>
    public async Task MemberUpdatedAsync(string guildId, DiscordMemberSnapshot member, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var row = await RowAsync(guildId, member.UserId, ct).ConfigureAwait(false);
        var auditLog = await AuditLogCoversAsync(guildId, ct).ConfigureAwait(false);

        foreach (var fact in Changes(row, member, now, before: null, auditLog))
            await WriteAsync(fact, ct).ConfigureAwait(false);

        Apply(row, member, now);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task BannedAsync(string guildId, string userId, CancellationToken ct)
        => GatewayModerationAsync(guildId, FactType.DiscordMemberBanned, userId, ct);

    public Task UnbannedAsync(string guildId, string userId, CancellationToken ct)
        => GatewayModerationAsync(guildId, FactType.DiscordMemberUnbanned, userId, ct);

    /// <summary>A ban or unban seen on the gateway. Recorded only when the audit log cannot say who did it.</summary>
    private async Task GatewayModerationAsync(string guildId, string type, string userId, CancellationToken ct)
    {
        if (await AuditLogCoversAsync(guildId, ct).ConfigureAwait(false))
            return;

        var name = await NameAsync(guildId, userId, ct).ConfigureAwait(false);
        await WriteAsync(Fact(type, userId, _clock.UtcNow, data: name is null ? null : new JsonObject { ["displayName"] = name }), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Somebody joined, left or moved between voice channels.</summary>
    public async Task VoiceChangedAsync(string guildId, string userId, string? from, string? to, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var row = await RowAsync(guildId, userId, ct).ConfigureAwait(false);

        var fact = (from, to) switch
        {
            (null, { } joined) => Fact(FactType.DiscordVoiceJoined, userId, now, data: new JsonObject { ["channelId"] = joined }),
            ({ } left, null) => Fact(FactType.DiscordVoiceLeft, userId, now, data: new JsonObject { ["channelId"] = left }),
            ({ } left, { } joined) => Fact(FactType.DiscordVoiceMoved, userId, now, data: new JsonObject { ["from"] = left, ["channelId"] = joined }),
            _ => null,
        };

        if (fact is null)
            return;

        row.VoiceChannelId = to;
        row.VoiceSince = to is null ? null : now;
        row.UpdatedAt = now;

        await WriteAsync(fact, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── Catching up ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compares the whole member list with the stored one, recording who joined, left or changed
    /// while the bot was away. The first time, it records only how many members there were.
    /// </summary>
    /// <param name="seenThrough">The last moment the bot was known to be listening, or null if never.</param>
    /// <returns>Facts written.</returns>
    public async Task<int> MembersListedAsync(
        string guildId,
        IReadOnlyList<DiscordMemberSnapshot> members,
        DateTimeOffset? seenThrough,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(members);

        var now = _clock.UtcNow;
        var since = seenThrough is { } s && s < now ? s : now;
        var before = since < now ? now : (DateTimeOffset?)null;

        var server = await _db.DiscordServers.FirstOrDefaultAsync(x => x.GuildId == guildId, ct).ConfigureAwait(false);
        var firstList = server?.MembersListedAt is null;
        var auditLog = server?.BotCanViewAuditLog ?? false;

        var rows = await _db.DiscordMembers
            .Where(m => m.GuildId == guildId)
            .ToDictionaryAsync(m => m.UserId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var facts = new List<FactRecord>();
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in members)
        {
            present.Add(member.UserId);

            if (!rows.TryGetValue(member.UserId, out var row) || row.LeftAt is not null)
            {
                if (row is null)
                {
                    row = new DiscordMember { GuildId = guildId, UserId = member.UserId, FirstSeenAt = now };
                    _db.DiscordMembers.Add(row);
                    rows[member.UserId] = row;
                }

                if (!firstList)
                {
                    // Discord says when they joined; that is exact whenever it falls in the gap.
                    var joined = member.JoinedAt is { } j && j >= since && j <= now
                        ? Fact(FactType.DiscordMemberJoined, member.UserId, j, data: Named(member))
                        : Fact(FactType.DiscordMemberJoined, member.UserId, since, before, Named(member));
                    facts.Add(joined);
                }

                row.LeftAt = null;
            }
            else
            {
                facts.AddRange(Changes(row, member, since, before, auditLog));
            }

            Apply(row, member, now);
        }

        foreach (var row in rows.Values.Where(r => r.LeftAt is null && !present.Contains(r.UserId)))
        {
            facts.Add(Fact(FactType.DiscordMemberLeft, row.UserId, since, before, new JsonObject { ["displayName"] = row.DisplayName }));
            row.LeftAt = now;
            row.VoiceChannelId = null;
            row.VoiceSince = null;
            row.UpdatedAt = now;
        }

        if (firstList)
        {
            facts.Add(new FactRecord
            {
                Type = FactType.DiscordMembersSnapshot,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = guildId,
                Source = FactSource.Discord,
                Data = new JsonObject { ["count"] = members.Count },
            });

            if (server is not null)
                server.MembersListedAt = now;
        }

        foreach (var fact in facts)
            await WriteAsync(fact, ct).ConfigureAwait(false);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await MemberCountAsync(members.Count, ct).ConfigureAwait(false);

        return facts.Count;
    }

    /// <summary>
    /// Compares who is in voice now with who was when the bot was last listening, and closes or
    /// opens sessions accordingly.
    /// </summary>
    /// <remarks>
    /// A session still open from before the gap ended somewhere inside it: the leave carries the
    /// whole gap as its window, and the time counted stops at the gap's start rather than guessing.
    /// Somebody already in voice is counted from now, not from an arrival nobody saw.
    /// </remarks>
    public async Task<int> VoiceListedAsync(
        string guildId,
        IReadOnlyList<DiscordVoiceState> states,
        DateTimeOffset? seenThrough,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(states);

        var now = _clock.UtcNow;
        var since = seenThrough is { } s && s < now ? s : now;
        var before = since < now ? now : (DateTimeOffset?)null;

        var inVoice = states.ToDictionary(v => v.UserId, v => v.ChannelId, StringComparer.Ordinal);
        var open = await _db.DiscordMembers
            .Where(m => m.GuildId == guildId && m.VoiceChannelId != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var facts = new List<FactRecord>();

        foreach (var row in open)
        {
            if (inVoice.TryGetValue(row.UserId, out var channel) && channel == row.VoiceChannelId)
                continue;

            facts.Add(Fact(FactType.DiscordVoiceLeft, row.UserId, since, before, new JsonObject { ["channelId"] = row.VoiceChannelId }));
            row.VoiceChannelId = null;
            row.VoiceSince = null;
            row.UpdatedAt = now;
        }

        var stillOpen = open.Where(r => r.VoiceChannelId is not null).Select(r => r.UserId).ToHashSet(StringComparer.Ordinal);

        foreach (var (userId, channelId) in inVoice)
        {
            if (stillOpen.Contains(userId))
                continue;

            var row = await RowAsync(guildId, userId, ct).ConfigureAwait(false);
            facts.Add(Fact(FactType.DiscordVoiceJoined, userId, now, data: new JsonObject { ["channelId"] = channelId, ["alreadyThere"] = true }));
            row.VoiceChannelId = channelId;
            row.VoiceSince = now;
            row.UpdatedAt = now;
        }

        foreach (var fact in facts)
            await WriteAsync(fact, ct).ConfigureAwait(false);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return facts.Count;
    }

    /// <summary>
    /// Records audit log entries -- live, or caught up after a disconnect -- and moves the read
    /// position past them.
    /// </summary>
    /// <returns>Facts written; entries already recorded write none.</returns>
    public async Task<int> AuditLogAsync(string guildId, DiscordAuditPage page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var written = 0;

        foreach (var entry in page.Entries)
        {
            foreach (var fact in await FactsForAsync(guildId, entry, ct).ConfigureAwait(false))
            {
                if (await AlreadyRecordedAsync(fact, entry, ct).ConfigureAwait(false))
                    continue;

                await WriteAsync(fact, ct).ConfigureAwait(false);
                written++;
            }
        }

        if (page.NewestId is not null
            && await _db.DiscordServers.FirstOrDefaultAsync(s => s.GuildId == guildId, ct).ConfigureAwait(false) is { } server)
        {
            server.AuditLogReadThrough = page.NewestId;
            server.AuditLogReadAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>Where the next audit log read starts, or null to read everything Discord keeps.</summary>
    public Task<string?> AuditLogReadThroughAsync(string guildId, CancellationToken ct)
        => _db.DiscordServers.AsNoTracking()
            .Where(s => s.GuildId == guildId)
            .Select(s => s.AuditLogReadThrough)
            .FirstOrDefaultAsync(ct);

    /// <summary>The last moment the bot was known to be listening.</summary>
    public Task<DateTimeOffset?> SeenThroughAsync(string guildId, CancellationToken ct)
        => _db.DiscordServers.AsNoTracking()
            .Where(s => s.GuildId == guildId)
            .Select(s => s.SeenThrough)
            .FirstOrDefaultAsync(ct);

    /// <summary>Notes that the bot is listening now.</summary>
    public async Task SeenAsync(string guildId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _db.DiscordServers
            .Where(s => s.GuildId == guildId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.SeenThrough, now), ct)
            .ConfigureAwait(false);
    }

    public async Task MemberCountAsync(int? count, CancellationToken ct)
    {
        if (count is { } value and >= 0)
            await _dailyTotals.SetAsync(DailyTotalMetrics.DiscordMembersCount, value, ct: ct).ConfigureAwait(false);
    }

    // ── Pieces ───────────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<FactRecord>> FactsForAsync(string guildId, DiscordAuditEntry entry, CancellationToken ct)
    {
        var target = entry.TargetId;
        if (target is null)
            return [];

        var data = new JsonObject { ["auditEntryId"] = entry.Id };

        if (entry.Reason is { } reason)
            data["reason"] = reason;

        if (entry.ActorId is { } actor && await NameAsync(guildId, actor, ct).ConfigureAwait(false) is { } actorName)
            data["actorDisplayName"] = actorName;

        async Task<JsonObject> AboutMemberAsync()
        {
            var copy = (JsonObject)data.DeepClone();
            if (await NameAsync(guildId, target, ct).ConfigureAwait(false) is { } name)
                copy["displayName"] = name;
            return copy;
        }

        JsonObject With(params (string Key, JsonNode? Value)[] more)
        {
            var copy = (JsonObject)data.DeepClone();
            foreach (var (key, value) in more)
                copy[key] = value;
            return copy;
        }

        FactRecord Make(string type, JsonObject payload) => new()
        {
            Type = type,
            OccurredAt = entry.At,
            SubjectPlatform = FactPlatform.Discord,
            SubjectId = target,
            ActorPlatform = entry.ActorId is null ? null : FactPlatform.Discord,
            ActorId = entry.ActorId,
            Source = FactSource.Discord,
            Data = payload,
        };

        switch (entry.Kind)
        {
            case DiscordAuditKinds.Ban:
                return [Make(FactType.DiscordMemberBanned, await AboutMemberAsync().ConfigureAwait(false))];
            case DiscordAuditKinds.Unban:
                return [Make(FactType.DiscordMemberUnbanned, await AboutMemberAsync().ConfigureAwait(false))];
            case DiscordAuditKinds.Kick:
                return [Make(FactType.DiscordMemberKicked, await AboutMemberAsync().ConfigureAwait(false))];

            case DiscordAuditKinds.Timeout:
            {
                var payload = await AboutMemberAsync().ConfigureAwait(false);
                payload["until"] = entry.Until;
                return [Make(FactType.DiscordMemberTimedOut, payload)];
            }

            case DiscordAuditKinds.TimeoutRemoved:
                return [Make(FactType.DiscordMemberTimeoutRemoved, await AboutMemberAsync().ConfigureAwait(false))];

            case DiscordAuditKinds.Roles:
            {
                var facts = new List<FactRecord>();
                foreach (var role in entry.Roles ?? [])
                {
                    var payload = await AboutMemberAsync().ConfigureAwait(false);
                    payload["roleId"] = role.RoleId;
                    payload["roleName"] = role.Name;
                    facts.Add(Make(role.Added ? FactType.DiscordRoleGranted : FactType.DiscordRoleRevoked, payload));
                }

                return facts;
            }

            case DiscordAuditKinds.MessagesDeleted:
            {
                var payload = await AboutMemberAsync().ConfigureAwait(false);
                payload["channelId"] = entry.ChannelId;
                payload["count"] = entry.Count;
                return [Make(FactType.DiscordMessagesRemoved, payload)];
            }

            case DiscordAuditKinds.MessagesBulkDeleted:
                return [Make(FactType.DiscordMessagesBulkRemoved, With(("count", entry.Count)))];

            case DiscordAuditKinds.ChannelCreated:
                return [Make(FactType.DiscordChannelCreated, With(("name", entry.Name)))];
            case DiscordAuditKinds.ChannelChanged:
                return [Make(FactType.DiscordChannelChanged, With(("name", entry.Name)))];
            case DiscordAuditKinds.ChannelDeleted:
                return [Make(FactType.DiscordChannelDeleted, With(("name", entry.Name)))];
            case DiscordAuditKinds.RoleCreated:
                return [Make(FactType.DiscordRoleCreated, With(("name", entry.Name)))];
            case DiscordAuditKinds.RoleChanged:
                return [Make(FactType.DiscordRoleChanged, With(("name", entry.Name)))];
            case DiscordAuditKinds.RoleDeleted:
                return [Make(FactType.DiscordRoleDeleted, With(("name", entry.Name)))];

            default:
                return [];
        }
    }

    /// <summary>
    /// Whether this audit entry, or the same action seen on the gateway without it, is already in
    /// the fact log.
    /// </summary>
    private async Task<bool> AlreadyRecordedAsync(FactRecord fact, DiscordAuditEntry entry, CancellationToken ct)
    {
        var roleId = fact.Data?["roleId"]?.GetValue<string>();

        var marker = new JsonObject { ["auditEntryId"] = entry.Id };
        if (roleId is not null)
            marker["roleId"] = roleId;

        var markerJson = marker.ToJsonString();

        if (await _db.Events.AsNoTracking()
                .AnyAsync(e => e.SubjectPlatform == FactPlatform.Discord
                               && e.Type == fact.Type
                               && EF.Functions.JsonContains(e.Data, markerJson), ct)
                .ConfigureAwait(false))
        {
            return true;
        }

        var from = fact.OccurredAt - SameActionWindow;
        var to = fact.OccurredAt + SameActionWindow;
        var sameRole = roleId is null ? null : new JsonObject { ["roleId"] = roleId }.ToJsonString();

        return await _db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.Discord
                        && e.SubjectId == fact.SubjectId
                        && e.Type == fact.Type
                        && e.OccurredAt >= from
                        && e.OccurredAt <= to
                        && !EF.Functions.JsonExists(e.Data, "auditEntryId"))
            .Where(e => sameRole == null || EF.Functions.JsonContains(e.Data, sameRole))
            .AnyAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The facts a member's change implies. Roles and timeouts only when the audit log will not
    /// record them itself.
    /// </summary>
    private static IEnumerable<FactRecord> Changes(
        DiscordMember row, DiscordMemberSnapshot member, DateTimeOffset at, DateTimeOffset? before, bool auditLog)
    {
        // A row made a moment ago for somebody never listed has nothing to compare with.
        if (row.UpdatedAt == default)
            yield break;

        if (!string.Equals(row.Nickname, member.Nickname, StringComparison.Ordinal))
        {
            yield return Fact(FactType.DiscordMemberNicknameChanged, member.UserId, at, before, new JsonObject
            {
                ["old"] = row.Nickname,
                ["new"] = member.Nickname,
                ["displayName"] = member.DisplayName,
            });
        }

        if (auditLog)
            yield break;

        var had = RoleIds(row.Roles);
        var has = member.RoleIds.ToHashSet(StringComparer.Ordinal);

        foreach (var added in has.Except(had).Order(StringComparer.Ordinal))
            yield return Fact(FactType.DiscordRoleGranted, member.UserId, at, before, new JsonObject { ["roleId"] = added, ["displayName"] = member.DisplayName });

        foreach (var removed in had.Except(has).Order(StringComparer.Ordinal))
            yield return Fact(FactType.DiscordRoleRevoked, member.UserId, at, before, new JsonObject { ["roleId"] = removed, ["displayName"] = member.DisplayName });

        if (row.TimedOutUntil != member.TimedOutUntil)
        {
            // A timeout that simply ran out clears on Discord's side with no event worth a fact.
            if (member.TimedOutUntil is { } until && until > at)
            {
                yield return Fact(FactType.DiscordMemberTimedOut, member.UserId, at, before, new JsonObject { ["until"] = until, ["displayName"] = member.DisplayName });
            }
            else if (row.TimedOutUntil is { } was && was > at)
            {
                yield return Fact(FactType.DiscordMemberTimeoutRemoved, member.UserId, at, before, new JsonObject { ["displayName"] = member.DisplayName });
            }
        }
    }

    private static void Apply(DiscordMember row, DiscordMemberSnapshot member, DateTimeOffset now)
    {
        row.Username = member.Username;
        row.DisplayName = member.DisplayName;
        row.GlobalName = member.GlobalName;
        row.Nickname = member.Nickname;
        row.AvatarUrl = member.AvatarUrl ?? row.AvatarUrl;
        row.IsBot = member.IsBot;
        row.IsPending = member.IsPending;
        row.BoostingSince = member.BoostingSince;
        row.JoinedAt = member.JoinedAt ?? row.JoinedAt;
        row.Roles = JsonSerializer.Serialize(member.RoleIds.Order(StringComparer.Ordinal).ToArray());
        row.TimedOutUntil = member.TimedOutUntil;
        row.UpdatedAt = now;
    }

    private static HashSet<string> RoleIds(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<string[]>(json) ?? []).ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private async Task<DiscordMember> RowAsync(string guildId, string userId, CancellationToken ct)
    {
        var row = _db.DiscordMembers.Local.FirstOrDefault(m => m.GuildId == guildId && m.UserId == userId)
            ?? await _db.DiscordMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId, ct).ConfigureAwait(false);

        if (row is null)
        {
            row = new DiscordMember { GuildId = guildId, UserId = userId, FirstSeenAt = _clock.UtcNow };
            _db.DiscordMembers.Add(row);
        }

        return row;
    }

    private async Task<string?> NameAsync(string guildId, string userId, CancellationToken ct)
    {
        var name = await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && m.UserId == userId)
            .Select(m => m.DisplayName)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return string.IsNullOrEmpty(name) ? null : name;
    }

    private Task<bool> AuditLogCoversAsync(string guildId, CancellationToken ct)
        => _db.DiscordServers.AsNoTracking()
            .Where(s => s.GuildId == guildId)
            .Select(s => s.BotCanViewAuditLog)
            .FirstOrDefaultAsync(ct);

    private static JsonObject Named(DiscordMemberSnapshot member) => new()
    {
        ["displayName"] = member.DisplayName,
        ["username"] = member.Username,
    };

    private static FactRecord Fact(string type, string subjectId, DateTimeOffset at, DateTimeOffset? before = null, JsonObject? data = null) => new()
    {
        Type = type,
        OccurredAt = at,
        OccurredBefore = before,
        SubjectPlatform = FactPlatform.Discord,
        SubjectId = subjectId,
        Source = FactSource.Discord,
        Data = data,
    };

    private async Task WriteAsync(FactRecord fact, CancellationToken ct)
    {
        await _partitions.EnsureForAsync(fact.OccurredAt, ct).ConfigureAwait(false);
        if (fact.OccurredBefore is { } before)
            await _partitions.EnsureForAsync(before, ct).ConfigureAwait(false);

        await _facts.WriteAsync(fact, ct).ConfigureAwait(false);
    }
}
