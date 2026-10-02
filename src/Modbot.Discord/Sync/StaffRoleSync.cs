using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Sync;

/// <summary>
/// Gives and takes Modbot roles as the staff role mappings say, and for both-ways mappings the
/// Discord roles too (staff roles from Discord design §5, §6, §3.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The plan is <see cref="StaffRoles.PlanAsync"/>'s</strong>, the same code the preview
/// runs, so what the screen listed is what happens. This class only carries it out.
/// </para>
/// <para>
/// <strong>Only after this connection has compared the member list.</strong> Between connecting and
/// the catch-up, <c>discord_member</c> holds the roles from before the bot went away, and acting on
/// them would take roles from people who were given them meanwhile.
/// </para>
/// <para>
/// <strong>The brake.</strong> A pass that would take roles from many accounts at once (see
/// <see cref="StaffRoles.Brakes"/>) takes nothing, says so once, and waits for somebody to press
/// Apply after seeing the list. It catches a Discord role deleted by mistake, a server swapped in
/// settings, or the bot losing sight of members. Giving is not braked.
/// </para>
/// <para>
/// <strong>Pace.</strong> At most <see cref="MaxChangesPerPass"/> changes a pass, a pass a minute,
/// the same shape as role sync.
/// </para>
/// </remarks>
public sealed class StaffRoleSync
{
    /// <summary>How many changes one pass makes. The next pass carries on.</summary>
    public const int MaxChangesPerPass = 50;

    /// <summary>The subject of the facts about the mappings as a whole.</summary>
    public const string Subject = "staff-roles";

    private const string Reason = "Modbot: staff role set in Modbot";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;

    public StaffRoleSync(ModbotContext db, IModbotClock clock, IFactWriter facts, EventPartitionMaintainer partitions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
    }

    /// <summary>One pass.</summary>
    /// <param name="membersRead">This connection has compared the member list. Nothing happens until it has.</param>
    /// <param name="pastBrake">Carry it out even when it would take roles from many accounts: the Apply button.</param>
    public async Task<StaffRolePass> RunAsync(IDiscordGateway? gateway, bool membersRead, bool pastBrake, CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.DiscordStaffRolesOn, s.DiscordGuildId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (settings is not { DiscordStaffRolesOn: true } || string.IsNullOrWhiteSpace(settings.DiscordGuildId))
            return StaffRolePass.Nothing;

        if (!membersRead)
            return StaffRolePass.Nothing with { Problem = pastBrake ? "The bot has not read the server's member list yet." : null };

        var guildId = settings.DiscordGuildId.Trim();
        var plan = await StaffRoles.PlanAsync(_db, null, withNotes: false, _clock.UtcNow, ct).ConfigureAwait(false);
        var losing = plan.AccountsLosing;

        // The brake holds back taking only. Giving goes ahead: a pass held for Apply must not also
        // keep new staff waiting for their role.
        var held = !pastBrake && StaffRoles.Brakes(losing, plan.Covered);
        if (held)
            await HoldAsync(losing, ct).ConfigureAwait(false);

        // Quiet bookkeeping: hand-given roles a held Discord role now gives, and both-ways states
        // where the two sides already agree.
        foreach (var (userId, roleId) in plan.Adopt)
        {
            await _db.UserRoles
                .Where(r => r.UserId == userId && r.RoleId == roleId && !r.FromDiscord)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.FromDiscord, true), ct)
                .ConfigureAwait(false);
        }

        foreach (var agreement in plan.Agree)
            await AgreeAsync(agreement.MappingId, agreement.UserId, agreement.DiscordUserId, agreement.Held, ct).ConfigureAwait(false);

        var given = 0;
        var taken = 0;
        var left = 0;
        string? problem = held ? Stopped(losing) : plan.Problems.Count > 0 ? plan.Problems[0] : null;

        // A mapping Discord refused once this pass is not asked again for anybody else: one
        // refusal, one fact, and the mapping is Not set up from here on.
        var refused = new HashSet<Guid>();

        foreach (var change in plan.Changes)
        {
            if (held && StaffRoleChangeKinds.TakesAway(change.What))
            {
                left++;
                continue;
            }

            if (given + taken >= MaxChangesPerPass)
            {
                left++;
                continue;
            }

            if (change.MappingId is { } mappingId && refused.Contains(mappingId)
                && change.What is StaffRoleChangeKinds.GiveDiscord or StaffRoleChangeKinds.TakeDiscord)
            {
                left++;
                continue;
            }

            var outcome = change.What switch
            {
                StaffRoleChangeKinds.Give or StaffRoleChangeKinds.Take => await ChangeModbotAsync(change, ct).ConfigureAwait(false),
                StaffRoleChangeKinds.GiveDiscord or StaffRoleChangeKinds.TakeDiscord => await ChangeDiscordAsync(gateway, guildId, change, refused, ct).ConfigureAwait(false),
                _ => (Done: false, Error: (string?)null),
            };

            if (outcome.Done)
            {
                if (change.What is StaffRoleChangeKinds.Give or StaffRoleChangeKinds.GiveDiscord) given++; else taken++;
            }
            else if (outcome.Error is not null)
            {
                problem = outcome.Error;
            }
        }

        if (held)
            await NoteHeldRanAsync(ct).ConfigureAwait(false);
        else
            await NoteRanAsync(problem, ct).ConfigureAwait(false);

        return new StaffRolePass(given, taken, left, held, losing, problem);
    }

    // ── The Modbot side ────────────────────────────────────────────────────────────────────

    private async Task<(bool Done, string? Error)> ChangeModbotAsync(StaffRoleChange change, CancellationToken ct)
    {
        if (change.UserId is not { } userId)
            return (false, null);

        var give = change.What == StaffRoleChangeKinds.Give;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var user = await _db.Users
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);

        // Read again inside the transaction: an account made an administrator, disabled or given
        // the role by hand since the plan was worked out is left as it now is.
        if (user is null || user.IsDisabled || user.DeletedAt is not null || user.Roles.Any(r => RoleRank.IsAdministrator(r.Role)))
            return (false, null);

        // A give is checked again too: still the Discord account the plan read, still proven, the
        // mapping still there, and the switch still on.
        if (give && !await MayStillGiveAsync(user, change, ct).ConfigureAwait(false))
            return (false, null);

        var held = user.Roles.FirstOrDefault(r => r.RoleId == change.RoleId);
        if (give == (held is not null))
            return (false, null);

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == change.RoleId, ct).ConfigureAwait(false);
        if (role is null || RoleRank.IsAdministrator(role))
            return (false, null);

        var beforeNames = user.Roles.Select(r => r.Role.Name).Order().ToList();
        var beforeIds = user.Roles.Select(r => r.RoleId).Order().ToList();

        if (give)
            user.Roles.Add(new ModbotUserRole { User = user, UserId = user.Id, Role = role, RoleId = role.Id, FromDiscord = true });
        else
            user.Roles.Remove(held!);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (user.DiscordVerifiedAt is null || string.IsNullOrEmpty(user.DiscordUserId))
        {
            // Taken because the Discord account came off: nothing agreed about it counts any more.
            await _db.DiscordStaffRoleStates.Where(st => st.UserId == user.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        else if (change.MappingId is { } mappingId && await IsBothWaysAsync(mappingId, ct).ConfigureAwait(false))
        {
            await AgreeAsync(mappingId, user.Id, user.DiscordUserId, give, ct).ConfigureAwait(false);
        }

        var afterNames = user.Roles.Select(r => r.Role.Name).Order().ToList();
        var afterIds = user.Roles.Select(r => r.RoleId).Order().ToList();

        var data = new JsonObject
        {
            ["before"] = string.Join(", ", beforeNames),
            ["after"] = string.Join(", ", afterNames),
            ["beforeRoleIds"] = new JsonArray(beforeIds.Select(r => (JsonNode?)r.ToString()).ToArray()),
            ["afterRoleIds"] = new JsonArray(afterIds.Select(r => (JsonNode?)r.ToString()).ToArray()),
            ["username"] = user.Username,
            ["why"] = "discord-role",
            ["roleName"] = role.Name,
            ["description"] = change.Why,
        };

        if (change.DiscordUserId is not null)
            data["discordUserId"] = change.DiscordUserId;

        if (change.DiscordRoleId is not null)
        {
            data["discordRoleId"] = change.DiscordRoleId;
            data["discordRoleName"] = change.DiscordRoleName;
        }

        if (change.ByHand)
            data["byHand"] = true;

        await WriteAsync(FactType.UserRolesChanged, FactPlatform.Modbot, user.Id.ToString(), data, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return (true, null);
    }

    // ── The Discord side (both ways) ───────────────────────────────────────────────────────

    private async Task<(bool Done, string? Error)> ChangeDiscordAsync(
        IDiscordGateway? gateway, string guildId, StaffRoleChange change, HashSet<Guid> refused, CancellationToken ct)
    {
        if (change.UserId is not { } userId || change.DiscordUserId is not { } discordUserId
            || change.DiscordRoleId is not { } discordRoleId || change.MappingId is not { } mappingId)
        {
            return (false, null);
        }

        if (gateway is null)
            return (false, "The Discord bot is not connected.");

        var give = change.What == StaffRoleChangeKinds.GiveDiscord;

        // The account may have been made an administrator since the plan: never touch one.
        var stillCovered = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId && !u.IsDisabled && u.DeletedAt == null && u.DiscordUserId == discordUserId && u.DiscordVerifiedAt != null)
            .Where(u => !u.Roles.Any(r => r.Role.Id == BuiltInRoles.AdministratorId || (r.Role.Permissions & ModbotPermissions.Administrator) != 0))
            .AnyAsync(ct)
            .ConfigureAwait(false);

        if (!stillCovered)
            return (false, null);

        // The mapping is still there, still both ways, and the switch still on.
        var stillMapped = await _db.DiscordStaffRoles.AsNoTracking()
            .AnyAsync(m => m.Id == mappingId && m.Direction == StaffRoleDirections.Both && m.DiscordRoleId == discordRoleId, ct)
            .ConfigureAwait(false);

        if (!stillMapped || !await SwitchOnAsync(ct).ConfigureAwait(false))
            return (false, null);

        var outcome = await gateway.ChangeRoleAsync(guildId, discordUserId, discordRoleId, give, Reason, ct).ConfigureAwait(false);

        // Gone from the server between the read and the write: the next pass sees it as Discord's
        // change and follows it.
        if (outcome.NotInServer)
            return (false, null);

        var data = new JsonObject
        {
            ["mappingId"] = mappingId.ToString(),
            ["why"] = "staff-role",
            ["roleId"] = discordRoleId,
            ["roleName"] = change.DiscordRoleName,
            ["discordUserId"] = discordUserId,
            ["modbotUserId"] = userId.ToString(),
            ["modbotRoleName"] = change.RoleName,
            ["description"] = change.Why,
        };

        if (outcome.Done)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await AgreeAsync(mappingId, userId, discordUserId, give, ct).ConfigureAwait(false);
            await SetProblemAsync(mappingId, null, refusedAt: null, ct).ConfigureAwait(false);
            await WriteAsync(give ? FactType.CopiedRoleGiven : FactType.CopiedRoleTaken, FactPlatform.Discord, discordUserId, data, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (true, null);
        }

        // Refused: the mapping is Not set up everywhere from now on (StaffRoles.Works), so it works
        // as Discord decides, its role cannot be changed by hand, and the bot does not ask again
        // until a day has passed or a role in the server has changed. Nothing is agreed.
        refused.Add(mappingId);
        var error = outcome.Error ?? "Discord refused the change.";
        var sentence = $"Not set up: Discord refused to change {change.DiscordRoleName ?? discordRoleId}: {error}";
        await SetProblemAsync(mappingId, sentence, _clock.UtcNow, ct).ConfigureAwait(false);

        data["error"] = error;
        await WriteAsync(FactType.CopyFailed, FactPlatform.Discord, discordUserId, data, ct).ConfigureAwait(false);

        return (false, sentence);
    }

    // ── Pieces ─────────────────────────────────────────────────────────────────────────────

    private Task<bool> IsBothWaysAsync(Guid mappingId, CancellationToken ct)
        => _db.DiscordStaffRoles.AsNoTracking().AnyAsync(m => m.Id == mappingId && m.Direction == StaffRoleDirections.Both, ct);

    private async Task AgreeAsync(Guid mappingId, Guid userId, string discordUserId, bool held, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        var updated = await _db.DiscordStaffRoleStates
            .Where(s => s.MappingId == mappingId && s.UserId == userId)
            .ExecuteUpdateAsync(
                u => u.SetProperty(s => s.Held, held).SetProperty(s => s.AgreedAt, now).SetProperty(s => s.DiscordUserId, discordUserId),
                ct)
            .ConfigureAwait(false);

        if (updated > 0)
            return;

        _db.DiscordStaffRoleStates.Add(new DiscordStaffRoleState
        {
            MappingId = mappingId,
            UserId = userId,
            DiscordUserId = discordUserId,
            Held = held,
            AgreedAt = now,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private Task SetProblemAsync(Guid mappingId, string? problem, DateTimeOffset? refusedAt, CancellationToken ct)
        => _db.DiscordStaffRoles
            .Where(m => m.Id == mappingId)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Problem, problem).SetProperty(m => m.RefusedAt, refusedAt), ct);

    private Task<bool> SwitchOnAsync(CancellationToken ct)
        => _db.Settings.AsNoTracking().AnyAsync(s => s.Id == 1 && s.DiscordStaffRolesOn, ct);

    /// <summary>Whether a Modbot role the plan gives may still be given now.</summary>
    private async Task<bool> MayStillGiveAsync(ModbotUser user, StaffRoleChange change, CancellationToken ct)
    {
        if (user.DiscordVerifiedAt is null || string.IsNullOrEmpty(user.DiscordUserId)
            || !string.Equals(user.DiscordUserId, change.DiscordUserId, StringComparison.Ordinal))
        {
            return false;
        }

        if (change.MappingId is not { } mappingId)
            return false;

        var mapped = await _db.DiscordStaffRoles.AsNoTracking()
            .AnyAsync(m => m.Id == mappingId && m.RoleId == change.RoleId, ct)
            .ConfigureAwait(false);

        return mapped && await SwitchOnAsync(ct).ConfigureAwait(false);
    }

    private async Task HoldAsync(int losing, CancellationToken ct)
    {
        var state = await StateAsync(ct).ConfigureAwait(false);
        var first = state.StaffRolesHeldAt is null;

        state.StaffRolesHeldAt ??= _clock.UtcNow;
        state.StaffRolesHeldCount = losing;
        state.StaffRolesRanAt = _clock.UtcNow;
        state.StaffRolesProblem = Stopped(losing);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Said once when it stops, not every minute while it waits.
        if (first)
            await WriteAsync(FactType.StaffRolesHeld, FactPlatform.Modbot, Subject, new JsonObject { ["accounts"] = losing }, ct).ConfigureAwait(false);
    }

    /// <summary>A held pass ran: the gives went ahead, the hold stays until Apply.</summary>
    private async Task NoteHeldRanAsync(CancellationToken ct)
    {
        var state = await StateAsync(ct).ConfigureAwait(false);
        state.StaffRolesRanAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task NoteRanAsync(string? problem, CancellationToken ct)
    {
        var state = await StateAsync(ct).ConfigureAwait(false);

        state.StaffRolesRanAt = _clock.UtcNow;
        state.StaffRolesProblem = problem;
        state.StaffRolesHeldAt = null;
        state.StaffRolesHeldCount = null;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<DiscordSyncState> StateAsync(CancellationToken ct)
    {
        var state = await _db.DiscordSyncState.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);

        if (state is null)
        {
            state = new DiscordSyncState { Id = 1 };
            _db.DiscordSyncState.Add(state);
        }

        return state;
    }

    private static string Stopped(int losing)
        => $"Stopped: this would take roles from {losing} accounts at once.";

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
}
