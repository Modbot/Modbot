using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Giveaways;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Giveaways;

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
public sealed record ListRoleChange(string What, string DiscordUserId, string? VRChatUserId, string? Name);

/// <summary>What one list role would do right now (roles from lists design §5).</summary>
/// <param name="ListRoleId">The saved pairing, or null for one not saved yet.</param>
/// <param name="Changes">Roles to give, then roles to take away. Every one, not a page.</param>
/// <param name="Forget">
/// Discord accounts whose given-row goes quietly, with nothing sent: they left the server (which
/// took the role), somebody took the role off them by hand and they have since left the list, or
/// they left and came back between two passes (and are in <paramref name="Changes"/> to be given it
/// again).
/// </param>
/// <param name="AlreadyHave">In the list, in the server, and holding the role.</param>
/// <param name="TakenByHand">In the list, and somebody took off the role Modbot gave them. Left alone.</param>
/// <param name="NoLinkedDiscord">In the list by their VRChat account, with no linked Discord account.</param>
/// <param name="NotInServer">In the list with a Discord account that is not in the server.</param>
/// <param name="Holders">Everybody in the server holding the role now, however they got it.</param>
/// <param name="Problem">Why nothing can be done for it, as a sentence. Everything else is empty when set.</param>
public sealed record ListRolePlan(
    Guid? ListRoleId,
    Guid ListId,
    string ListName,
    string DiscordRoleId,
    string? RoleName,
    IReadOnlyList<ListRoleChange> Changes,
    IReadOnlyList<string> Forget,
    int AlreadyHave,
    int TakenByHand,
    int NoLinkedDiscord,
    int NotInServer,
    int Holders,
    string? Problem)
{
    public int Giving => Changes.Count(c => c.What == ListRoleChangeKinds.Give);

    public int Taking => Changes.Count(c => c.What == ListRoleChangeKinds.Take);

    /// <summary>Whether this many removals at once stops the pass (design §5).</summary>
    public bool Stops => ListRoleChecks.Brakes(Taking, Holders);
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
/// Lists page shows. A list that cannot be answered is never read as "nobody": the plan carries the
/// reason and no changes, because "nobody" would take the role from everybody.
/// </para>
/// <para>
/// <strong>Only what it gave is taken.</strong> A take needs a row in
/// <c>discord_list_role_given</c>; somebody holding the role with no row was given it by hand, and
/// is never touched (design §3).
/// </para>
/// </remarks>
public sealed class ListRolePlanner
{
    private readonly ModbotContext _db;
    private readonly GiveawayRuleChecker _checker;

    public ListRolePlanner(ModbotContext db, GiveawayRuleChecker checker)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(checker);

        _db = db;
        _checker = checker;
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

        ListRolePlan Refused(string why) => new(pairingId, listId, listName, roleId, roleName, [], [], 0, 0, 0, 0, 0, why);

        if (list is null || list.DeletedAt is not null)
            return Refused("That list does not exist any more.");

        if (server.GuildId is null)
            return Refused("Discord is not set up.");

        var elsewhere = await DecidedElsewhereAsync(server.Settings, roleId, pairingId, ct).ConfigureAwait(false);

        if (ListRoleChecks.WhyNot(role, elsewhere) is { } why)
            return Refused(why);

        var people = await _checker.PeopleAsync(GiveawayRules.ReadStored(list.Rules), ct).ConfigureAwait(false);

        if (people.Unanswerable is { } unanswerable)
            return Refused(unanswerable);

        var given = pairingId is { } id
            ? await _db.DiscordListRolesGiven.AsNoTracking()
                .Where(g => g.ListRoleId == id)
                .ToDictionaryAsync(g => g.DiscordUserId, StringComparer.Ordinal, ct)
                .ConfigureAwait(false)
            : new Dictionary<string, DiscordListRoleGiven>(StringComparer.Ordinal);

        var gives = new List<ListRoleChange>();
        var takes = new List<ListRoleChange>();
        var forget = new List<string>();
        var inList = new HashSet<string>(StringComparer.Ordinal);
        int alreadyHave = 0, takenByHand = 0, noLinked = 0, notInServer = 0;

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
                    // Modbot gave it and somebody took it off them by hand. Not given again while
                    // the row stands: a moderator is not overruled a minute later (design §3).
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

            // Leaving the server took every role, and a role somebody already took off them by hand
            // has nothing left to take. Either way the row has done its work.
            if (!server.Members.TryGetValue(row.DiscordUserId, out var member) || !member.Roles.Contains(roleId))
            {
                forget.Add(row.DiscordUserId);
                continue;
            }

            takes.Add(new ListRoleChange(ListRoleChangeKinds.Take, row.DiscordUserId, row.VRChatUserId, member.Name));
        }

        var holders = server.Members.Values.Count(m => m.Roles.Contains(roleId));

        return new ListRolePlan(
            pairingId, listId, listName, roleId, roleName,
            [.. gives, .. takes], forget, alreadyHave, takenByHand, noLinked, notInServer, holders, null);
    }

    // ── What is read once per plan or set of plans ─────────────────────────────────────────

    private sealed record Member(string? Name, DateTimeOffset? JoinedAt, HashSet<string> Roles);

    private sealed record Server(
        Settings? Settings,
        string? GuildId,
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
                settings, null,
                new Dictionary<string, DiscordRole>(StringComparer.Ordinal),
                new Dictionary<string, Member>(StringComparer.Ordinal));
        }

        var roles = await _db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId)
            .ToDictionaryAsync(r => r.RoleId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        // Everybody in the server now, bots left out: a bot is never in a list.
        var rows = await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && m.LeftAt == null && !m.IsBot)
            .Select(m => new { m.UserId, m.DisplayName, m.JoinedAt, m.Roles })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var members = new Dictionary<string, Member>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
            members[row.UserId] = new Member(row.DisplayName, row.JoinedAt, Ids(row.Roles));

        return new Server(settings, guildId, roles, members);
    }

    /// <summary>What already decides who holds this role, as a sentence, or null when nothing does.</summary>
    private async Task<string?> DecidedElsewhereAsync(Settings? settings, string roleId, Guid? except, CancellationToken ct)
    {
        if (await _db.DiscordRolePairs.AsNoTracking().AnyAsync(p => p.DiscordRoleId == roleId, ct).ConfigureAwait(false))
            return "That role is paired with a group role in role sync.";

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
