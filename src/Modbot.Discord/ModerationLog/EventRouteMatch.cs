using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Discord.ModerationLog;

/// <summary>
/// Whether one fact goes to one route (Discord event routes design §3). Pure: everything it needs
/// to know about people and their roles arrives in a <see cref="RoutePeople"/>.
/// </summary>
public static class EventRouteMatch
{
    /// <summary>Every filter that is set must match; inside one filter any listed value is enough.</summary>
    public static bool Matches(DiscordEventRoute route, ModbotEvent fact, RoutePeople people)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(people);

        if (!DiscordEventTypes.CanSend(fact.Type)
            || !route.EventTypes.Contains(DiscordEventTypes.RouteTypeOf(fact.Type), StringComparer.Ordinal))
            return false;

        if (IsGroupBaseline(fact) || IsCatchUp(fact))
            return false;

        var subject = people.Of(fact.SubjectPlatform, fact.SubjectId);

        if ((route.SubjectIds.Count > 0 || route.SubjectDiscordIds.Count > 0)
            && !Named(subject, route.SubjectIds, route.SubjectDiscordIds))
            return false;

        var roles = people.RolesOf(fact);

        if (route.SubjectVRChatRoleIds.Count > 0 && !roles.SubjectVRChatRoles.Intersect(route.SubjectVRChatRoleIds, StringComparer.Ordinal).Any())
            return false;

        var actor = fact.ActorId is null ? PersonIdentities.Nobody : people.Of(fact.ActorPlatform, fact.ActorId);

        if (route.ActorIds.Count > 0 || route.ActorDiscordIds.Count > 0 || route.ActorAutomatic)
        {
            var byNobody = route.ActorAutomatic && fact.ActorId is null;
            var byListed = fact.ActorId is not null && Named(actor, route.ActorIds, route.ActorDiscordIds);

            if (!byNobody && !byListed)
                return false;
        }

        if (route.ActorVRChatRoleIds.Count > 0
            && (fact.ActorId is null || !roles.ActorVRChatRoles.Intersect(route.ActorVRChatRoleIds, StringComparer.Ordinal).Any()))
            return false;

        if (route.ActorModbotRoleIds.Count > 0
            && (fact.ActorId is null
                || !roles.ActorModbotRoles.Any(r => Guid.TryParse(r, out var id) && route.ActorModbotRoleIds.Contains(id))))
            return false;

        return true;
    }

    /// <summary>Whether any of the routes -- all sending to one channel -- takes the fact. The channel gets it once.</summary>
    public static bool AnyMatches(IEnumerable<DiscordEventRoute> routes, ModbotEvent fact, RoutePeople people)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return routes.Any(r => Matches(r, fact, people));
    }

    /// <summary>
    /// The group-details fact the first poll writes: the whole group as Modbot first saw it, with
    /// nothing changed. It is the starting point for the member count, not an event, and a card for
    /// it would list no changes at all.
    /// </summary>
    private static bool IsGroupBaseline(ModbotEvent fact)
    {
        if (!string.Equals(fact.Type, FactType.GroupInfoChanged, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(fact.Data)
            || !fact.Data.Contains("\"baseline\"", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(fact.Data);

            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && document.RootElement.TryGetProperty("baseline", out _);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// An entry Modbot wrote long after the thing it records, to fill in what it had not written
    /// before: an instance that ended before "ended on its own" entries existed. It is history
    /// arriving late, not news, and a channel would get one card for every instance the group ever
    /// ran. The audit log and the Stats page still show it.
    /// </summary>
    private static bool IsCatchUp(ModbotEvent fact)
    {
        if (!string.Equals(fact.Type, FactType.InstanceEndedOnItsOwn, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(fact.Data)
            || !fact.Data.Contains("\"catchUp\"", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(fact.Data);

            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && document.RootElement.TryGetProperty("catchUp", out var flag)
                   && flag.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>A person matches a list when any account they stand for is on it.</summary>
    private static bool Named(PersonIdentities person, IReadOnlyCollection<string> vrchat, IReadOnlyCollection<string> discord)
        => person.VRChatIds.Any(vrchat.Contains) || person.DiscordIds.Any(discord.Contains);
}

/// <summary>
/// What the route filters know about a batch of facts: who the people in them are, across
/// platforms, and the roles they held when each fact happened.
/// </summary>
public sealed class RoutePeople
{
    private readonly PeopleDirectory _directory;
    private readonly IReadOnlyDictionary<long, HeldRoles> _roles;

    /// <param name="roles">By fact id: the roles its people held when it happened.</param>
    public RoutePeople(PeopleDirectory directory, IReadOnlyDictionary<long, HeldRoles> roles)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(roles);

        _directory = directory;
        _roles = roles;
    }

    /// <summary>Nobody known and no roles. Enough for routes without people filters.</summary>
    public static RoutePeople Empty { get; } = new(PeopleDirectory.Empty, new Dictionary<long, HeldRoles>());

    public PersonIdentities Of(FactPlatform? platform, string? id) => _directory.Of(platform, id);

    /// <summary>The roles held when the fact happened. None known reads as no roles, so a role filter does not match.</summary>
    public HeldRoles RolesOf(ModbotEvent fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return _roles.GetValueOrDefault(fact.Id) ?? HeldRoles.None;
    }

    /// <summary>
    /// People for these facts, and when <paramref name="withRoles"/> the roles of each: the ones
    /// saved in the fact when it was written, or for an older fact without them, worked out from
    /// the role changes recorded since (<see cref="RoleHistory"/>).
    /// </summary>
    /// <param name="now">From <c>IModbotClock</c>: whether typed staff Discord ids still count.</param>
    public static async Task<RoutePeople> LoadAsync(
        ModbotContext db, IReadOnlyCollection<ModbotEvent> facts, bool withRoles, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);

        var directory = await PeopleDirectory.LoadAsync(
                db,
                facts.Select(f => ((FactPlatform?)f.SubjectPlatform, (string?)f.SubjectId))
                    .Concat(facts.Select(f => (f.ActorPlatform, f.ActorId))),
                now,
                ct)
            .ConfigureAwait(false);

        var roles = new Dictionary<long, HeldRoles>();

        if (withRoles)
        {
            string? groupId = null;
            var groupRead = false;

            foreach (var fact in facts)
            {
                if (HeldRoles.Read(fact.Data) is { } saved)
                {
                    roles[fact.Id] = saved;
                    continue;
                }

                if (!groupRead)
                {
                    groupId = await db.Settings.AsNoTracking()
                        .Where(s => s.Id == 1)
                        .Select(s => s.ManagedGroupId)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false);
                    groupRead = true;
                }

                if (await RoleHistory.ForFactAsync(
                        db, directory, groupId, fact.SubjectPlatform, fact.SubjectId, fact.ActorPlatform, fact.ActorId, fact.OccurredAt, ct)
                    .ConfigureAwait(false) is { } worked)
                {
                    roles[fact.Id] = worked;
                }
            }
        }

        return new RoutePeople(directory, roles);
    }
}
