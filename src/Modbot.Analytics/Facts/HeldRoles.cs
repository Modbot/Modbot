using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Analytics.Facts;

/// <summary>
/// The roles the people in a fact held when it happened: the VRChat group roles of the person it
/// happened to and of the person who did it, and the Modbot roles of the person who did it.
/// </summary>
/// <remarks>
/// <para>
/// Discord event routes design §3. A route that sends "bans by Moderators" must decide on the
/// roles as they were at the ban, not at the post: a bot that was offline while a moderator lost
/// their role would otherwise drop the ban from the channel on catch-up.
/// </para>
/// <para>
/// <strong>Saved in the fact's own payload, under <see cref="Key"/>, when the fact is written.</strong>
/// Facts are never changed after that (foundation §5.2), so the roles travel with the fact: a
/// retention move keeps them, a purge removes them with the fact, and nothing is left behind in a
/// second table to tidy up. Absent on facts written before this, on facts with no person in them,
/// and on client presence reports, whose volume makes a lookup per report poor value; for those
/// <see cref="RoleHistory"/> works the roles out from the role changes the log recorded.
/// </para>
/// </remarks>
public sealed record HeldRoles(
    IReadOnlyList<string> SubjectVRChatRoles,
    IReadOnlyList<string> ActorVRChatRoles,
    IReadOnlyList<string> ActorModbotRoles)
{
    /// <summary>The payload key the roles are saved under.</summary>
    public const string Key = "heldRoles";

    public static HeldRoles None { get; } = new([], [], []);

    public JsonObject ToJson() => new()
    {
        ["subjectVRChatRoles"] = Array(SubjectVRChatRoles),
        ["actorVRChatRoles"] = Array(ActorVRChatRoles),
        ["actorModbotRoles"] = Array(ActorModbotRoles),
    };

    /// <summary>The saved roles in a fact's payload, or null when it has none.</summary>
    public static HeldRoles? Read(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return null;

        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(Key, out var held)
                || held.ValueKind != JsonValueKind.Object)
                return null;

            return new HeldRoles(
                Strings(held, "subjectVRChatRoles"),
                Strings(held, "actorVRChatRoles"),
                Strings(held, "actorModbotRoles"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)v).ToArray());

    private static List<string> Strings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }
}

/// <summary>A Modbot account as the lookups see it.</summary>
public sealed record DirectoryAccount(Guid Id, string? VRChatUserId, string? DiscordUserId, IReadOnlySet<Guid> RoleIds);

/// <summary>Every account a person in a fact stands for, and their Modbot account if they have one.</summary>
public sealed record PersonIdentities(IReadOnlySet<string> VRChatIds, IReadOnlySet<string> DiscordIds, DirectoryAccount? Account)
{
    public static PersonIdentities Nobody { get; } =
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), null);

    public bool IsNobody => VRChatIds.Count == 0 && DiscordIds.Count == 0 && Account is null;
}

/// <summary>
/// Who the subject and actor of a fact are, across VRChat, Discord and Modbot (Discord event routes
/// design §3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>An account always stands for itself.</strong> A VRChat id is that VRChat account and a
/// Discord id that Discord account, whether or not the person ever linked anything, because most
/// never will. A link only adds: a VRChat account linked to a Discord account (the
/// <c>discord_account_link</c> table, active rows) stands for both, and so does the Discord side.
/// </para>
/// <para>
/// A Modbot account id stands for the VRChat account it linked and the Discord id stored on it,
/// plus whatever those are linked to. Anything else -- a group, a location, a channel -- is nobody.
/// </para>
/// <para>
/// <see cref="Of"/> is the one place identities are worked out; a new kind of link is added here.
/// </para>
/// </remarks>
public sealed class PeopleDirectory
{
    private readonly Dictionary<Guid, DirectoryAccount> _byId = [];
    private readonly Dictionary<string, DirectoryAccount> _byVRChat = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectoryAccount> _byDiscord = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _discordOfVRChat = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _vrchatOfDiscord = new(StringComparer.Ordinal);

    /// <param name="links">Active links, as (VRChat user id, Discord user id).</param>
    public PeopleDirectory(IEnumerable<DirectoryAccount> accounts, IEnumerable<(string VRChatUserId, string DiscordUserId)> links)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(links);

        foreach (var account in accounts)
        {
            _byId[account.Id] = account;
            if (!string.IsNullOrEmpty(account.VRChatUserId)) _byVRChat.TryAdd(account.VRChatUserId, account);
            if (!string.IsNullOrEmpty(account.DiscordUserId)) _byDiscord.TryAdd(account.DiscordUserId, account);
        }

        foreach (var (vrchat, discord) in links)
        {
            Add(_discordOfVRChat, vrchat, discord);
            Add(_vrchatOfDiscord, discord, vrchat);
        }
    }

    public static PeopleDirectory Empty { get; } = new([], []);

    public PersonIdentities Of(FactPlatform? platform, string? id)
    {
        if (string.IsNullOrEmpty(id))
            return PersonIdentities.Nobody;

        var vrchat = new HashSet<string>(StringComparer.Ordinal);
        var discord = new HashSet<string>(StringComparer.Ordinal);
        DirectoryAccount? account = null;

        switch (platform)
        {
            case FactPlatform.VRChat:
                vrchat.Add(id);
                break;

            case FactPlatform.Discord:
                discord.Add(id);
                break;

            case FactPlatform.Modbot when Guid.TryParse(id, out var guid) && _byId.TryGetValue(guid, out var found):
                account = found;
                if (!string.IsNullOrEmpty(found.VRChatUserId)) vrchat.Add(found.VRChatUserId);
                if (!string.IsNullOrEmpty(found.DiscordUserId)) discord.Add(found.DiscordUserId);
                break;

            default:
                return PersonIdentities.Nobody;
        }

        // A link adds the other side. One step is enough: a Discord account links one VRChat
        // account and the reverse.
        foreach (var v in vrchat.ToList())
            discord.UnionWith(_discordOfVRChat.GetValueOrDefault(v) ?? []);
        foreach (var d in discord.ToList())
            vrchat.UnionWith(_vrchatOfDiscord.GetValueOrDefault(d) ?? []);

        account ??= vrchat.Select(v => _byVRChat.GetValueOrDefault(v)).FirstOrDefault(a => a is not null)
            ?? discord.Select(d => _byDiscord.GetValueOrDefault(d)).FirstOrDefault(a => a is not null);

        return new PersonIdentities(vrchat, discord, account);
    }

    /// <summary>Accounts and active links for everybody named in these (platform, id) pairs, in a handful of queries.</summary>
    public static async Task<PeopleDirectory> LoadAsync(
        ModbotContext db, IEnumerable<(FactPlatform? Platform, string? Id)> parties, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(parties);

        var guids = new HashSet<Guid>();
        var vrchat = new HashSet<string>(StringComparer.Ordinal);
        var discord = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (platform, id) in parties)
        {
            if (string.IsNullOrEmpty(id)) continue;

            switch (platform)
            {
                case FactPlatform.VRChat: vrchat.Add(id); break;
                case FactPlatform.Discord: discord.Add(id); break;
                case FactPlatform.Modbot when Guid.TryParse(id, out var guid): guids.Add(guid); break;
            }
        }

        if (guids.Count == 0 && vrchat.Count == 0 && discord.Count == 0)
            return Empty;

        var accounts = await AccountsAsync(db, guids, vrchat, discord, ct).ConfigureAwait(false);

        foreach (var account in accounts)
        {
            if (!string.IsNullOrEmpty(account.VRChatUserId)) vrchat.Add(account.VRChatUserId);
            if (!string.IsNullOrEmpty(account.DiscordUserId)) discord.Add(account.DiscordUserId);
        }

        var vrchatList = vrchat.ToList();
        var discordList = discord.ToList();

        var links = await db.ActiveAccountLinks()
            .Where(l => vrchatList.Contains(l.VRChatUserId) || discordList.Contains(l.DiscordUserId))
            .Select(l => new { l.VRChatUserId, l.DiscordUserId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // The other side of a link may have a Modbot account the first query could not see.
        var linkedVRChat = links.Select(l => l.VRChatUserId).Where(v => !vrchat.Contains(v)).ToHashSet(StringComparer.Ordinal);
        var linkedDiscord = links.Select(l => l.DiscordUserId).Where(d => !discord.Contains(d)).ToHashSet(StringComparer.Ordinal);

        if (linkedVRChat.Count > 0 || linkedDiscord.Count > 0)
        {
            var more = await AccountsAsync(db, [], linkedVRChat, linkedDiscord, ct).ConfigureAwait(false);
            accounts.AddRange(more.Where(m => accounts.All(a => a.Id != m.Id)));
        }

        return new PeopleDirectory(accounts, links.Select(l => (l.VRChatUserId, l.DiscordUserId)));
    }

    private static async Task<List<DirectoryAccount>> AccountsAsync(
        ModbotContext db, IReadOnlyCollection<Guid> guids, IReadOnlyCollection<string> vrchat, IReadOnlyCollection<string> discord, CancellationToken ct)
    {
        var guidList = guids.ToList();
        var vrchatList = vrchat.ToList();
        var discordList = discord.ToList();

        var rows = await db.Users.AsNoTracking()
            .Where(u => guidList.Contains(u.Id)
                || (u.VRChatUserId != null && vrchatList.Contains(u.VRChatUserId))
                || (u.DiscordUserId != null && discordList.Contains(u.DiscordUserId)))
            .Select(u => new { u.Id, u.VRChatUserId, u.DiscordUserId, Roles = u.Roles.Select(r => r.RoleId).ToList() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => new DirectoryAccount(r.Id, r.VRChatUserId, r.DiscordUserId, r.Roles.ToHashSet())).ToList();
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(value);
    }
}

/// <summary>
/// Works out the roles people held at a moment from what the fact log recorded since.
/// </summary>
/// <remarks>
/// <para>
/// Starts from the roles held now -- <c>group_member.roles</c>, which a member who left keeps, and
/// the account's Modbot roles -- and walks back through every recorded change after the moment,
/// newest first, undoing each: a role granted afterwards was not held yet, a role taken away
/// afterwards still was. With no recorded change after the moment, the roles now are the answer,
/// which is the fallback for a person Modbot has no history for.
/// </para>
/// <para>
/// Used when a fact is written, so the saved roles are right even for an audit-log entry caught up
/// days late, and when a route meets a fact that has no saved roles.
/// </para>
/// </remarks>
public static class RoleHistory
{
    /// <summary>The roles of the people in one fact at <paramref name="at"/>, or null when neither is a person.</summary>
    public static async Task<HeldRoles?> ForFactAsync(
        ModbotContext db,
        PeopleDirectory people,
        string? groupId,
        FactPlatform subjectPlatform,
        string subjectId,
        FactPlatform? actorPlatform,
        string? actorId,
        DateTimeOffset at,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(people);

        var subject = people.Of(subjectPlatform, subjectId);
        var actor = actorId is null ? PersonIdentities.Nobody : people.Of(actorPlatform, actorId);

        if (subject.IsNobody && actor.IsNobody)
            return null;

        var subjectRoles = await GroupRolesAsOfAsync(db, groupId, subject.VRChatIds, at, ct).ConfigureAwait(false);
        var actorRoles = await GroupRolesAsOfAsync(db, groupId, actor.VRChatIds, at, ct).ConfigureAwait(false);
        var actorModbot = actor.Account is { } account
            ? await ModbotRolesAsOfAsync(db, account, at, ct).ConfigureAwait(false)
            : [];

        return new HeldRoles(subjectRoles, actorRoles, actorModbot.Select(g => g.ToString()).ToList());
    }

    /// <summary>The managed group's roles these VRChat accounts held at <paramref name="at"/>.</summary>
    public static async Task<IReadOnlyList<string>> GroupRolesAsOfAsync(
        ModbotContext db, string? groupId, IReadOnlySet<string> vrchatIds, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(vrchatIds);

        if (string.IsNullOrEmpty(groupId) || vrchatIds.Count == 0)
            return [];

        var ids = vrchatIds.ToList();

        var rows = await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId && ids.Contains(m.UserId))
            .Select(m => m.Roles)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var roles in rows)
            held.UnionWith(RoleIds(roles));

        var changes = await db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat
                && ids.Contains(e.SubjectId)
                && (e.Type == FactType.RoleGranted || e.Type == FactType.RoleRevoked)
                && e.OccurredAt > at)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Select(e => new { e.Type, e.Data })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var change in changes)
        {
            if (Text(change.Data, "roleId") is not { } roleId)
                continue;

            if (change.Type == FactType.RoleGranted)
                held.Remove(roleId);
            else
                held.Add(roleId);
        }

        return held.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The Modbot roles an account held at <paramref name="at"/>: the "before" of the first roles
    /// change recorded after it, or the roles now when there was none.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ModbotRolesAsOfAsync(
        ModbotContext db, DirectoryAccount account, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(account);

        var subjectId = account.Id.ToString();

        var next = await db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.Modbot
                && e.SubjectId == subjectId
                && e.Type == FactType.UserRolesChanged
                && e.OccurredAt > at)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .Select(e => e.Data)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (next is null)
            return account.RoleIds.Order().ToList();

        using var document = JsonDocument.Parse(next);
        var root = document.RootElement;

        if (root.TryGetProperty("beforeRoleIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            return ids.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();
        }

        // Written before role ids were recorded: names joined with ", ", read against the roles as
        // they are named now. A role renamed since is not found, and is left out.
        if (!root.TryGetProperty("before", out var before) || before.ValueKind != JsonValueKind.String)
            return account.RoleIds.Order().ToList();

        var names = (before.GetString() ?? string.Empty)
            .Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ModbotRole.Normalize)
            .ToList();

        if (names.Count == 0)
            return [];

        return await db.Roles.AsNoTracking()
            .Where(r => names.Contains(r.NameNormalized))
            .Select(r => r.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private static List<string> RoleIds(string? json)
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

    private static string? Text(string data, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
