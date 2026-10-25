using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.ClipRequests;
using Modbot.Api.Features.Companion.Live;
using Modbot.Core.AutomaticRoles;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Core.Users;

namespace Modbot.Api.Features.AutomaticRoles;

/// <summary>
/// Builds the <see cref="ModeratorFacts"/> the automatic role rules read, for many accounts at once
/// and in a fixed number of queries, however many accounts and rules there are.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Who is a moderator.</strong> An enabled, not deleted account that holds Administrator or
/// any of Kick, Ban, Warn and Pair a companion (<see cref="ModeratorPermissions"/>). Every enabled
/// account gets facts, because a rule can also be for the members of chosen Discord or VRChat
/// roles, and those need not hold any of them.
/// </para>
/// <para>
/// <strong>Whose ban it is.</strong> A ban made in Modbot is a <c>modbot.action.ban</c> fact whose
/// actor is the account. A ban made in VRChat's own menu is a group audit fact whose actor is a
/// VRChat id, and counts for the account that linked that id. VRChat's record of a ban Modbot made
/// names Modbot's own VRChat account, which says nothing about a person, so it is left out (the same
/// rule <see cref="ClipRequestIntake"/> follows).
/// </para>
/// <para>
/// <strong>A ban overturned on appeal</strong> is one whose appeal was upheld, counted when the appeal
/// was decided. The appeal names the person, not the ban, so it is matched to the newest ban of that
/// person made before the appeal was logged, and counts for whoever made that ban.
/// </para>
/// <para>
/// <strong>The staff trial is passed</strong> when the account has one and it was closed. There is no
/// separate "passed" mark; closing is what the leads do when the Council has voted the person in.
/// </para>
/// </remarks>
public sealed class ModeratorFactsGatherer
{
    /// <summary>What makes an account part of the moderator team, besides Administrator.</summary>
    public const ModbotPermissions ModeratorPermissions =
        ModbotPermissions.Kick | ModbotPermissions.Ban | ModbotPermissions.Warn | ModbotPermissions.PairCompanion;

    private static readonly string[] BanTypes = [FactType.ActionBan, FactType.MemberBanned];

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly DeviceLocations _locations;
    private readonly CompanionConnections _connections;
    private readonly ClipSettingsReports _clipSettings;
    private readonly ClipRequestStore _clipRequests;

    public ModeratorFactsGatherer(
        ModbotContext db,
        IModbotClock clock,
        DeviceLocations locations,
        CompanionConnections connections,
        ClipSettingsReports clipSettings,
        ClipRequestStore clipRequests)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clipSettings);
        ArgumentNullException.ThrowIfNull(clipRequests);

        _db = db;
        _clock = clock;
        _locations = locations;
        _connections = connections;
        _clipSettings = clipSettings;
        _clipRequests = clipRequests;
    }

    /// <summary>
    /// The facts for every enabled account, or only <paramref name="onlyUsers"/> when given, looking
    /// back as far as the longest day count in <paramref name="rules"/> and at least 24 hours.
    /// </summary>
    public async Task<IReadOnlyList<ModeratorFacts>> GatherAsync(
        IReadOnlyCollection<AutomaticRole> rules, IReadOnlyCollection<Guid>? onlyUsers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var now = _clock.UtcNow;
        var lookBack = TimeSpan.FromDays(Math.Max(1, rules.Select(LongestDays).DefaultIfEmpty(0).Max()));
        var since = now - lookBack;

        var settings = await _db.GetSettingsAsync(ct);

        var enabled = _db.Users.AsNoTracking().Where(u => !u.IsDisabled && u.DeletedAt == null);
        if (onlyUsers is not null)
            enabled = enabled.Where(u => onlyUsers.Contains(u.Id));

        var accounts = await enabled
            .Select(u => new
            {
                u.Id,
                u.VRChatUserId,
                u.DiscordUserId,
                u.DiscordVerifiedAt,
                Permissions = u.Roles.Select(r => r.Role.Permissions).ToList(),
            })
            .ToListAsync(ct);

        if (accounts.Count == 0)
            return [];

        var userIds = accounts.Select(a => a.Id).ToList();

        var discordIds = await StaffDiscord.CountedIdsAsync(
            _db, accounts.Select(a => (a.Id, a.DiscordUserId, a.DiscordVerifiedAt)), now, ct);
        var discordRoles = await DiscordRolesAsync(settings.DiscordGuildId, discordIds.Values, ct);

        var vrchatIds = accounts.Where(a => a.VRChatUserId is { Length: > 0 }).Select(a => a.VRChatUserId!).Distinct().ToList();
        var vrchatRoles = await VRChatRolesAsync(settings.ManagedGroupId, vrchatIds, ct);

        var passedTrial = (await _db.StaffTrials.AsNoTracking()
            .Where(t => userIds.Contains(t.AccountId) && t.ClosedAt != null)
            .Select(t => t.AccountId)
            .ToListAsync(ct)).ToHashSet();

        var devices = (await _db.CompanionDevices.AsNoTracking()
            .Where(d => userIds.Contains(d.IssuedToUserId) && d.RevokedAt == null)
            .Select(d => new { d.IssuedToUserId, d.Id })
            .ToListAsync(ct))
            .ToLookup(d => d.IssuedToUserId, d => d.Id);

        var inGroupInstance = await UsersInGroupInstanceAsync(settings.ManagedGroupId, devices, now, ct);

        var banners = BanOwners(accounts.Select(a => (a.Id, a.VRChatUserId)), settings.VRChatAccountUserId);
        var bans = await BansByAsync(banners, since, ct);
        var overturned = await OverturnedAsync(banners, since, ct);

        var missed = (await _clipRequests.MissedSinceAsync(userIds, since, ct))
            .ToLookup(m => m.ModeratorUserId, m => m.At);

        return [.. accounts.Select(account =>
        {
            var deviceIds = devices[account.Id].ToList();
            var permissions = ModbotRole.Union(account.Permissions);

            return new ModeratorFacts(
                account.Id,
                now,
                IsModerator: permissions.HasFlag(ModbotPermissions.Administrator) || (permissions & ModeratorPermissions) != 0,
                CompanionRunning: _connections.IsOpen(deviceIds, now),
                ClipsReady: _clipSettings.IsReady(deviceIds),
                InGroupInstance: inGroupInstance.Contains(account.Id),
                PassedTrial: passedTrial.Contains(account.Id),
                DiscordRoleIds: discordIds.TryGetValue(account.Id, out var discordId) && discordRoles.TryGetValue(discordId, out var held) ? held : [],
                VRChatRoleIds: account.VRChatUserId is { } vrchatId && vrchatRoles.TryGetValue(vrchatId, out var heldInGroup) ? heldInGroup : [],
                BanTimes: [.. bans[account.Id]],
                OverturnedDecidedAt: [.. overturned[account.Id]],
                MissedClipTimes: [.. missed[account.Id]]);
        })];
    }

    private static int LongestDays(AutomaticRole rule)
        => Math.Max(rule.OverturnedBansDays ?? 0, rule.MissedClipsDays ?? 0);

    private async Task<Dictionary<string, List<string>>> DiscordRolesAsync(
        string? guildId, IEnumerable<string> discordIds, CancellationToken ct)
    {
        var ids = discordIds.Distinct(StringComparer.Ordinal).ToList();
        if (guildId is not { Length: > 0 } || ids.Count == 0)
            return [];

        var members = await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && m.LeftAt == null && ids.Contains(m.UserId))
            .Select(m => new { m.UserId, m.Roles })
            .ToListAsync(ct);

        return members.ToDictionary(m => m.UserId, m => RoleList(m.Roles), StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, List<string>>> VRChatRolesAsync(
        string? groupId, IReadOnlyCollection<string> vrchatIds, CancellationToken ct)
    {
        if (groupId is not { Length: > 0 } || vrchatIds.Count == 0)
            return [];

        var members = await _db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId && m.LeftAt == null && vrchatIds.Contains(m.UserId))
            .Select(m => new { m.UserId, m.Roles })
            .ToListAsync(ct);

        return members.ToDictionary(m => m.UserId, m => RoleList(m.Roles), StringComparer.Ordinal);
    }

    private static List<string> RoleList(string json)
        => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    /// <summary>
    /// The accounts with a paired computer standing in an instance of the managed group that is
    /// still open. The device's instance is its world and number; a client that named the number
    /// alone is matched by the number.
    /// </summary>
    private async Task<HashSet<Guid>> UsersInGroupInstanceAsync(
        string? groupId, ILookup<Guid, Guid> devices, DateTimeOffset now, CancellationToken ct)
    {
        var standing = new List<(Guid User, string InstanceId, string? WorldId)>();
        foreach (var owner in devices)
        {
            foreach (var device in owner)
            {
                if (_locations.Where(device, now) is { } there)
                    standing.Add((owner.Key, there.InstanceId, there.WorldId));
            }
        }

        if (groupId is not { Length: > 0 } || standing.Count == 0)
            return [];

        var numbers = standing.Select(s => s.InstanceId).Distinct(StringComparer.Ordinal).ToList();
        var open = await _db.VRChatInstances.AsNoTracking()
            .Where(i => i.GroupId == groupId && i.ClosedAt == null && i.VRChatInstanceId != null && numbers.Contains(i.VRChatInstanceId))
            .Select(i => new { i.WorldId, Number = i.VRChatInstanceId! })
            .ToListAsync(ct);

        return [.. standing
            .Where(s => open.Any(i => string.Equals(i.Number, s.InstanceId, StringComparison.Ordinal)
                && (s.WorldId is null || string.Equals(i.WorldId, s.WorldId, StringComparison.Ordinal))))
            .Select(s => s.User)];
    }

    /// <summary>Which account a ban's actor is: a Modbot account by its id, or a VRChat id by the account that linked it.</summary>
    private sealed class BanOwnerMap
    {
        public required Dictionary<string, Guid> ByModbotId { get; init; }

        public required Dictionary<string, Guid> ByVRChatId { get; init; }

        public required string? ServiceAccountId { get; init; }

        public Guid? Of(FactPlatform? platform, string? actorId)
        {
            if (actorId is not { Length: > 0 })
                return null;

            if (platform == FactPlatform.Modbot)
                return ByModbotId.TryGetValue(actorId, out var user) ? user : null;

            if (platform == FactPlatform.VRChat && !string.Equals(actorId, ServiceAccountId, StringComparison.Ordinal))
                return ByVRChatId.TryGetValue(actorId, out var linked) ? linked : null;

            return null;
        }
    }

    private static BanOwnerMap BanOwners(IEnumerable<(Guid Id, string? VRChatUserId)> accounts, string? serviceAccountId)
    {
        var list = accounts.ToList();

        return new BanOwnerMap
        {
            ByModbotId = list.ToDictionary(a => a.Id.ToString(), a => a.Id, StringComparer.OrdinalIgnoreCase),
            ByVRChatId = list
                .Where(a => a.VRChatUserId is { Length: > 0 })
                .GroupBy(a => a.VRChatUserId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal),
            ServiceAccountId = serviceAccountId,
        };
    }

    /// <summary>When each of these accounts made each of its bans since the given moment.</summary>
    private async Task<ILookup<Guid, DateTimeOffset>> BansByAsync(BanOwnerMap owners, DateTimeOffset since, CancellationToken ct)
    {
        var modbotIds = owners.ByModbotId.Keys.ToList();
        var vrchatIds = owners.ByVRChatId.Keys.ToList();

        var facts = await _db.Events.AsNoTracking()
            .Where(e => BanTypes.Contains(e.Type)
                && e.OccurredAt >= since
                && ((e.ActorPlatform == FactPlatform.Modbot && modbotIds.Contains(e.ActorId!))
                    || (e.ActorPlatform == FactPlatform.VRChat && vrchatIds.Contains(e.ActorId!))))
            .Select(e => new { e.ActorPlatform, e.ActorId, e.OccurredAt })
            .ToListAsync(ct);

        return facts
            .Select(f => (User: owners.Of(f.ActorPlatform, f.ActorId), At: f.OccurredAt))
            .Where(f => f.User is not null)
            .ToLookup(f => f.User!.Value, f => f.At);
    }

    /// <summary>
    /// For each account, when each of its bans that was overturned on appeal was decided, for the
    /// appeals decided since the given moment.
    /// </summary>
    private async Task<ILookup<Guid, DateTimeOffset>> OverturnedAsync(BanOwnerMap owners, DateTimeOffset since, CancellationToken ct)
    {
        var appeals = await _db.Appeals.AsNoTracking()
            .Where(a => a.Outcome == AppealOutcome.Upheld && a.DecidedAt != null && a.DecidedAt >= since)
            .Select(a => new { a.UserId, a.LoggedAt, DecidedAt = a.DecidedAt!.Value })
            .ToListAsync(ct);

        if (appeals.Count == 0)
            return Array.Empty<(Guid, DateTimeOffset)>().ToLookup(x => x.Item1, x => x.Item2);

        var people = appeals.Select(a => a.UserId).Distinct(StringComparer.Ordinal).ToList();
        var latest = appeals.Max(a => a.LoggedAt);

        // Every ban of those people made before the newest appeal, whoever made it: the ban an
        // appeal is about is the newest one before it, and that may be a colleague's.
        var bansOfThem = await _db.Events.AsNoTracking()
            .Where(e => BanTypes.Contains(e.Type) && e.OccurredAt <= latest && people.Contains(e.SubjectId))
            .Select(e => new { e.SubjectId, e.ActorPlatform, e.ActorId, e.OccurredAt })
            .ToListAsync(ct);

        var byPerson = bansOfThem
            .Where(b => b.ActorPlatform == FactPlatform.Modbot
                || (b.ActorPlatform == FactPlatform.VRChat && !string.Equals(b.ActorId, owners.ServiceAccountId, StringComparison.Ordinal)))
            .ToLookup(b => b.SubjectId, StringComparer.Ordinal);

        return appeals
            .Select(a => (
                Ban: byPerson[a.UserId].Where(b => b.OccurredAt <= a.LoggedAt).OrderByDescending(b => b.OccurredAt).FirstOrDefault(),
                a.DecidedAt))
            .Where(x => x.Ban is not null)
            .Select(x => (User: owners.Of(x.Ban!.ActorPlatform, x.Ban.ActorId), x.DecidedAt))
            .Where(x => x.User is not null)
            .ToLookup(x => x.User!.Value, x => x.DecidedAt);
    }
}
