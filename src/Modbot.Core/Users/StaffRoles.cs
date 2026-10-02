using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Users;

/// <summary>One mapping as the planner reads it: saved, or proposed by a preview.</summary>
/// <param name="Id">The saved row's id; a fresh id for one only proposed.</param>
/// <param name="Direction">One of <see cref="StaffRoleDirections"/>.</param>
/// <param name="SavedAt">When the saved row last changed; null for one only proposed.</param>
/// <param name="RefusedAt">When Discord last refused this row's role (<see cref="DiscordStaffRole.RefusedAt"/>).</param>
public sealed record StaffRoleRule(
    Guid Id,
    string DiscordRoleId,
    Guid RoleId,
    string Direction,
    DateTimeOffset? SavedAt = null,
    DateTimeOffset? RefusedAt = null);

/// <summary>What a planned change does. Text, so the API and the web app read the same word.</summary>
public static class StaffRoleChangeKinds
{
    /// <summary>Give the Modbot role.</summary>
    public const string Give = "give";

    /// <summary>Take the Modbot role away.</summary>
    public const string Take = "take";

    /// <summary>Give the Discord role (both-ways mappings only).</summary>
    public const string GiveDiscord = "give-discord";

    /// <summary>Take the Discord role away (both-ways mappings only).</summary>
    public const string TakeDiscord = "take-discord";

    /// <summary>A member holds a mapped Discord role and has no Modbot account. Nothing is done.</summary>
    public const string NoAccount = "no-account";

    /// <summary>A member holds a mapped Discord role and their Modbot account has not proven it. Nothing is done.</summary>
    public const string NotProven = "not-proven";

    public static bool TakesAway(string what) => what is Take or TakeDiscord;
}

/// <summary>One change the staff role pass would make, or a person it leaves alone and why.</summary>
/// <param name="Name">The account's username, or the Discord member's name for the notes.</param>
/// <param name="ByHand">For a <see cref="StaffRoleChangeKinds.Take"/>: the role was given by hand, not by a mapping.</param>
/// <param name="Why">One plain sentence saying what made the change necessary.</param>
public sealed record StaffRoleChange(
    string What,
    Guid? UserId,
    string? Name,
    string? DiscordUserId,
    Guid RoleId,
    string RoleName,
    string? DiscordRoleId,
    string? DiscordRoleName,
    Guid? MappingId,
    bool ByHand,
    string Why);

/// <summary>Both sides of a both-ways mapping already agree for this account: write it down.</summary>
public sealed record StaffRoleAgreement(Guid MappingId, Guid UserId, string DiscordUserId, bool Held);

/// <summary>What one staff role pass would do.</summary>
/// <param name="Changes">Roles to give and take, on either side.</param>
/// <param name="Notes">Members holding a mapped Discord role whom the pass cannot reach. Only filled for a preview.</param>
/// <param name="Adopt">Roles given by hand that a held Discord role now gives: marked as from Discord, nothing else changes.</param>
/// <param name="Agree">Both-ways states to write where the two sides already agree.</param>
/// <param name="Covered">How many accounts the mappings reach.</param>
/// <param name="Problems">Mappings that cannot work as they are, as sentences.</param>
public sealed record StaffRolePlan(
    IReadOnlyList<StaffRoleChange> Changes,
    IReadOnlyList<StaffRoleChange> Notes,
    IReadOnlyList<(Guid UserId, Guid RoleId)> Adopt,
    IReadOnlyList<StaffRoleAgreement> Agree,
    int Covered,
    IReadOnlyList<string> Problems)
{
    public static StaffRolePlan Empty(string? problem = null)
        => new([], [], [], [], 0, problem is null ? [] : [problem]);

    /// <summary>How many different accounts would lose a role, on either side.</summary>
    public int AccountsLosing
        => Changes.Where(c => StaffRoleChangeKinds.TakesAway(c.What) && c.UserId is not null).Select(c => c.UserId).Distinct().Count();
}

/// <summary>
/// Works out what the staff role mappings ask for (staff roles from Discord design §4, §5, §3.1).
/// Reads, never writes: the Discord bot's pass applies the plan, and the preview shows it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Who is reached.</strong> Only enabled accounts with a proven Discord account
/// (<see cref="ModbotUser.DiscordVerifiedAt"/>) that hold no role carrying the Administrator
/// permission. Typed-in Discord ids never count here, even before <see cref="StaffDiscord.TypedIdsEnd"/>:
/// elsewhere a wrong typed id sends a message to the wrong person, here it would hand somebody
/// permissions. Administrators, and so the owner, are never given or taken anything on either side.
/// </para>
/// <para>
/// <strong>An account that no longer proves a Discord account</strong> loses every role a mapping
/// gave it, so removing the link is not a way to keep them.
/// </para>
/// <para>
/// The preview and the pass are the same code, so what the screen lists before a mapping is saved
/// is what the pass will do.
/// </para>
/// </remarks>
public static class StaffRoles
{
    /// <summary>The brake: a pass taking roles from more than this many accounts at once stops.</summary>
    public const int BrakeAccounts = 5;

    /// <summary>The brake never stops a pass taking roles from fewer than this many accounts.</summary>
    public const int BrakeFloor = 3;

    /// <summary>
    /// Whether a pass that would take roles from <paramref name="losing"/> of <paramref name="covered"/>
    /// accounts stops and waits for somebody to press Apply (design §6).
    /// </summary>
    public static bool Brakes(int losing, int covered)
        => losing >= BrakeFloor && (losing > BrakeAccounts || losing * 2 > covered);

    /// <summary>Whether a Discord role can be mapped at all: not @everyone, not owned by a bot, not gone.</summary>
    public static bool CanMap(DiscordRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return !role.Everyone && !role.Managed && role.RemovedAt is null;
    }

    /// <summary>Whether the bot may assign this Discord role, as the server index last read it.</summary>
    public static bool BotCanGive(DiscordRole? role)
        => role is { BotCanAssign: true, RemovedAt: null };

    /// <summary>How long after Discord refused a both-ways role the bot asks again, when nothing in the server changed.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(1);

    /// <summary>
    /// Whether a both-ways mapping works: the bot may assign its Discord role, the role carries no
    /// power over the server as last read, and Discord has not refused it lately (design §3.1). Otherwise it is Not set up, everywhere: it works as Discord
    /// decides and its role cannot be changed by hand.
    /// </summary>
    /// <param name="rolesChangedAt">
    /// The latest change to any role in the server. A refusal stands until a day has passed or a
    /// role has changed since, which is what fixing the bot's permissions or role order looks like.
    /// </param>
    public static bool Works(StaffRoleRule rule, DiscordRole? role, DateTimeOffset? rolesChangedAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);

        // A Discord role that has come to carry power over the server since it was linked is never
        // handed out again, whatever the save allowed then.
        if (rule.Direction != StaffRoleDirections.Both || !BotCanGive(role) || IsPowerful(role!))
            return false;

        if (rule.RefusedAt is not { } refused)
            return true;

        return now - refused >= RetryAfter || rolesChangedAt > refused;
    }

    /// <summary>The Discord permissions a both-ways role may not carry: Modbot must never hand those out.</summary>
    /// <remarks>Administrator, Kick Members, Ban Members, Manage Server and Manage Roles, as Discord numbers them.</remarks>
    public const long PowerfulDiscordPermissions = (1L << 3) | (1L << 1) | (1L << 2) | (1L << 5) | (1L << 28);

    /// <summary>Whether a Discord role carries a permission Modbot must never hand out, or is not read yet.</summary>
    public static bool IsPowerful(DiscordRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return role.Permissions is not { } permissions || (permissions & PowerfulDiscordPermissions) != 0;
    }
    /// <summary>The advisory lock every save of a linked role or a VRChat role pair takes.</summary>
    public const string SaveLock = "modbot.staff-roles.save";

    /// <summary>
    /// One save at a time of a linked role or a VRChat role pair, until the transaction ends. The
    /// checks between them -- a both-ways row alone for its Modbot role, beside no one-way row, and
    /// never on a Discord role a pair writes -- read other rows, so two saves at once could each
    /// pass them; under this lock the second one reads what the first wrote.
    /// </summary>
    public static Task LockSavesAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({SaveLock}))", ct);
    }

    /// <summary>The saved mappings, as the planner reads them.</summary>
    public static async Task<IReadOnlyList<StaffRoleRule>> RulesAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        return await db.DiscordStaffRoles.AsNoTracking()
            .OrderBy(m => m.CreatedAt)
            .Select(m => new StaffRoleRule(m.Id, m.DiscordRoleId, m.RoleId, m.Direction, m.UpdatedAt, m.RefusedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// For each account the mappings reach, the roles that follow Discord and so cannot be given or
    /// taken by hand: every mapped role except those behind a both-ways mapping the bot can give.
    /// Empty while the switch is off, because then nothing would undo a change by hand.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> FollowingDiscordAsync(
        ModbotContext db, IReadOnlyCollection<ModbotUser> users, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);

        var none = new Dictionary<Guid, IReadOnlySet<Guid>>();

        var settings = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.DiscordStaffRolesOn, s.DiscordGuildId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (settings is not { DiscordStaffRolesOn: true } || string.IsNullOrWhiteSpace(settings.DiscordGuildId))
            return none;

        var rules = await RulesAsync(db, ct).ConfigureAwait(false);
        if (rules.Count == 0)
            return none;

        var discordRoles = await DiscordRolesAsync(db, settings.DiscordGuildId, ct).ConfigureAwait(false);
        var rolesChangedAt = LatestChange(discordRoles);

        var locked = rules
            .GroupBy(r => r.RoleId)
            .Where(g => !g.Any(r => Works(r, discordRoles.GetValueOrDefault(r.DiscordRoleId), rolesChangedAt, now)))
            .Select(g => g.Key)
            .ToHashSet();

        if (locked.Count == 0)
            return none;

        var result = new Dictionary<Guid, IReadOnlySet<Guid>>();
        foreach (var user in users)
        {
            if (IsCovered(user))
                result[user.Id] = locked;
        }

        return result;
    }

    /// <summary>
    /// Whether the mappings reach this account: enabled, not deleted, a proven Discord account, and
    /// no role carrying the Administrator permission. Its roles must be loaded.
    /// </summary>
    public static bool IsCovered(ModbotUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.DeletedAt is null
               && !user.IsDisabled
               && user.DiscordVerifiedAt is not null
               && !string.IsNullOrEmpty(user.DiscordUserId)
               && !user.Roles.Any(r => RoleRank.IsAdministrator(r.Role));
    }

    /// <summary>
    /// What the mappings ask for right now.
    /// </summary>
    /// <param name="proposed">The mappings as they would be after a save, for a preview; null reads the saved ones.</param>
    /// <param name="withNotes">Also list the Discord members the pass cannot reach. Reads every member, so the preview only.</param>
    public static async Task<StaffRolePlan> PlanAsync(
        ModbotContext db, IReadOnlyList<StaffRoleRule>? proposed, bool withNotes, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var guildId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(guildId))
            return StaffRolePlan.Empty("No Discord server is set.");

        var rules = proposed ?? await RulesAsync(db, ct).ConfigureAwait(false);
        if (rules.Count == 0)
            return StaffRolePlan.Empty();

        var roleIds = rules.Select(r => r.RoleId).Distinct().ToList();
        var roles = await db.Roles.AsNoTracking()
            .Where(r => roleIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, ct)
            .ConfigureAwait(false);

        var discordRoles = await DiscordRolesAsync(db, guildId, ct).ConfigureAwait(false);
        var rolesChangedAt = LatestChange(discordRoles);
        var problems = new List<string>();

        // Each Modbot role with the mappings that give it. A role that has come to carry the
        // Administrator permission since it was mapped is skipped and named, never given.
        var byRole = new List<(ModbotRole Role, List<StaffRoleRule> Rules, StaffRoleRule? Both, bool BothWorks)>();
        foreach (var group in rules.GroupBy(r => r.RoleId))
        {
            if (!roles.TryGetValue(group.Key, out var role))
                continue;

            if (RoleRank.IsAdministrator(role))
            {
                problems.Add($"{role.Name} carries the Administrator permission, so no Discord role can give it.");
                continue;
            }

            var usable = group
                .Where(r => !discordRoles.TryGetValue(r.DiscordRoleId, out var d) || (!d.Everyone && !d.Managed))
                .ToList();

            if (usable.Count == 0)
                continue;

            var both = usable.FirstOrDefault(r => r.Direction == StaffRoleDirections.Both);
            var works = both is not null && Works(both, discordRoles.GetValueOrDefault(both.DiscordRoleId), rolesChangedAt, now);

            if (both is not null && !works)
                problems.Add($"Not set up: the bot cannot give {DiscordName(both.DiscordRoleId, discordRoles)} both ways.");

            byRole.Add((role, usable, both, works));
        }

        if (byRole.Count == 0)
            return new StaffRolePlan([], [], [], [], 0, problems);

        var accounts = await db.Users.AsNoTracking()
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .Where(u => u.DeletedAt == null && !u.IsDisabled)
            .OrderBy(u => u.Username)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var provenIds = accounts
            .Where(u => u.DiscordVerifiedAt is not null && !string.IsNullOrEmpty(u.DiscordUserId))
            .Select(u => u.DiscordUserId!)
            .ToList();

        var members = await db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && (withNotes ? m.LeftAt == null || provenIds.Contains(m.UserId) : provenIds.Contains(m.UserId)))
            .Select(m => new Member(m.UserId, m.DisplayName, m.Roles, m.LeftAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var memberById = members.ToDictionary(m => m.UserId, StringComparer.Ordinal);

        var bothIds = byRole.Where(r => r.BothWorks).Select(r => r.Both!.Id).ToList();
        var states = bothIds.Count == 0
            ? []
            : await db.DiscordStaffRoleStates.AsNoTracking()
                .Where(s => bothIds.Contains(s.MappingId))
                .ToDictionaryAsync(s => (s.MappingId, s.UserId), ct)
                .ConfigureAwait(false);

        var changes = new List<StaffRoleChange>();
        var adopt = new List<(Guid, Guid)>();
        var agree = new List<StaffRoleAgreement>();
        var covered = 0;

        foreach (var user in accounts)
        {
            if (user.Roles.Any(r => RoleRank.IsAdministrator(r.Role)))
                continue;

            var heldRoles = user.Roles.ToDictionary(r => r.RoleId);

            if (user.DiscordVerifiedAt is null || string.IsNullOrEmpty(user.DiscordUserId))
            {
                // No proven Discord account: what a mapping gave goes with the link.
                foreach (var (role, _, _, _) in byRole)
                {
                    if (heldRoles.TryGetValue(role.Id, out var held) && held.FromDiscord)
                    {
                        changes.Add(new StaffRoleChange(
                            StaffRoleChangeKinds.Take, user.Id, user.Username, user.DiscordUserId, role.Id, role.Name,
                            null, null, null, false, "Their Discord account is no longer connected."));
                    }
                }

                continue;
            }

            covered++;

            var member = memberById.GetValueOrDefault(user.DiscordUserId);
            var inServer = member is { LeftAt: null };
            var discordHeldIds = inServer ? Live(Ids(member!.Roles), discordRoles) : [];

            foreach (var (role, roleRules, both, bothWorks) in byRole)
            {
                var holding = roleRules.Where(r => discordHeldIds.Contains(r.DiscordRoleId)).ToList();
                var discordHeld = holding.Count > 0;
                heldRoles.TryGetValue(role.Id, out var heldRole);
                var modbotHeld = heldRole is not null;

                if (bothWorks)
                {
                    var discordRoleName = DiscordName(both!.DiscordRoleId, discordRoles);

                    // Not in the server: holding no Discord role, whatever was agreed, and Discord
                    // decides. The Modbot role goes, and nothing is ever given in a server the
                    // person is not in.
                    if (!inServer)
                    {
                        if (modbotHeld)
                        {
                            changes.Add(new StaffRoleChange(
                                StaffRoleChangeKinds.Take, user.Id, user.Username, user.DiscordUserId, role.Id, role.Name,
                                both.DiscordRoleId, discordRoleName, both.Id, !heldRole!.FromDiscord,
                                "They are not in the Discord server."));
                        }

                        continue;
                    }

                    // With nothing agreed, Discord decides when the account proved its Discord
                    // account after the mapping was saved: a new or re-proven Discord account is not
                    // a reason to hand it the Discord role.
                    var discordDecides = both.SavedAt is { } saved && user.DiscordVerifiedAt > saved;

                    var plan = BothWays(
                        states.GetValueOrDefault((both.Id, user.Id)),
                        user.DiscordUserId,
                        discordDecides,
                        discordHeld,
                        modbotHeld);

                    switch (plan)
                    {
                        case BothWaysStep.Agree:
                            agree.Add(new StaffRoleAgreement(both.Id, user.Id, user.DiscordUserId, discordHeld));
                            break;

                        case BothWaysStep.FollowDiscord:
                            changes.Add(new StaffRoleChange(
                                discordHeld ? StaffRoleChangeKinds.Give : StaffRoleChangeKinds.Take,
                                user.Id, user.Username, user.DiscordUserId, role.Id, role.Name,
                                both.DiscordRoleId, discordRoleName, both.Id, false,
                                discordHeld
                                    ? $"They hold {discordRoleName} in Discord."
                                    : $"{discordRoleName} was taken away in Discord."));
                            break;

                        case BothWaysStep.FollowModbot:
                            changes.Add(new StaffRoleChange(
                                modbotHeld ? StaffRoleChangeKinds.GiveDiscord : StaffRoleChangeKinds.TakeDiscord,
                                user.Id, user.Username, user.DiscordUserId, role.Id, role.Name,
                                both.DiscordRoleId, discordRoleName, both.Id, false,
                                modbotHeld ? $"They hold {role.Name} in Modbot." : $"{role.Name} was taken away in Modbot."));
                            break;
                    }

                    continue;
                }

                if (discordHeld && !modbotHeld)
                {
                    changes.Add(new StaffRoleChange(
                        StaffRoleChangeKinds.Give, user.Id, user.Username, user.DiscordUserId, role.Id, role.Name,
                        holding[0].DiscordRoleId, DiscordName(holding[0].DiscordRoleId, discordRoles), holding[0].Id, false,
                        $"They hold {DiscordName(holding[0].DiscordRoleId, discordRoles)} in Discord."));
                }
                else if (discordHeld && !heldRole!.FromDiscord)
                {
                    adopt.Add((user.Id, role.Id));
                }
                else if (!discordHeld && modbotHeld)
                {
                    var names = string.Join(" or ", roleRules.Select(r => DiscordName(r.DiscordRoleId, discordRoles)));
                    var byHand = !heldRole!.FromDiscord;

                    changes.Add(new StaffRoleChange(
                        StaffRoleChangeKinds.Take, user.Id, user.Username, user.DiscordUserId, role.Id, role.Name,
                        null, null, null, byHand,
                        !inServer
                            ? "They are not in the Discord server."
                            : byHand ? $"Given by hand, and they do not hold {names} in Discord." : $"They no longer hold {names} in Discord."));
                }
            }
        }

        var notes = withNotes ? Notes(byRole.SelectMany(r => r.Rules).ToList(), roles, discordRoles, accounts, members) : [];

        return new StaffRolePlan(changes, notes, adopt, agree, covered, problems);
    }

    // ── Both ways ──────────────────────────────────────────────────────────────────────────

    /// <summary>What a both-ways mapping does for one account.</summary>
    public enum BothWaysStep
    {
        Nothing,
        Agree,
        FollowDiscord,
        FollowModbot,
    }

    /// <summary>
    /// Which side a both-ways mapping copies for one account (design §3.1).
    /// </summary>
    /// <param name="state">What the two sides last agreed on, or null when nothing has been agreed yet.</param>
    /// <param name="discordUserId">The Discord account the account proves now. An agreement about another one does not count.</param>
    /// <param name="discordDecidesWithoutAgreement">
    /// With nothing agreed, follow Discord rather than give the role to whichever side lacks it: the
    /// account's Discord account is new since the mapping was saved. (Somebody not in the server
    /// never reaches here: they hold no Discord role and Discord decides.)
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Held roles are compared, not times.</strong> Each side is compared with what was
    /// agreed. The row's change time is not used: voice and name updates move it too, and a stamp
    /// taken from the clock when the agreement is written would hide a Discord change that landed
    /// between the plan's read and that write. What stops the bot's own change bouncing back is that
    /// the pass writes that change into the stored member row as it makes it, so the row already
    /// agrees before Discord's update arrives.
    /// </para>
    /// <para>Pure, so the table in the design is tested as a table.</para>
    /// </remarks>
    public static BothWaysStep BothWays(
        DiscordStaffRoleState? state,
        string discordUserId,
        bool discordDecidesWithoutAgreement,
        bool discordHeld,
        bool modbotHeld)
    {
        if (state is not null && !string.Equals(state.DiscordUserId, discordUserId, StringComparison.Ordinal))
            state = null;

        if (state is null)
        {
            // The first time: both agree, or whichever side holds it gives it to the other, unless
            // Discord decides for want of anything agreed.
            if (discordHeld == modbotHeld)
                return BothWaysStep.Agree;

            if (discordDecidesWithoutAgreement)
                return BothWaysStep.FollowDiscord;

            return discordHeld ? BothWaysStep.FollowDiscord : BothWaysStep.FollowModbot;
        }

        var discordChanged = discordHeld != state.Held;
        var modbotChanged = modbotHeld != state.Held;

        if (!discordChanged && !modbotChanged)
            return BothWaysStep.Nothing;

        // Both moved away from the agreement: with one yes-or-no each, they now say the same.
        if (discordChanged && modbotChanged)
            return BothWaysStep.Agree;

        return discordChanged ? BothWaysStep.FollowDiscord : BothWaysStep.FollowModbot;
    }

    // ── Pieces ─────────────────────────────────────────────────────────────────────────────

    private static List<StaffRoleChange> Notes(
        IReadOnlyList<StaffRoleRule> rules,
        IReadOnlyDictionary<Guid, ModbotRole> roles,
        IReadOnlyDictionary<string, DiscordRole> discordRoles,
        IReadOnlyList<ModbotUser> accounts,
        IReadOnlyList<Member> members)
    {
        var proven = accounts
            .Where(u => u.DiscordVerifiedAt is not null && !string.IsNullOrEmpty(u.DiscordUserId))
            .Select(u => u.DiscordUserId!)
            .ToHashSet(StringComparer.Ordinal);

        var typed = accounts
            .Where(u => u.DiscordVerifiedAt is null && !string.IsNullOrEmpty(u.DiscordUserId))
            .GroupBy(u => u.DiscordUserId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var notes = new List<StaffRoleChange>();

        foreach (var member in members.Where(m => m.LeftAt is null && !proven.Contains(m.UserId)).OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var held = Live(Ids(member.Roles), discordRoles);
            var rule = rules.FirstOrDefault(r => held.Contains(r.DiscordRoleId));
            if (rule is null || !roles.TryGetValue(rule.RoleId, out var role))
                continue;

            var discordRoleName = DiscordName(rule.DiscordRoleId, discordRoles);

            if (typed.TryGetValue(member.UserId, out var account))
            {
                notes.Add(new StaffRoleChange(
                    StaffRoleChangeKinds.NotProven, account.Id, account.Username, member.UserId, role.Id, role.Name,
                    rule.DiscordRoleId, discordRoleName, rule.Id, false,
                    $"Holds {discordRoleName}, and their Modbot account has not connected Discord."));
            }
            else
            {
                notes.Add(new StaffRoleChange(
                    StaffRoleChangeKinds.NoAccount, null, member.DisplayName, member.UserId, role.Id, role.Name,
                    rule.DiscordRoleId, discordRoleName, rule.Id, false,
                    $"Holds {discordRoleName}, and has no Modbot account."));
            }
        }

        return notes;
    }

    private static DateTimeOffset? LatestChange(Dictionary<string, DiscordRole> roles)
        => roles.Count == 0 ? null : roles.Values.Max(r => r.UpdatedAt);

    private static Task<Dictionary<string, DiscordRole>> DiscordRolesAsync(ModbotContext db, string guildId, CancellationToken ct)
        => db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId)
            .ToDictionaryAsync(r => r.RoleId, StringComparer.Ordinal, ct);

    private static string DiscordName(string roleId, IReadOnlyDictionary<string, DiscordRole> roles)
        => roles.TryGetValue(roleId, out var role) && role.Name.Length > 0 ? role.Name : roleId;

    /// <summary>
    /// The held role ids less those the server index has found gone from Discord. A member row
    /// keeps a deleted role's id until that member's next update, and a role that no longer exists
    /// gives nobody anything: deleting a linked Discord role takes its Modbot role from everybody who
    /// held it, on the next pass (brake permitting).
    /// </summary>
    private static HashSet<string> Live(HashSet<string> ids, IReadOnlyDictionary<string, DiscordRole> roles)
    {
        ids.RemoveWhere(id => roles.TryGetValue(id, out var role) && role.RemovedAt is not null);
        return ids;
    }

    private static HashSet<string> Ids(string json)
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

    private sealed record Member(string UserId, string DisplayName, string Roles, DateTimeOffset? LeftAt);
}
