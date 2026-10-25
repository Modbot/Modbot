using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.AutomaticRoles;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Moderation;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Serilog;

namespace Modbot.Api.Features.AutomaticRoles;

/// <summary>
/// One pass of the automatic roles job: for every person and every role the rules give, decides
/// (with <see cref="AutomaticRoleDecider"/>) whether to give it, take it or leave it, does it in
/// VRChat or Discord, notes it in the grants and writes it to the audit log.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Who is looked at.</strong> Every enabled account, for every role a rule gives, and every
/// person with an open grant, even when their account has since been turned off (their role is
/// taken, with the reason "Account turned off"). A half is skipped when Modbot cannot act on it:
/// no linked VRChat id, no counted Discord id, or the person is not a current member of the group
/// or the server.
/// </para>
/// <para>
/// <strong>No retry.</strong> Every call is made once. After a VRChat call fails, no more VRChat
/// calls are made in the pass (a cold stop, as with a 429); the next pass tries again. A Discord
/// call that fails is logged and the pass goes on.
/// </para>
/// <para>
/// <strong>The stored view lags.</strong> Roles are read from the stored group and server members,
/// which catch up with a change only after a while. Without a guard the job would give a role it
/// just gave, or take one it just took, on every pass until the view caught up. So a role is not
/// given while its grant is younger than <see cref="StaleViewWait"/>, and not taken again within
/// <see cref="StaleViewWait"/> of the last take.
/// </para>
/// </remarks>
public sealed class AutomaticRoleGiver
{
    /// <summary>How long the stored roles are trusted to lag behind what Modbot just did.</summary>
    public static readonly TimeSpan StaleViewWait = TimeSpan.FromMinutes(5);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ModeratorFactsGatherer _gatherer;
    private readonly AutomaticRoleGrantStore _grants;
    private readonly IVRChatModerationActions _vrchat;
    private readonly IDiscordAutomaticRoleActions _discord;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;

    // Set after a VRChat call fails; the giver lives for one pass.
    private bool _vrchatStopped;

    public AutomaticRoleGiver(
        ModbotContext db,
        IModbotClock clock,
        ModeratorFactsGatherer gatherer,
        AutomaticRoleGrantStore grants,
        IVRChatModerationActions vrchat,
        IDiscordAutomaticRoleActions discord,
        IFactWriter facts,
        EventPartitionMaintainer partitions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(gatherer);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(vrchat);
        ArgumentNullException.ThrowIfNull(discord);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _gatherer = gatherer;
        _grants = grants;
        _vrchat = vrchat;
        _discord = discord;
        _facts = facts;
        _partitions = partitions;
    }

    private readonly record struct RolePair(AutomaticRoleKind Kind, string RoleId);

    /// <summary>Runs one pass. <paramref name="modbotStartedAt"/> is when Modbot started, for the wait before it takes any role.</summary>
    public async Task RunOnceAsync(DateTimeOffset modbotStartedAt, CancellationToken ct)
    {
        _vrchatStopped = false;

        var rules = await _db.AutomaticRoles.AsNoTracking().ToListAsync(ct);
        var openGrants = await _db.AutomaticRoleGrants.AsNoTracking().Where(g => g.TakenAt == null).ToListAsync(ct);
        if (rules.Count == 0 && openGrants.Count == 0)
            return;

        var facts = rules.Count == 0 ? [] : await _gatherer.GatherAsync(rules, null, ct);
        var factsByUser = facts.ToDictionary(f => f.UserId);

        var rulesByPair = rules
            .SelectMany(r => PairsOf(r).Select(p => (Pair: p, Rule: r)))
            .ToLookup(x => x.Pair, x => x.Rule);

        var pairsByUser = new Dictionary<Guid, HashSet<RolePair>>();
        foreach (var f in facts)
            pairsByUser[f.UserId] = [.. rulesByPair.Select(g => g.Key)];
        foreach (var g in openGrants)
        {
            if (!pairsByUser.TryGetValue(g.ModbotUserId, out var set))
                pairsByUser[g.ModbotUserId] = set = [];
            set.Add(new RolePair(g.Kind, g.RoleId));
        }

        var now = _clock.UtcNow;
        var settings = await _db.GetSettingsAsync(ct);

        var userIds = pairsByUser.Keys.ToList();
        var accounts = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.VRChatUserId, u.DiscordUserId, u.DiscordVerifiedAt, u.IsDisabled, u.DeletedAt })
            .ToListAsync(ct);

        var discordIds = await StaffDiscord.CountedIdsAsync(
            _db, accounts.Select(a => (a.Id, a.DiscordUserId, a.DiscordVerifiedAt)), now, ct);
        var discordRoles = await _gatherer.DiscordRolesAsync(settings.DiscordGuildId, discordIds.Values, ct);
        var vrchatIds = accounts.Where(a => a.VRChatUserId is { Length: > 0 }).Select(a => a.VRChatUserId!).Distinct().ToList();
        var vrchatRoles = await _gatherer.VRChatRolesAsync(settings.ManagedGroupId, vrchatIds, ct);

        var openByKey = openGrants.ToDictionary(g => (g.ModbotUserId, new RolePair(g.Kind, g.RoleId)));
        var discordRoleNames = await DiscordRoleNamesAsync(settings.DiscordGuildId, pairsByUser, ct);

        foreach (var account in accounts)
        {
            factsByUser.TryGetValue(account.Id, out var userFacts);
            var turnedOff = account.IsDisabled || account.DeletedAt is not null;

            var vrchatId = account.VRChatUserId is { Length: > 0 } v ? v : null;
            List<string>? vrchatHeld = null;
            if (vrchatId is not null)
                vrchatRoles.TryGetValue(vrchatId, out vrchatHeld);

            discordIds.TryGetValue(account.Id, out var discordId);
            List<string>? discordHeld = null;
            if (discordId is not null)
                discordRoles.TryGetValue(discordId, out discordHeld);

            foreach (var pair in pairsByUser[account.Id].OrderBy(p => p.Kind).ThenBy(p => p.RoleId, StringComparer.Ordinal))
            {
                var isVRChat = pair.Kind == AutomaticRoleKind.VRChat;
                var targetId = isVRChat ? vrchatId : discordId;
                var held = isVRChat ? vrchatHeld : discordHeld;
                if (targetId is null || held is null)
                    continue;
                if (isVRChat && _vrchatStopped)
                    continue;
                if (!isVRChat && settings.DiscordGuildId is not { Length: > 0 })
                    continue;

                var rulesForPair = rulesByPair[pair].ToList();
                var applicable = userFacts is null
                    ? []
                    : rulesForPair.Where(r => AutomaticRoleRule.AppliesTo(r, userFacts)).ToList();
                var checks = userFacts is null
                    ? []
                    : applicable.Select(r => AutomaticRoleRule.Check(r, userFacts)).ToList();

                openByKey.TryGetValue((account.Id, pair), out var open);
                var holds = held.Contains(pair.RoleId);

                DateTimeOffset? lastTaken = null;
                if (checks.Any(c => c.Holds) && !holds)
                    lastTaken = await _grants.LastTakenAsync(account.Id, pair.Kind, pair.RoleId, ct);

                var decision = AutomaticRoleDecider.Decide(new RoleDecisionInput(
                    checks,
                    holds,
                    ModbotGaveIt: open is not null,
                    OnTakeBackList: applicable.Any(r => r.TakeBackUserIds.Contains(account.Id)),
                    lastTaken,
                    now,
                    modbotStartedAt));

                switch (decision.Action)
                {
                    case RoleAction.Give:
                        if (open is not null && now - open.GivenAt < StaleViewWait)
                            break;

                        var givingRule = applicable[checks.FindIndex(c => c.Holds)];
                        await GiveAsync(account.Id, pair, targetId, settings.DiscordGuildId, givingRule, decision.Reason, now, discordRoleNames, ct);
                        break;

                    case RoleAction.Take:
                        lastTaken ??= await _grants.LastTakenAsync(account.Id, pair.Kind, pair.RoleId, ct);
                        if (lastTaken is { } taken && now - taken < StaleViewWait)
                            break;

                        var reason = turnedOff ? AutomaticRoleReasons.AccountTurnedOff : decision.Reason;
                        var ruleId = open?.RuleId ?? applicable[0].Id;
                        var takingRule = rules.FirstOrDefault(r => r.Id == ruleId);
                        await TakeAsync(account.Id, pair, targetId, settings.DiscordGuildId, ruleId, takingRule, reason, now, discordRoleNames, ct);
                        break;
                }
            }
        }
    }

    private async Task GiveAsync(
        Guid userId, RolePair pair, string targetId, string? guildId, AutomaticRole rule, string reason,
        DateTimeOffset now, IReadOnlyDictionary<string, string> roleNames, CancellationToken ct)
    {
        if (!await CallAsync(pair, targetId, guildId, give: true, reason, ct))
            return;

        await _grants.OpenAsync(rule.Id, userId, pair.Kind, pair.RoleId, now, ct);
        await WriteFactAsync(FactType.AutomaticRoleGiven, userId, pair, targetId, rule.Id, rule.Name, reason, now, roleNames, ct);
    }

    private async Task TakeAsync(
        Guid userId, RolePair pair, string targetId, string? guildId, Guid ruleId, AutomaticRole? rule, string reason,
        DateTimeOffset now, IReadOnlyDictionary<string, string> roleNames, CancellationToken ct)
    {
        if (!await CallAsync(pair, targetId, guildId, give: false, reason, ct))
            return;

        await _grants.RecordTakeAsync(ruleId, userId, pair.Kind, pair.RoleId, now, reason, ct);
        await WriteFactAsync(FactType.AutomaticRoleTaken, userId, pair, targetId, ruleId, rule?.Name, reason, now, roleNames, ct);
    }

    /// <summary>
    /// Makes the one call. True when it was done. A VRChat failure stops VRChat for the rest of the
    /// pass; a Discord person who left or a role that is gone is skipped quietly, any other Discord
    /// failure is logged.
    /// </summary>
    private async Task<bool> CallAsync(RolePair pair, string targetId, string? guildId, bool give, string reason, CancellationToken ct)
    {
        var verb = give ? "give" : "take";

        if (pair.Kind == AutomaticRoleKind.VRChat)
        {
            var outcome = give
                ? await _vrchat.GiveGroupRoleAsync(targetId, pair.RoleId, ct)
                : await _vrchat.TakeGroupRoleAsync(targetId, pair.RoleId, ct);
            if (outcome.Done)
                return true;

            _vrchatStopped = true;
            Log.Warning("Automatic roles: could not {Verb} VRChat role {RoleId}: {Error}; no more VRChat calls this pass", verb, pair.RoleId, outcome.Error);
            return false;
        }

        var why = $"Modbot: automatic role - {reason}";
        var result = give
            ? await _discord.AddRoleAsync(guildId!, targetId, pair.RoleId, why, ct)
            : await _discord.RemoveRoleAsync(guildId!, targetId, pair.RoleId, why, ct);
        if (result.Done)
            return true;

        if (!result.BotOffline && !result.NotInServer && !result.RoleGone)
            Log.Warning("Automatic roles: could not {Verb} Discord role {RoleId}: {Error}", verb, pair.RoleId, result.Error);

        return false;
    }

    private static IEnumerable<RolePair> PairsOf(AutomaticRole rule)
    {
        if (rule.VRChatRoleId is { Length: > 0 } vrchat)
            yield return new RolePair(AutomaticRoleKind.VRChat, vrchat);
        if (rule.DiscordRoleId is { Length: > 0 } discord)
            yield return new RolePair(AutomaticRoleKind.Discord, discord);
    }

    private async Task<Dictionary<string, string>> DiscordRoleNamesAsync(
        string? guildId, Dictionary<Guid, HashSet<RolePair>> pairsByUser, CancellationToken ct)
    {
        var ids = pairsByUser.Values.SelectMany(s => s).Where(p => p.Kind == AutomaticRoleKind.Discord).Select(p => p.RoleId).Distinct().ToList();
        if (guildId is not { Length: > 0 } || ids.Count == 0)
            return [];

        return await _db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId && ids.Contains(r.RoleId))
            .ToDictionaryAsync(r => r.RoleId, r => r.Name, StringComparer.Ordinal, ct);
    }

    private async Task WriteFactAsync(
        string type, Guid userId, RolePair pair, string targetId, Guid ruleId, string? ruleName, string reason,
        DateTimeOffset now, IReadOnlyDictionary<string, string> roleNames, CancellationToken ct)
    {
        var data = new JsonObject
        {
            ["platform"] = pair.Kind == AutomaticRoleKind.VRChat ? "VRChat" : "Discord",
            ["roleId"] = pair.RoleId,
            ["roleName"] = pair.Kind == AutomaticRoleKind.Discord && roleNames.TryGetValue(pair.RoleId, out var name) ? name : null,
            ["ruleId"] = ruleId.ToString(),
            ["ruleName"] = ruleName,
            ["reason"] = reason,
        };
        data[pair.Kind == AutomaticRoleKind.VRChat ? "vrchatUserId" : "discordUserId"] = targetId;

        await _partitions.EnsureForAsync(now, ct);
        await _facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = userId.ToString(),
                Source = FactSource.Modbot,
                Data = data,
            },
            ct);
    }
}
