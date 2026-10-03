using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Giveaways;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Giveaways;
using Modbot.Core.Time;

namespace Modbot.Analytics.Lists;

/// <summary>What a list role does for one person.</summary>
public static class ListRoleChangeKinds
{
    public const string Give = "give";
    public const string Take = "take";
}

/// <summary>One role a pass would give or take.</summary>
/// <param name="What">One of <see cref="ListRoleChangeKinds"/>.</param>
/// <param name="Name">Their name as the list or the member list shows it.</param>
/// <param name="Holds">
/// For a take: the stored member row shows the role. When it does not, the removal is still sent,
/// but Discord changes nothing it can report, so no fact says a role was taken.
/// </param>
public sealed record ListRoleChange(string What, string DiscordUserId, string? VRChatUserId, string? Name, bool Holds = true);

/// <summary>What one list role would do right now (roles from lists design §5).</summary>
/// <param name="ListRoleId">The saved pairing, or null for one not saved yet.</param>
/// <param name="Changes">Roles to give, then roles to take away. Every one, not a page.</param>
/// <param name="Forget">
/// Discord accounts whose given-row goes with nothing sent: Modbot saw them leave the server, which
/// took the role, or they left and came back between two passes (and are in
/// <paramref name="Changes"/> to be given it again). Never because the stored member row lacks the
/// role: that is a reason to look again, not proof (design §3).
/// </param>
/// <param name="Leaving">How many of <paramref name="Forget"/> Modbot saw leave the server.</param>
/// <param name="AlreadyHave">In the list, in the server, and holding the role.</param>
/// <param name="TakenByHand">
/// In the list; Modbot gave them the role and the member list does not show it now. Left alone:
/// somebody took it off by hand, or the update has not arrived.
/// </param>
/// <param name="NoLinkedDiscord">In the list by their VRChat account, with no linked Discord account.</param>
/// <param name="NotInServer">In the list with a Discord account that is not in the server.</param>
/// <param name="Holders">Everybody in the server holding the role now, however they got it.</param>
/// <param name="Members">Everybody in the server, bots left out, for the give brake.</param>
/// <param name="TakesHeld">Roles this plan would have taken away and is not taking, because of <paramref name="HeldBecause"/>.</param>
/// <param name="HeldBecause">Why nothing is taken away this time though other changes go ahead, or null.</param>
/// <param name="Problem">Why nothing can be done for it, as a sentence. Everything else is empty when set.</param>
public sealed record ListRolePlan(
    Guid? ListRoleId,
    Guid ListId,
    string ListName,
    string DiscordRoleId,
    string? RoleName,
    IReadOnlyList<ListRoleChange> Changes,
    IReadOnlyList<string> Forget,
    int Leaving,
    int AlreadyHave,
    int TakenByHand,
    int NoLinkedDiscord,
    int NotInServer,
    int Holders,
    int Members,
    int TakesHeld,
    string? HeldBecause,
    string? Problem)
{
    public int Giving => Changes.Count(c => c.What == ListRoleChangeKinds.Give);

    public int Taking => Changes.Count(c => c.What == ListRoleChangeKinds.Take);

    /// <summary>Everybody this plan stops counting as holding the role: taken away, or seen leaving.</summary>
    public int Losing => Taking + Leaving;

    /// <summary>Whether this many losses at once stops the pass (design §5).</summary>
    public bool LossStops => ListRoleChecks.Brakes(Losing, Holders + Leaving);

    /// <summary>Whether giving the role to most of the server at once stops the pass (design §5).</summary>
    public bool GiveStops => ListRoleChecks.BrakesGiving(Giving, Members);

    /// <summary>Whether either brake stops the pass until somebody presses Apply.</summary>
    public bool Stops => LossStops || GiveStops;
}

/// <summary>
/// Works out who a list role would give its Discord role to and take it from (roles from lists
/// design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The preview and the pass are this one class</strong>, so what the screen lists before a
/// pairing is saved or the switch is turned on is what the pass will do. It changes nothing.
/// </para>
/// <para>
/// Who is in the list comes from <see cref="GiveawayRuleChecker.PeopleAsync"/>, the same answer the
/// Lists page shows. A list that cannot be answered, rules that cannot be read and rules that let
/// everybody in are never acted on: the plan carries the reason and no changes.
/// </para>
/// <para>
/// <strong>Only with a fresh member list.</strong> Who holds the role comes from the stored
/// member list, which falls behind whenever the bot is not connected or member updates are off. So
/// nothing is planned unless the whole list was compared in the bot's current connection and the
/// bot is still listening (<see cref="MembersFreshFor"/>).
/// </para>
/// <para>
/// <strong>Only what it gave is taken</strong>, and a given-row is only forgotten when Modbot saw
/// the person leave the server (design §3).
/// </para>
/// </remarks>
public sealed class ListRolePlanner
{
    /// <summary>
    /// How long after the bot last noted it was listening the stored member list still counts as
    /// current. The bot notes it once a minute.
    /// </summary>
    public static readonly TimeSpan MembersFreshFor = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How old the VRChat group's member list may be before a list that asks about VRChat takes
    /// nobody's role away. Gives still go ahead (design §5).
    /// </summary>
    public static readonly TimeSpan VRChatStaleAfter = TimeSpan.FromHours(6);

    /// <summary>The rule kinds answered from Discord or Modbot alone; every other question reads VRChat data.</summary>
    private static readonly HashSet<string> DiscordOnlyKinds = new(StringComparer.Ordinal)
    {
        GiveawayRuleKinds.DiscordMemberDays,
        GiveawayRuleKinds.VoiceHours,
        GiveawayRuleKinds.Messages,
        GiveawayRuleKinds.DiscordRole,
        GiveawayRuleKinds.LinkedAccounts,
    };

    private const string WaitingForMembers = "Waiting for the member list.";

    /// <summary>How many times lists named inside lists are written out before the rest read as nobody.</summary>
    private const int MaxExpansions = 4;

    private readonly ModbotContext _db;
    private readonly GiveawayRuleChecker _checker;
    private readonly IModbotClock _clock;

    public ListRolePlanner(ModbotContext db, GiveawayRuleChecker checker, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _checker = checker;
        _clock = clock;
    }

    /// <summary>Every saved pairing's plan, oldest first; only the switched-on ones when asked.</summary>
    public async Task<IReadOnlyList<ListRolePlan>> PlanAllAsync(bool onlyEnabled, CancellationToken ct = default)
    {
        var pairings = await _db.DiscordListRoles.AsNoTracking()
            .Where(p => !onlyEnabled || p.Enabled)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (pairings.Count == 0)
            return [];

        var server = await ServerAsync(ct).ConfigureAwait(false);
        var plans = new List<ListRolePlan>(pairings.Count);

        foreach (var pairing in pairings)
            plans.Add(await PlanAsync(server, pairing.Id, pairing.ListId, pairing.DiscordRoleId, ct).ConfigureAwait(false));

        return plans;
    }

    /// <summary>One saved pairing's plan.</summary>
    public async Task<ListRolePlan> PlanAsync(DiscordListRole pairing, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);

        var server = await ServerAsync(ct).ConfigureAwait(false);
        return await PlanAsync(server, pairing.Id, pairing.ListId, pairing.DiscordRoleId, ct).ConfigureAwait(false);
    }

    /// <summary>What a pairing not saved yet would do: only gives, since it has given nothing.</summary>
    public async Task<ListRolePlan> PlanNewAsync(Guid listId, string discordRoleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discordRoleId);

        var server = await ServerAsync(ct).ConfigureAwait(false);
        return await PlanAsync(server, null, listId, discordRoleId.Trim(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Why this role cannot be given by a list, or null when it can (design §4).
    /// </summary>
    /// <param name="except">The pairing being changed, which does not count as another list giving the role.</param>
    public async Task<string?> WhyNotAsync(string discordRoleId, Guid? except, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(discordRoleId);

        var settings = await SettingsAsync(ct).ConfigureAwait(false);

        if (settings?.DiscordGuildId is not { } guildId)
            return "Discord is not set up.";

        var role = await _db.DiscordRoles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.GuildId == guildId && r.RoleId == discordRoleId, ct)
            .ConfigureAwait(false);

        return ListRoleChecks.WhyNot(role, await DecidedElsewhereAsync(settings, discordRoleId, except, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// A list's stored rules with every list they name written out, or why a list cannot give a role
    /// with them: rules that cannot be read are never taken to mean "everybody", and rules that let
    /// everybody in, or hold an empty group, are refused (design §4). Checked after the lists they
    /// name are written out, so a list that only names an empty list is caught too.
    /// </summary>
    public async Task<(GiveawayRule? Rules, string? Problem)> RulesAsync(string? json, CancellationToken ct = default)
    {
        GiveawayRule? rule = null;
        string? problem = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(json))
                rule = GiveawayRules.Read(JsonNode.Parse(json), out problem);
        }
        catch (JsonException)
        {
            rule = null;
        }

        if (rule is null || problem is not null)
            return (null, "The list's rules cannot be read.");

        // A list cannot name a list when it is saved, so one pass is all a tree takes; a few more
        // cover rows from before that check, and a list still named after them reads as nobody.
        var expanded = rule;
        for (var pass = 0; pass < MaxExpansions && GiveawayRules.ListsIn(expanded).Count > 0; pass++)
        {
            var next = await SavedListRules.ExpandAsync(_db, expanded, ct).ConfigureAwait(false);
            if (next == expanded || GiveawayRules.Count(next) > SavedListRules.MaxExpandedRules)
                break;

            expanded = next;
        }

        if (LetsEverybodyIn(expanded))
            return (null, "That list lets everybody in. Give it rules first.");

        if (HasEmptyGroup(expanded))
            return (null, "That list has an empty group of rules. Fill it or take it out first.");

        return (expanded, null);
    }

    // ── One plan ───────────────────────────────────────────────────────────────────────────

    private async Task<ListRolePlan> PlanAsync(Server server, Guid? pairingId, Guid listId, string roleId, CancellationToken ct)
    {
        var list = await _db.SavedLists.AsNoTracking()
            .Where(l => l.Id == listId)
            .Select(l => new { l.Name, l.Rules, l.DeletedAt })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var listName = list?.Name ?? string.Empty;
        var role = server.Roles.GetValueOrDefault(roleId);
        var roleName = role?.Name;

        ListRolePlan Refused(string why)
            => new(pairingId, listId, listName, roleId, roleName, [], [], 0, 0, 0, 0, 0, 0, 0, 0, null, why);

        if (list is null || list.DeletedAt is not null)
            return Refused("That list does not exist any more.");

        if (server.GuildId is null)
            return Refused("Discord is not set up.");

        var elsewhere = await DecidedElsewhereAsync(server.Settings, roleId, pairingId, ct).ConfigureAwait(false);

        if (ListRoleChecks.WhyNot(role, elsewhere) is { } why)
            return Refused(why);

        var (rules, rulesProblem) = await RulesAsync(list.Rules, ct).ConfigureAwait(false);
        if (rules is null)
            return Refused(rulesProblem!);

        // Who holds the role comes from the stored member list. Out of date, it would show roles
        // Modbot gave as missing and people who left as present.
        if (!server.MembersFresh)
            return Refused(WaitingForMembers);

        var people = await _checker.PeopleAsync(rules, ct).ConfigureAwait(false);

        if (people.Unanswerable is { } unanswerable)
            return Refused(unanswerable);

        var given = pairingId is { } id
            ? await _db.DiscordListRolesGiven.AsNoTracking()
                .Where(g => g.ListRoleId == id)
                .ToDictionaryAsync(g => g.DiscordUserId, StringComparer.Ordinal, ct)
                .ConfigureAwait(false)
            : new Dictionary<string, DiscordListRoleGiven>(StringComparer.Ordinal);

        // Of the people with a given-row, who Modbot saw leave the server. Somebody with no member
        // row at all was never seen leaving, and keeps theirs.
        var givenIds = given.Keys.ToList();
        var seenLeaving = givenIds.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await _db.DiscordMembers.AsNoTracking()
                .Where(m => m.GuildId == server.GuildId && m.LeftAt != null && givenIds.Contains(m.UserId))
                .Select(m => m.UserId)
                .ToListAsync(ct)
                .ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);

        var gives = new List<ListRoleChange>();
        var takes = new List<ListRoleChange>();
        var forget = new List<string>();
        var inList = new HashSet<string>(StringComparer.Ordinal);
        int alreadyHave = 0, takenByHand = 0, noLinked = 0, notInServer = 0, leaving = 0;

        foreach (var person in people.People.Select(p => p.Person))
        {
            if (person.DiscordUserId is not { } discordUserId)
            {
                noLinked++;
                continue;
            }

            inList.Add(discordUserId);

            if (!server.Members.TryGetValue(discordUserId, out var member))
            {
                notInServer++;

                // Leaving took the role; the row has done its work.
                if (given.ContainsKey(discordUserId) && seenLeaving.Contains(discordUserId))
                {
                    forget.Add(discordUserId);
                    leaving++;
                }

                continue;
            }

            if (member.Roles.Contains(roleId))
            {
                alreadyHave++;
                continue;
            }

            if (given.TryGetValue(discordUserId, out var row))
            {
                // Given before they last joined: they left and came back between two passes, and
                // leaving took the role. The row is from a membership that is over.
                if (member.JoinedAt is { } joined && joined > row.GivenAt)
                {
                    forget.Add(discordUserId);
                }
                else
                {
                    // Modbot gave it and the member list does not show it. Somebody took it off by
                    // hand, or the update has not arrived: either way it is not given again while
                    // the row stands, so a moderator is not overruled a minute later (design §3).
                    takenByHand++;
                    continue;
                }
            }

            gives.Add(new ListRoleChange(
                ListRoleChangeKinds.Give, discordUserId, person.VRChatUserId, person.Name ?? member.Name));
        }

        foreach (var row in given.Values.OrderBy(g => g.DiscordUserId, StringComparer.Ordinal))
        {
            if (inList.Contains(row.DiscordUserId))
                continue;

            if (server.Members.TryGetValue(row.DiscordUserId, out var member))
            {
                // Taken away whether or not the stored row shows the role: a missing role there is
                // a reason to ask Discord, which answers a removal of a role not held as done.
                takes.Add(new ListRoleChange(
                    ListRoleChangeKinds.Take, row.DiscordUserId, row.VRChatUserId, member.Name, member.Roles.Contains(roleId)));
            }
            else if (seenLeaving.Contains(row.DiscordUserId))
            {
                forget.Add(row.DiscordUserId);
                leaving++;
            }
        }

        var holders = server.Members.Values.Count(m => m.Roles.Contains(roleId));

        // A list that asks about VRChat, read from a VRChat member list that has not been read
        // lately, would take roles from people who only look gone. Gives still go ahead.
        var held = 0;
        string? heldBecause = null;

        if (takes.Count > 0 && AsksVRChat(rules) && VRChatStale(server.Settings) is { } stale)
        {
            held = takes.Count;
            heldBecause = stale;
            takes.Clear();
        }

        return new ListRolePlan(
            pairingId, listId, listName, roleId, roleName,
            [.. gives, .. takes], forget, leaving, alreadyHave, takenByHand, noLinked, notInServer, holders,
            server.Members.Count, held, heldBecause, null);
    }

    // ── What the rules ask ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the tree lets everybody through, by the checker's own reading: an empty "all of" and
    /// an empty "none of" let everybody in, an empty "any of" nobody, and a list that is gone nobody.
    /// </summary>
    public static bool LetsEverybodyIn(GiveawayRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule.Kind switch
        {
            GiveawayRuleKinds.AllOf => rule.Rules.All(LetsEverybodyIn),
            GiveawayRuleKinds.AnyOf => rule.Rules.Any(LetsEverybodyIn),
            GiveawayRuleKinds.NoneOf => rule.Rules.All(LetsNobodyIn),
            _ => false,
        };
    }

    private static bool LetsNobodyIn(GiveawayRule rule) => rule.Kind switch
    {
        GiveawayRuleKinds.AllOf => rule.Rules.Any(LetsNobodyIn),
        GiveawayRuleKinds.AnyOf => rule.Rules.All(LetsNobodyIn),
        GiveawayRuleKinds.NoneOf => rule.Rules.Any(LetsEverybodyIn),
        GiveawayRuleKinds.InList => true,
        _ => false,
    };

    /// <summary>
    /// Whether an "all of" or "none of" anywhere in the tree is empty. Such a group lets everybody
    /// through on its own, so whatever it sits in reads differently from what anybody meant.
    /// </summary>
    public static bool HasEmptyGroup(GiveawayRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!GiveawayRuleKinds.IsCombining(rule.Kind))
            return false;

        if (rule.Rules.Count == 0 && rule.Kind is GiveawayRuleKinds.AllOf or GiveawayRuleKinds.NoneOf)
            return true;

        return rule.Rules.Any(HasEmptyGroup);
    }

    /// <summary>Whether any question in the tree is answered from VRChat data.</summary>
    private static bool AsksVRChat(GiveawayRule rule)
        => GiveawayRuleKinds.IsCombining(rule.Kind) ? rule.Rules.Any(AsksVRChat) : !DiscordOnlyKinds.Contains(rule.Kind);

    /// <summary>Why the VRChat member list is too old to take roles away by, or null when it is not.</summary>
    private string? VRChatStale(Settings? settings)
    {
        var since = _clock.UtcNow - VRChatStaleAfter;

        return settings?.MemberSweepCompletedAt is { } swept && swept >= since
               && settings.AuditLogPolledAt is { } polled && polled >= since
            ? null
            : "Not taking the role away until the VRChat group's member list has been read again.";
    }

    // ── What is read once per plan or set of plans ─────────────────────────────────────────

    private sealed record Member(string? Name, DateTimeOffset? JoinedAt, HashSet<string> Roles);

    private sealed record Server(
        Settings? Settings,
        string? GuildId,
        bool MembersFresh,
        IReadOnlyDictionary<string, DiscordRole> Roles,
        IReadOnlyDictionary<string, Member> Members);

    private Task<Settings?> SettingsAsync(CancellationToken ct)
        => _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

    private async Task<Server> ServerAsync(CancellationToken ct)
    {
        var settings = await SettingsAsync(ct).ConfigureAwait(false);
        var guildId = string.IsNullOrWhiteSpace(settings?.DiscordGuildId) ? null : settings!.DiscordGuildId!.Trim();

        if (guildId is null)
        {
            return new Server(
                settings, null, false,
                new Dictionary<string, DiscordRole>(StringComparer.Ordinal),
                new Dictionary<string, Member>(StringComparer.Ordinal));
        }

        var roles = await _db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId)
            .ToDictionaryAsync(r => r.RoleId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        // Compared whole in the bot's current connection, and the bot still listening. Member
        // updates need no check of their own here, unlike staff roles: the list is only read with
        // the Server Members intent in the session (DiscordNetGateway.ReadMembersAsync answers null
        // without it), every connection clears MembersReadAt before reading, and only a compare in
        // that connection writes it again. So with member updates off or refused it stays empty,
        // and lists refuse on the same signal staff roles do.
        var read =await _db.DiscordServers.AsNoTracking()
            .Where(s => s.GuildId == guildId)
            .Select(s => new { s.MembersReadAt, s.SeenThrough })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var fresh = read?.MembersReadAt is { } readAt
                    && _clock.UtcNow - (read.SeenThrough is { } seen && seen > readAt ? seen : readAt) <= MembersFreshFor;

        // Everybody in the server now, bots left out: a bot is never in a list.
        var rows = await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && m.LeftAt == null && !m.IsBot)
            .Select(m => new { m.UserId, m.DisplayName, m.JoinedAt, m.Roles })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var members = new Dictionary<string, Member>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
            members[row.UserId] = new Member(row.DisplayName, row.JoinedAt, Ids(row.Roles));

        return new Server(settings, guildId, fresh, roles, members);
    }

    /// <summary>What already decides who holds this role, as a sentence, or null when nothing does.</summary>
    private async Task<string?> DecidedElsewhereAsync(Settings? settings, string roleId, Guid? except, CancellationToken ct)
    {
        if (await _db.DiscordRolePairs.AsNoTracking().AnyAsync(p => p.DiscordRoleId == roleId, ct).ConfigureAwait(false))
            return "That role is paired with a group role in role sync.";

        // A role a linked Modbot role gives and takes both ways is that sync's to write; two syncs
        // writing one role would undo each other (staff roles from Discord design §3.1).
        if (await _db.DiscordStaffRoles.AsNoTracking()
                .AnyAsync(m => m.DiscordRoleId == roleId && m.Direction == StaffRoleDirections.Both, ct)
                .ConfigureAwait(false))
        {
            return "That role is linked to a Modbot role both ways.";
        }

        if (string.Equals(settings?.DiscordLinkedRoleId?.Trim(), roleId, StringComparison.Ordinal))
            return "Modbot already gives that role to members who link their accounts.";

        if (string.Equals(settings?.DiscordEighteenPlusRoleId?.Trim(), roleId, StringComparison.Ordinal))
            return "Modbot already gives that role to linked members who are 18+.";

        var otherList = await _db.DiscordListRoles.AsNoTracking()
            .AnyAsync(p => p.DiscordRoleId == roleId && p.Id != except, ct)
            .ConfigureAwait(false);

        return otherList ? "Another list already gives that role." : null;
    }

    private static HashSet<string> Ids(string? json)
    {
        try
        {
            return (JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? []).ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
