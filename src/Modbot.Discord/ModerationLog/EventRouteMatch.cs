using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Discord.ModerationLog;

/// <summary>
/// Whether one fact goes to one route (Discord event routes design §3). Pure: everything it needs
/// to know about people arrives in a <see cref="RoutePeople"/>.
/// </summary>
public static class EventRouteMatch
{
    /// <summary>Every filter that is set must match; inside one filter any listed value is enough.</summary>
    public static bool Matches(DiscordEventRoute route, ModbotEvent fact, RoutePeople people)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(people);

        if (!DiscordEventTypes.CanSend(fact.Type) || !route.EventTypes.Contains(fact.Type, StringComparer.Ordinal))
            return false;

        var subject = people.PersonOf(fact.SubjectPlatform, fact.SubjectId);

        if (route.SubjectIds.Count > 0 && (subject is null || !route.SubjectIds.Contains(subject, StringComparer.Ordinal)))
            return false;

        if (route.SubjectVRChatRoleIds.Count > 0 && (subject is null || !people.GroupRolesOf(subject).Overlaps(route.SubjectVRChatRoleIds)))
            return false;

        var actor = fact.ActorId is null ? null : people.PersonOf(fact.ActorPlatform, fact.ActorId);

        if (route.ActorIds.Count > 0 || route.ActorAutomatic)
        {
            var byNobody = route.ActorAutomatic && fact.ActorId is null;
            var byListed = actor is not null && route.ActorIds.Contains(actor, StringComparer.Ordinal);

            if (!byNobody && !byListed)
                return false;
        }

        if (route.ActorVRChatRoleIds.Count > 0 && (actor is null || !people.GroupRolesOf(actor).Overlaps(route.ActorVRChatRoleIds)))
            return false;

        if (route.ActorModbotRoleIds.Count > 0
            && (fact.ActorId is null || !people.ModbotRolesOf(fact.ActorPlatform, fact.ActorId).Overlaps(route.ActorModbotRoleIds)))
            return false;

        return true;
    }

    /// <summary>Whether any of the routes -- all sending to one channel -- takes the fact. The channel gets it once.</summary>
    public static bool AnyMatches(IEnumerable<DiscordEventRoute> routes, ModbotEvent fact, RoutePeople people)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return routes.Any(r => Matches(r, fact, people));
    }
}

/// <summary>A Modbot account as the route filters see it: which VRChat and Discord accounts it stored, and its roles.</summary>
public sealed record RouteAccount(Guid Id, string? VRChatUserId, string? DiscordUserId, IReadOnlySet<Guid> RoleIds);

/// <summary>
/// What the route filters know about the people named in a batch of facts: their Modbot accounts,
/// and the VRChat group roles they hold now.
/// </summary>
/// <remarks>
/// A subject or actor is read as a VRChat user id. A VRChat id is used as it is; a Modbot account
/// id is read as the VRChat account that account linked; a Discord user id through the Modbot
/// account that stored it. Anything else -- a group, a location, a channel -- is nobody.
/// </remarks>
public sealed class RoutePeople
{
    private static readonly IReadOnlySet<string> NoRoles = new HashSet<string>();
    private static readonly IReadOnlySet<Guid> NoModbotRoles = new HashSet<Guid>();

    private readonly Dictionary<Guid, RouteAccount> _byId;
    private readonly Dictionary<string, RouteAccount> _byVRChat;
    private readonly Dictionary<string, RouteAccount> _byDiscord;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _groupRoles;

    public RoutePeople(
        IEnumerable<RouteAccount> accounts,
        IReadOnlyDictionary<string, IReadOnlySet<string>> groupRoles)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(groupRoles);

        var list = accounts.ToList();
        _byId = list.ToDictionary(a => a.Id);
        _byVRChat = list.Where(a => !string.IsNullOrEmpty(a.VRChatUserId))
            .GroupBy(a => a.VRChatUserId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _byDiscord = list.Where(a => !string.IsNullOrEmpty(a.DiscordUserId))
            .GroupBy(a => a.DiscordUserId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _groupRoles = groupRoles;
    }

    /// <summary>Nobody known: no accounts, no roles. Enough for routes without people filters.</summary>
    public static RoutePeople Empty { get; } = new([], new Dictionary<string, IReadOnlySet<string>>());

    /// <summary>The VRChat user id a subject or actor stands for, or null when it is not a known person.</summary>
    public string? PersonOf(FactPlatform? platform, string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        return platform switch
        {
            FactPlatform.VRChat => id,
            FactPlatform.Modbot => Guid.TryParse(id, out var guid) && _byId.TryGetValue(guid, out var account)
                ? Blank(account.VRChatUserId)
                : null,
            FactPlatform.Discord => _byDiscord.TryGetValue(id, out var linked) ? Blank(linked.VRChatUserId) : null,
            _ => null,
        };
    }

    /// <summary>The managed group's roles this VRChat user holds now. Empty for somebody who is not a member.</summary>
    public IReadOnlySet<string> GroupRolesOf(string vrchatUserId)
        => _groupRoles.TryGetValue(vrchatUserId, out var roles) ? roles : NoRoles;

    /// <summary>The Modbot roles of the account behind a subject or actor. Empty when there is no account.</summary>
    public IReadOnlySet<Guid> ModbotRolesOf(FactPlatform? platform, string? id)
    {
        if (string.IsNullOrEmpty(id))
            return NoModbotRoles;

        var account = platform switch
        {
            FactPlatform.Modbot => Guid.TryParse(id, out var guid) ? _byId.GetValueOrDefault(guid) : null,
            FactPlatform.VRChat => _byVRChat.GetValueOrDefault(id),
            FactPlatform.Discord => _byDiscord.GetValueOrDefault(id),
            _ => null,
        };

        return account?.RoleIds ?? NoModbotRoles;
    }

    /// <summary>
    /// Everything the filters need for these facts, in three queries: the Modbot accounts named or
    /// linked, and the group roles of every VRChat person among them.
    /// </summary>
    public static async Task<RoutePeople> LoadAsync(ModbotContext db, IReadOnlyCollection<ModbotEvent> facts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);

        var named = facts
            .Select(f => (f.SubjectPlatform as FactPlatform?, (string?)f.SubjectId))
            .Concat(facts.Select(f => (f.ActorPlatform, f.ActorId)))
            .Where(p => !string.IsNullOrEmpty(p.Item2))
            .Distinct()
            .ToList();

        var modbotIds = named.Where(p => p.Item1 == FactPlatform.Modbot)
            .Select(p => Guid.TryParse(p.Item2, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();
        var vrchatIds = named.Where(p => p.Item1 == FactPlatform.VRChat).Select(p => p.Item2!).Distinct(StringComparer.Ordinal).ToList();
        var discordIds = named.Where(p => p.Item1 == FactPlatform.Discord).Select(p => p.Item2!).Distinct(StringComparer.Ordinal).ToList();

        var accounts = await db.Users.AsNoTracking()
            .Where(u => modbotIds.Contains(u.Id)
                || (u.VRChatUserId != null && vrchatIds.Contains(u.VRChatUserId))
                || (u.DiscordUserId != null && discordIds.Contains(u.DiscordUserId)))
            .Select(u => new
            {
                u.Id,
                u.VRChatUserId,
                u.DiscordUserId,
                Roles = u.Roles.Select(r => r.RoleId).ToList(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var routeAccounts = accounts
            .Select(a => new RouteAccount(a.Id, a.VRChatUserId, a.DiscordUserId, a.Roles.ToHashSet()))
            .ToList();

        var people = vrchatIds
            .Concat(routeAccounts.Select(a => a.VRChatUserId).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var groupId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ManagedGroupId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var roles = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(groupId) && people.Count > 0)
        {
            var members = await db.GroupMembers.AsNoTracking()
                .Where(m => m.GroupId == groupId && m.LeftAt == null && people.Contains(m.UserId))
                .Select(m => new { m.UserId, m.Roles })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var member in members)
                roles[member.UserId] = ReadRoles(member.Roles);
        }

        return new RoutePeople(routeAccounts, roles);
    }

    /// <summary><c>group_member.roles</c>: a JSON array of role id strings. Anything else reads as no roles.</summary>
    private static HashSet<string> ReadRoles(string? json)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
            return set;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return set;

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id)
                    set.Add(id);
            }
        }
        catch (JsonException)
        {
        }

        return set;
    }

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
