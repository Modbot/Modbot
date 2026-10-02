using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Giveaways;

/// <summary>Where a saved list is used, by what it would change.</summary>
/// <param name="Giveaways">The names of giveaways still being run that name it among their rules.</param>
/// <param name="AutoInvites">Auto-invites name it among their rules.</param>
/// <param name="Events">
/// The titles of events still being run that invite the list (calendar auto-invite design §9).
/// </param>
/// <param name="DiscordRoles">
/// The names of the Discord roles the list gives (roles from lists design §9), switched on or not:
/// a pairing switched off still names the list.
/// </param>
public sealed record SavedListUse(
    IReadOnlyList<string> Giveaways,
    bool AutoInvites,
    IReadOnlyList<string> Events,
    IReadOnlyList<string> DiscordRoles)
{
    public bool Any => Giveaways.Count > 0 || AutoInvites || Events.Count > 0 || DiscordRoles.Count > 0;
}

/// <summary>
/// Puts each saved list a rule tree names in its place, so a list is read by exactly the same
/// checker as the rules around it (lists design §4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The list's rules stand in for it; nothing else knows a list exists.</strong> The
/// checker, the retention refusal and the measurements all work on the tree that comes back, so a
/// list in a giveaway can never answer differently from the same rules typed into the giveaway
/// (giveaways design §2.6).
/// </para>
/// <para>
/// A list inside a list is refused when a list is saved, so one pass is all expanding ever takes
/// and no list can name itself, however indirectly.
/// </para>
/// <para>
/// A list that does not exist any more stays as it is, and the checker lets nobody through it
/// with the reason in words. A missing list must not quietly mean "everybody".
/// </para>
/// </remarks>
public static class SavedListRules
{
    /// <summary>
    /// The most rules a tree may hold once its lists are written out. A tree and its lists were
    /// each checked against <see cref="GiveawayRule.MaxTotalRules"/>; this keeps a tree naming many
    /// large lists from walking thousands of rules for every person.
    /// </summary>
    public const int MaxExpandedRules = GiveawayRule.MaxTotalRules * 4;

    /// <summary>The tree with every list it names written out, from lists already read.</summary>
    public static GiveawayRule Expand(GiveawayRule rule, IReadOnlyDictionary<string, GiveawayRule> lists)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(lists);

        if (rule.Kind == GiveawayRuleKinds.InList)
            return rule.Id is { } id && lists.TryGetValue(id, out var own) ? own : rule;

        if (!GiveawayRuleKinds.IsCombining(rule.Kind) || rule.Rules.Count == 0)
            return rule;

        var inner = new List<GiveawayRule>(rule.Rules.Count);

        foreach (var child in rule.Rules)
        {
            var written = Expand(child, lists);

            // "All of: in the list, and …" where the list is itself "all of: a, b" is "all of: a,
            // b, and …". Written flat so the Discord card lists a and b as two lines, the way the
            // organiser would have typed them, rather than one line with both inside it.
            if (child.Kind == GiveawayRuleKinds.InList
                && rule.Kind == GiveawayRuleKinds.AllOf
                && written.Kind == GiveawayRuleKinds.AllOf)
            {
                inner.AddRange(written.Rules);
            }
            else
            {
                inner.Add(written);
            }
        }

        return rule with { Rules = inner };
    }

    /// <summary>The tree with every list it names written out, reading the lists it needs.</summary>
    public static async Task<GiveawayRule> ExpandAsync(ModbotContext db, GiveawayRule rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rule);

        var named = GiveawayRules.ListsIn(rule);
        if (named.Count == 0)
            return rule;

        return Expand(rule, await RulesAsync(db, named, ct));
    }

    /// <summary>The rules of the lists asked for, by id. Deleted lists are left out.</summary>
    public static async Task<IReadOnlyDictionary<string, GiveawayRule>> RulesAsync(
        ModbotContext db, IReadOnlyCollection<string>? ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var wanted = Guids(ids);

        var rows = await db.SavedLists.AsNoTracking()
            .Where(l => l.DeletedAt == null && (wanted == null || wanted.Contains(l.Id)))
            .Select(l => new { l.Id, l.Rules })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.Id.ToString("D"),
            r => GiveawayRules.ReadStored(r.Rules),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Every list's name by id, deleted ones too, for the rule lines that name a list. The
    /// dictionary the rule lines take for role names takes these beside them.
    /// </summary>
    public static async Task<Dictionary<string, string>> NamesAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        return await db.SavedLists.AsNoTracking()
            .Select(l => new { l.Id, l.Name })
            .ToDictionaryAsync(l => l.Id.ToString("D"), l => l.Name, StringComparer.Ordinal, ct);
    }

    /// <summary>Why a tree cannot be saved because of a list it names, or null when it can.</summary>
    public static async Task<string?> WhyNotUsableAsync(ModbotContext db, GiveawayRule rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rule);

        var named = GiveawayRules.ListsIn(rule);
        if (named.Count == 0)
            return null;

        var lists = await RulesAsync(db, named, ct);

        if (named.Any(id => !lists.ContainsKey(id)))
            return "That list does not exist any more.";

        return GiveawayRules.Count(Expand(rule, lists)) > MaxExpandedRules
            ? $"With the lists they use, these rules come to more than {MaxExpandedRules} rules."
            : null;
    }

    /// <summary>
    /// What would change if this list changed: the giveaways still being run that name it,
    /// auto-invites, events that invite it, and the Discord roles it gives.
    /// </summary>
    /// <remarks>
    /// A drawn or cancelled giveaway is left out. A draw keeps its own copy of the rules with every
    /// list written out (lists design §4.2), so nothing that has happened changes with the list.
    /// </remarks>
    public static async Task<SavedListUse> UseOfAsync(ModbotContext db, Guid listId, CancellationToken ct)
    {
        var uses = await UsesAsync(db, ct);
        return uses.GetValueOrDefault(listId.ToString("D")) ?? Unused;
    }

    /// <summary>A list nothing names.</summary>
    public static SavedListUse Unused { get; } = new([], false, [], []);

    /// <summary><see cref="UseOfAsync"/> for every list at once, by id. A list nothing names is left out.</summary>
    public static async Task<Dictionary<string, SavedListUse>> UsesAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        // The trees are read and walked here rather than searched in SQL: the column is jsonb, and
        // the giveaways still being run are a handful of rows.
        var running = await db.Giveaways.AsNoTracking()
            .Where(g => g.DeletedAt == null
                && (g.State == GiveawayStates.Draft || g.State == GiveawayStates.Open || g.State == GiveawayStates.Closed))
            .OrderBy(g => g.Name)
            .Select(g => new { g.Name, g.Rules })
            .ToListAsync(ct);

        var autoInvites = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.GroupAutoInviteRules)
            .FirstOrDefaultAsync(ct);

        var giveaways = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var giveaway in running)
        {
            foreach (var id in GiveawayRules.ListsIn(GiveawayRules.ReadStored(giveaway.Rules)))
            {
                if (!giveaways.TryGetValue(id, out var names))
                    giveaways[id] = names = [];

                names.Add(giveaway.Name);
            }
        }

        var invited = GiveawayRules.ListsIn(GiveawayRules.ReadStored(autoInvites)).ToHashSet(StringComparer.Ordinal);

        // Events still being run that invite a list. A finished or cancelled one already sent its
        // invites, and an event that has not opened yet still needs the list to be there.
        var eventLists = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.InviteListId != null
                && e.DeletedAt == null
                && (e.State == CalendarEventStates.Draft || e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open))
            .OrderBy(e => e.Title)
            .Select(e => new { e.Title, ListId = e.InviteListId!.Value })
            .ToListAsync(ct);

        var events = eventLists
            .GroupBy(e => e.ListId.ToString("D"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Title).ToList(), StringComparer.Ordinal);

        // Discord roles a list gives. The live name from the server's role list where there is
        // one, else the name kept when the pairing was saved.
        var guildId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct);

        var roleRows = await db.DiscordListRoles.AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .Select(p => new
            {
                p.ListId,
                ListRules = db.SavedLists.Where(l => l.Id == p.ListId).Select(l => l.Rules).FirstOrDefault(),
                Name = db.DiscordRoles.Where(r => r.GuildId == guildId && r.RoleId == p.DiscordRoleId).Select(r => r.Name).FirstOrDefault()
                       ?? p.DiscordRoleName
                       ?? p.DiscordRoleId,
            })
            .ToListAsync(ct);

        // A list the paired list names among its rules decides who holds the role as much as the
        // paired list does, so it is in use by that role too (roles from lists design §9).
        var roles = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var row in roleRows)
        {
            var reached = GiveawayRules.ListsIn(GiveawayRules.ReadStored(row.ListRules))
                .Append(row.ListId.ToString("D"))
                .Distinct(StringComparer.Ordinal);

            foreach (var id in reached)
            {
                if (!roles.TryGetValue(id, out var names))
                    roles[id] = names = [];

                if (!names.Contains(row.Name, StringComparer.Ordinal))
                    names.Add(row.Name);
            }
        }

        return giveaways.Keys.Concat(invited).Concat(events.Keys).Concat(roles.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                id => id,
                id => new SavedListUse(
                    giveaways.GetValueOrDefault(id) ?? [],
                    invited.Contains(id),
                    events.GetValueOrDefault(id) ?? [],
                    roles.GetValueOrDefault(id) ?? []),
                StringComparer.Ordinal);
    }

    private static List<Guid>? Guids(IReadOnlyCollection<string>? ids)
    {
        if (ids is null)
            return null;

        var list = new List<Guid>(ids.Count);
        foreach (var id in ids)
        {
            if (Guid.TryParse(id, out var guid))
                list.Add(guid);
        }

        return list;
    }
}
