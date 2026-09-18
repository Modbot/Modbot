using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Moderation;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Sync;

/// <summary>
/// Keeps paired VRChat group roles and Discord roles in step (M5 §3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A pass compares both sides rather than following events.</strong> Each pair names one
/// VRChat role, one Discord role, and which side decides; the deciding side is read as the truth
/// and the other is made to match. Comparing state is what makes this safe to run again and again:
/// a pass that has already done its work finds nothing to do, a change Modbot itself made is
/// already agreed with by the time the next pass looks, and there is no circle for a role change
/// to travel round. That is why role sync needs no loop check of its own while ban sync does.
/// </para>
/// <para>
/// <strong>Nobody unpaired is touched, and nobody unlinked is touched</strong> (§3.2). Only roles
/// somebody has explicitly paired here are looked at — never "everything except" — and only people
/// whose Discord and VRChat accounts have been proved to be the same person. Most members of a
/// Discord server have no VRChat account tied to them, and every one of them is left exactly as
/// they are.
/// </para>
/// <para>
/// <strong>Somebody has to be on both sides to be mirrored.</strong> A linked person who has left
/// the group, or left the server, is skipped: a role cannot be mirrored onto a platform the person
/// is not on, and asking VRChat to give a group role to somebody who is not a member only earns a
/// refusal per pass forever.
/// </para>
/// <para>
/// <strong>Pace.</strong> At most <see cref="MaxChangesPerPass"/> changes a pass, and a pass a
/// minute — the same shape the linked-member roles job already uses, for the same reason: Discord
/// queues the bot's requests behind one another, so a sync that fired a thousand of them would
/// stall every other thing the bot does. The VRChat half is paced again by the gate at background
/// priority, so a moderator pressing Ban never waits behind it.
/// </para>
/// </remarks>
public sealed class RoleSync
{
    /// <summary>How many role changes one pass makes. The next pass carries on.</summary>
    public const int MaxChangesPerPass = 50;

    /// <summary>How many changes a dry run describes before it stops listing and only counts.</summary>
    public const int MaxListed = 500;

    /// <summary>
    /// How long before a pair that nobody decides says again that the two sides disagree.
    /// </summary>
    /// <remarks>
    /// A pair set to "nobody" changes nothing, so the disagreement it found is still there on the
    /// next pass and on every pass after that. Saying so once a minute forever would bury the log
    /// it is trying to be useful in.
    /// </remarks>
    public static readonly TimeSpan SaysAgainAfter = TimeSpan.FromDays(7);

    private const string Reason = "Modbot: keeping roles in step across platforms";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly CopyRecords _copies;
    private readonly IVRChatModerationActions _vrchat;

    public RoleSync(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        CopyRecords copies,
        IVRChatModerationActions vrchat)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(vrchat);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _copies = copies;
        _vrchat = vrchat;
    }

    /// <summary>
    /// Works out every difference the pairs cover, and — unless this is a dry run — fixes as many
    /// as one pass may.
    /// </summary>
    /// <param name="apply">
    /// False looks and changes nothing at all: no request to either platform, no fact, no copy
    /// record. That is the mode an operator switches a sync on from (§7), and it is the same code
    /// that does the work, so what it lists is what would happen.
    /// </param>
    public async Task<RoleSyncPass> RunAsync(IDiscordGateway? gateway, bool apply, CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.DiscordGuildId, s.ManagedGroupId, s.DiscordRoleSyncOn })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (Blank(settings?.DiscordGuildId) is not { } guildId || Blank(settings?.ManagedGroupId) is not { } groupId)
            return Empty();

        // The switch stops the job from acting; a dry run answers whatever the switch says, because
        // seeing what would happen is how somebody decides whether to turn it on.
        if (apply && !settings!.DiscordRoleSyncOn)
            return Empty();

        var pairs = await _db.DiscordRolePairs.AsNoTracking()
            .Where(p => p.Enabled)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (pairs.Count == 0)
            return Empty();

        var discordRoles = await _db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId)
            .ToDictionaryAsync(r => r.RoleId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        // Everybody whose two accounts are proved to be the same person, who is still in the group
        // and still in the server. Everyone else is left alone -- which is nearly everyone.
        var people = await _db.DiscordAccountLinks.AsNoTracking()
            .Where(l => l.UnlinkedAt == null)
            .Join(
                _db.GroupMembers.AsNoTracking().Where(m => m.GroupId == groupId && m.LeftAt == null),
                l => l.VRChatUserId,
                m => m.UserId,
                (l, m) => new { Link = l, GroupRoles = m.Roles })
            .Join(
                _db.DiscordMembers.AsNoTracking().Where(m => m.GuildId == guildId && m.LeftAt == null),
                x => x.Link.DiscordUserId,
                m => m.UserId,
                (x, m) => new
                {
                    x.Link.VRChatUserId,
                    x.Link.DiscordUserId,
                    x.Link.VRChatDisplayName,
                    DiscordName = m.DisplayName,
                    x.GroupRoles,
                    DiscordRoles = m.Roles,
                })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var saidRecently = await SaidRecentlyAsync(ct).ConfigureAwait(false);

        var changes = new List<PlannedChange>();
        var tally = new Tally();

        foreach (var person in people)
        {
            var heldInGroup = Ids(person.GroupRoles);
            var heldInServer = Ids(person.DiscordRoles);
            var name = person.VRChatDisplayName ?? person.DiscordName;

            foreach (var pair in pairs)
            {
                var inGroup = heldInGroup.Contains(pair.VRChatRoleId);
                var inServer = heldInServer.Contains(pair.DiscordRoleId);

                if (inGroup == inServer)
                    continue;

                if (pair.Decides == RoleSyncDecides.Nobody)
                {
                    tally.Disagreed++;

                    Add(changes, new PlannedChange(
                        "disagree",
                        SyncPlatforms.Discord,
                        person.VRChatUserId,
                        person.DiscordUserId,
                        name,
                        NameOf(pair, discordRoles),
                        inGroup
                            ? "Held in the group but not in Discord, and nobody decides this pair."
                            : "Held in Discord but not in the group, and nobody decides this pair."));

                    if (apply && !saidRecently.Contains((person.VRChatUserId, pair.Id)))
                        await SayTheyDisagreeAsync(pair, person.VRChatUserId, person.DiscordUserId, inGroup, ct).ConfigureAwait(false);

                    continue;
                }

                var toDiscord = pair.Decides == RoleSyncDecides.VRChat;
                var give = toDiscord ? inGroup : inServer;

                Add(changes, new PlannedChange(
                    give ? CopyKinds.RoleGiven : CopyKinds.RoleTaken,
                    toDiscord ? SyncPlatforms.Discord : SyncPlatforms.VRChat,
                    person.VRChatUserId,
                    person.DiscordUserId,
                    name,
                    toDiscord ? NameOf(pair, discordRoles) : pair.VRChatRoleName,
                    Why(pair.Decides, give)));

                if (!apply)
                    continue;

                if (tally.Given + tally.Taken >= MaxChangesPerPass)
                {
                    tally.Left++;
                    continue;
                }

                await ChangeAsync(gateway, guildId, pair, person.VRChatUserId, person.DiscordUserId, toDiscord, give, discordRoles, tally, ct)
                    .ConfigureAwait(false);
            }
        }

        if (apply)
            await NoteRanAsync(tally.Problem, ct).ConfigureAwait(false);

        return new RoleSyncPass(tally.Given, tally.Taken, tally.Disagreed, tally.Left, tally.Problem, changes);
    }

    // ── One change ─────────────────────────────────────────────────────────────────────────

    private async Task ChangeAsync(
        IDiscordGateway? gateway,
        string guildId,
        DiscordRolePair pair,
        string vrchatUserId,
        string discordUserId,
        bool toDiscord,
        bool give,
        IReadOnlyDictionary<string, DiscordRole> discordRoles,
        Tally tally,
        CancellationToken ct)
    {
        var kind = give ? CopyKinds.RoleGiven : CopyKinds.RoleTaken;
        var roleId = toDiscord ? pair.DiscordRoleId : pair.VRChatRoleId;

        bool done;
        string? error;

        if (toDiscord)
        {
            if (gateway is null)
            {
                tally.Problem = "The Discord bot is not connected.";
                tally.Left++;
                return;
            }

            // A role the bot cannot assign is a setup problem, named here rather than discovered
            // as one refusal per member per pass (M5 §3.3).
            if (discordRoles.TryGetValue(pair.DiscordRoleId, out var role) && !role.BotCanAssign)
            {
                var cannot = $"The bot cannot assign {role.Name}. Give it Manage Roles and keep its own role above that one.";
                await SetPairProblemAsync(pair.Id, cannot, ct).ConfigureAwait(false);
                tally.Problem = cannot;
                tally.Left++;
                return;
            }

            var outcome = await gateway.ChangeRoleAsync(guildId, discordUserId, pair.DiscordRoleId, give, Reason, ct)
                .ConfigureAwait(false);

            // Somebody who left the server between the read and the write is not a failure worth
            // reporting: the next pass will not see them at all.
            if (outcome.NotInServer)
                return;

            done = outcome.Done;
            error = outcome.Error;
        }
        else
        {
            var outcome = give
                ? await _vrchat.GiveGroupRoleAsync(vrchatUserId, pair.VRChatRoleId, ct).ConfigureAwait(false)
                : await _vrchat.TakeGroupRoleAsync(vrchatUserId, pair.VRChatRoleId, ct).ConfigureAwait(false);

            done = outcome.Done;
            error = outcome.Error;
        }

        await _copies.RecordAsync(
                toDiscord ? CopyDirections.ToDiscord : CopyDirections.ToVRChat,
                kind,
                toDiscord ? discordUserId : vrchatUserId,
                toDiscord ? vrchatUserId : discordUserId,
                roleId,
                done,
                error,
                ct)
            .ConfigureAwait(false);

        var data = new JsonObject
        {
            ["pairId"] = pair.Id.ToString(),
            ["decides"] = pair.Decides,
            ["roleId"] = roleId,
            ["roleName"] = toDiscord ? NameOf(pair, discordRoles) : pair.VRChatRoleName,
            ["vrchatUserId"] = vrchatUserId,
            ["discordUserId"] = discordUserId,
            ["vrchatRoleId"] = pair.VRChatRoleId,
            ["discordRoleId"] = pair.DiscordRoleId,
            ["description"] = Why(pair.Decides, give),
        };

        if (done)
        {
            if (give) tally.Given++; else tally.Taken++;

            await SetPairProblemAsync(pair.Id, null, ct).ConfigureAwait(false);
            await WriteAsync(
                    give ? FactType.CopiedRoleGiven : FactType.CopiedRoleTaken,
                    toDiscord ? FactPlatform.Discord : FactPlatform.VRChat,
                    toDiscord ? discordUserId : vrchatUserId,
                    data,
                    ct)
                .ConfigureAwait(false);
        }
        else
        {
            tally.Problem = error;
            await SetPairProblemAsync(pair.Id, error, ct).ConfigureAwait(false);

            data["error"] = error;
            await WriteAsync(
                    FactType.CopyFailed,
                    toDiscord ? FactPlatform.Discord : FactPlatform.VRChat,
                    toDiscord ? discordUserId : vrchatUserId,
                    data,
                    ct)
                .ConfigureAwait(false);
        }
    }

    // ── Pieces ─────────────────────────────────────────────────────────────────────────────

    private Task SayTheyDisagreeAsync(
        DiscordRolePair pair, string vrchatUserId, string discordUserId, bool inGroup, CancellationToken ct)
        => WriteAsync(FactType.RolesDisagree, FactPlatform.VRChat, vrchatUserId, new JsonObject
        {
            ["pairId"] = pair.Id.ToString(),
            ["vrchatRoleId"] = pair.VRChatRoleId,
            ["discordRoleId"] = pair.DiscordRoleId,
            ["discordUserId"] = discordUserId,
            ["heldIn"] = inGroup ? SyncPlatforms.VRChat : SyncPlatforms.Discord,
        }, ct);

    /// <summary>
    /// Who has already been told about lately, so a pair nobody decides does not repeat itself
    /// every minute forever. One query for the whole pass.
    /// </summary>
    private async Task<HashSet<(string, Guid)>> SaidRecentlyAsync(CancellationToken ct)
    {
        var since = _clock.UtcNow - SaysAgainAfter;

        var rows = await _db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.RolesDisagree && e.OccurredAt >= since)
            .Select(e => new { e.SubjectId, e.Data })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var said = new HashSet<(string, Guid)>();

        foreach (var row in rows)
        {
            if (row.Data is null)
                continue;

            try
            {
                using var document = JsonDocument.Parse(row.Data);

                if (document.RootElement.TryGetProperty("pairId", out var pairId)
                    && Guid.TryParse(pairId.GetString(), out var id))
                {
                    said.Add((row.SubjectId, id));
                }
            }
            catch (JsonException)
            {
                // A payload Modbot cannot read is not a reason to stop a pass.
            }
        }

        return said;
    }

    private Task SetPairProblemAsync(Guid pairId, string? problem, CancellationToken ct)
        => _db.DiscordRolePairs
            .Where(p => p.Id == pairId && p.Problem != problem)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Problem, problem), ct);

    private async Task NoteRanAsync(string? problem, CancellationToken ct)
    {
        var state = await _db.DiscordSyncState.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);

        if (state is null)
        {
            state = new DiscordSyncState { Id = 1 };
            _db.DiscordSyncState.Add(state);
        }

        state.RolesRanAt = _clock.UtcNow;
        state.RolesProblem = problem;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(string type, FactPlatform platform, string subjectId, JsonObject data, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        await _facts.WriteAsync(new FactRecord
        {
            Type = type,
            OccurredAt = now,
            SubjectPlatform = platform,
            SubjectId = subjectId,
            Source = FactSource.Modbot,
            Data = data,
        }, ct).ConfigureAwait(false);
    }

    private static string Why(string decides, bool give) => (decides, give) switch
    {
        (RoleSyncDecides.VRChat, true) => "They hold the group role, and VRChat decides this pair.",
        (RoleSyncDecides.VRChat, false) => "They do not hold the group role, and VRChat decides this pair.",
        (RoleSyncDecides.Discord, true) => "They hold the Discord role, and Discord decides this pair.",
        _ => "They do not hold the Discord role, and Discord decides this pair.",
    };

    private static string? NameOf(DiscordRolePair pair, IReadOnlyDictionary<string, DiscordRole> roles)
        => roles.TryGetValue(pair.DiscordRoleId, out var role) && role.Name.Length > 0 ? role.Name : pair.DiscordRoleName;

    private static void Add(List<PlannedChange> changes, PlannedChange change)
    {
        if (changes.Count < MaxListed)
            changes.Add(change);
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

    private static RoleSyncPass Empty() => new(0, 0, 0, 0, null, []);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class Tally
    {
        public int Given;
        public int Taken;
        public int Disagreed;
        public int Left;
        public string? Problem;
    }
}
