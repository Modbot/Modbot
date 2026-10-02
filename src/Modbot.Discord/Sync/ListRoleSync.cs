using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Lists;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Sync;

/// <summary>What one roles from lists pass did.</summary>
/// <param name="Left">Changes this pass did not get to. The next pass carries on.</param>
/// <param name="Stopped">Pairings stopped by the brake, waiting for Apply.</param>
/// <param name="Problem">The last thing that went wrong, as a sentence, or null.</param>
public sealed record ListRolePass(int Given, int Taken, int Left, int Stopped, string? Problem)
{
    public static ListRolePass Nothing { get; } = new(0, 0, 0, 0, null);
}

/// <summary>
/// Gives and takes the Discord roles saved lists are paired with (roles from lists design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The plan is <see cref="ListRolePlanner"/>'s</strong>, the same code the preview runs, so
/// what the screen listed is what happens. This class only carries it out, and writes down each role
/// it gave so that it is the only role it ever takes away (design §3).
/// </para>
/// <para>
/// <strong>The switch.</strong> Off, the pass does nothing at all. Each pairing has its own On too.
/// </para>
/// <para>
/// <strong>The brake.</strong> A pairing whose pass would take its role from many people at once
/// (<see cref="Core.Discord.ListRoleChecks.Brakes"/>) gives and takes nothing, says so once, and waits
/// until somebody looks at the list and presses Apply, which allows that many removals. It catches a
/// list whose rules were changed or broken, a retention change, a member list gone missing.
/// </para>
/// <para>
/// <strong>Pace.</strong> At most <see cref="MaxChangesPerPass"/> changes a pass across every
/// pairing, a pass a minute: role sync's shape, for its reason. Discord queues the bot's requests
/// behind one another, and a thousand at once would stall everything else the bot does.
/// </para>
/// </remarks>
public sealed class ListRoleSync
{
    /// <summary>How many role changes one pass makes. The next pass carries on.</summary>
    public const int MaxChangesPerPass = 50;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly ListRolePlanner _planner;

    public ListRoleSync(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        ListRolePlanner planner)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(planner);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _planner = planner;
    }

    /// <summary>One pass over every pairing that is on.</summary>
    public async Task<ListRolePass> RunAsync(IDiscordGateway? gateway, CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.DiscordListRolesOn, s.DiscordGuildId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (settings is not { DiscordListRolesOn: true } || string.IsNullOrWhiteSpace(settings.DiscordGuildId))
            return ListRolePass.Nothing;

        var pairings = await _db.DiscordListRoles
            .Where(p => p.Enabled)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (pairings.Count == 0)
            return ListRolePass.Nothing;

        if (gateway is null)
            return ListRolePass.Nothing with { Problem = "The Discord bot is not connected." };

        var guildId = settings.DiscordGuildId.Trim();
        var tally = new Tally();

        foreach (var pairing in pairings)
            await RunOneAsync(gateway, guildId, pairing, tally, ct).ConfigureAwait(false);

        await NoteRanAsync(tally.Problem, ct).ConfigureAwait(false);

        return new ListRolePass(tally.Given, tally.Taken, tally.Left, tally.Stopped, tally.Problem);
    }

    private async Task RunOneAsync(IDiscordGateway gateway, string guildId, DiscordListRole pairing, Tally tally, CancellationToken ct)
    {
        var plan = await _planner.PlanAsync(pairing, ct).ConfigureAwait(false);

        if (plan.Problem is { } refused)
        {
            pairing.Problem = refused;
            tally.Problem = refused;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        var losing = plan.Losing;
        var roleName = plan.RoleName ?? pairing.DiscordRoleName ?? pairing.DiscordRoleId;

        // The brakes come before anything is forgotten or sent: a member list that suddenly shows
        // everybody gone must not cost Modbot its record of whom it gave the role to, and a list
        // that suddenly lets most of the server in must not hand them all the role.
        var lossStops = plan.LossStops && !(pairing.RemovalsAllowed is { } allowed && losing <= allowed);
        var giveStops = plan.GiveStops && !(pairing.GivesAllowed is { } gives && plan.Giving <= gives);

        if (lossStops || giveStops)
        {
            await StopAsync(pairing, plan, roleName, lossStops, tally, ct).ConfigureAwait(false);
            tally.Left += plan.Changes.Count;
            return;
        }

        // Under a brake again: whatever Apply allowed for it has been used up or is no longer needed.
        if (!plan.LossStops)
            pairing.RemovalsAllowed = null;

        if (!plan.GiveStops)
            pairing.GivesAllowed = null;

        pairing.StoppedAt = null;
        pairing.StoppedTaking = null;
        pairing.Problem = plan.HeldBecause;

        if (plan.Forget.Count > 0)
        {
            var forget = plan.Forget.ToList();
            await _db.DiscordListRolesGiven
                .Where(g => g.ListRoleId == pairing.Id && forget.Contains(g.DiscordUserId))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            Allowed(pairing, plan.Leaving);
        }

        for (var i = 0; i < plan.Changes.Count; i++)
        {
            var change = plan.Changes[i];

            if (tally.Given + tally.Taken >= MaxChangesPerPass)
            {
                tally.Left += plan.Changes.Count - i;
                break;
            }

            var give = change.What == ListRoleChangeKinds.Give;
            var reason = give
                ? $"Modbot: in the list “{plan.ListName}”"
                : $"Modbot: no longer in the list “{plan.ListName}”";

            var outcome = await gateway.ChangeRoleAsync(guildId, change.DiscordUserId, pairing.DiscordRoleId, give, reason, ct)
                .ConfigureAwait(false);

            if (outcome.Done)
            {
                // The stored member row follows at once, rather than waiting for Discord's update to
                // come back: the next pass reads it.
                await MemberHoldsAsync(guildId, change.DiscordUserId, pairing.DiscordRoleId, give, ct).ConfigureAwait(false);

                if (give)
                {
                    await RememberGivenAsync(pairing.Id, change, ct).ConfigureAwait(false);
                    if (pairing.GivesAllowed is { } givesLeft)
                        pairing.GivesAllowed = Math.Max(0, givesLeft - 1);
                    tally.Given++;
                }
                else
                {
                    await ForgetGivenAsync(pairing.Id, change.DiscordUserId, ct).ConfigureAwait(false);
                    Allowed(pairing, 1);

                    // The member row did not show the role: Discord accepted a removal of a role
                    // they may not have held, so nothing is said to have been taken.
                    if (!change.Holds)
                        continue;

                    tally.Taken++;
                }

                await WriteAsync(
                        give ? FactType.ListRoleGiven : FactType.ListRoleTaken,
                        FactPlatform.Discord,
                        change.DiscordUserId,
                        Payload(pairing, plan, roleName, change.VRChatUserId),
                        ct)
                    .ConfigureAwait(false);

                continue;
            }

            // Discord says they are not in the server: leaving took the role, and the next pass will
            // not see them at all.
            if (outcome.NotInServer)
            {
                await ForgetGivenAsync(pairing.Id, change.DiscordUserId, ct).ConfigureAwait(false);
                continue;
            }

            // A role gone or a permission the bot lost fails the same way for everybody after this
            // one, so the pairing stops for this pass and says why. What it did not get to is left.
            pairing.Problem = outcome.RoleGone ? "That role is not in the Discord server." : outcome.Error;
            tally.Problem = pairing.Problem;
            tally.Left += plan.Changes.Count - i;
            break;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Lowers what Apply allowed by the losses just made, so it covers only the backlog it was given for.</summary>
    private static void Allowed(DiscordListRole pairing, int used)
    {
        if (pairing.RemovalsAllowed is { } allowed)
            pairing.RemovalsAllowed = Math.Max(0, allowed - used);
    }

    /// <summary>
    /// Adds or removes this one role on the stored member row, in one statement, leaving every other
    /// role as it is. Never a write of the whole list, which could undo an update that arrived since.
    /// </summary>
    private Task<int> MemberHoldsAsync(string guildId, string discordUserId, string roleId, bool holds, CancellationToken ct)
        => holds
            ? _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE discord_member
                SET roles = roles || jsonb_build_array({roleId}::text)
                WHERE guild_id = {guildId} AND user_id = {discordUserId}
                  AND NOT (roles @> jsonb_build_array({roleId}::text))
                """,
                ct)
            : _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE discord_member
                SET roles = roles - {roleId}::text
                WHERE guild_id = {guildId} AND user_id = {discordUserId}
                """,
                ct);

    private async Task StopAsync(
        DiscordListRole pairing, ListRolePlan plan, string roleName, bool losses, Tally tally, CancellationToken ct)
    {
        var first = pairing.StoppedAt is null;
        var taking = plan.Losing;

        pairing.StoppedAt ??= _clock.UtcNow;
        pairing.StoppedTaking = taking;
        pairing.RemovalsAllowed = null;
        pairing.GivesAllowed = null;
        pairing.Problem = losses
            ? $"Stopped: {taking} people would lose {roleName} at once."
            : $"Stopped: {plan.Giving} people would be given {roleName} at once.";

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        tally.Stopped++;
        tally.Problem = pairing.Problem;

        // Said once when it stops, not every minute while it waits.
        if (first)
        {
            await WriteAsync(FactType.ListRoleStopped, FactPlatform.Modbot, pairing.Id.ToString(), new JsonObject
            {
                ["listId"] = plan.ListId.ToString(),
                ["listName"] = plan.ListName,
                ["roleId"] = pairing.DiscordRoleId,
                ["roleName"] = roleName,
                ["taking"] = taking,
                ["giving"] = plan.Giving,
                ["holders"] = plan.Holders,
            }, ct).ConfigureAwait(false);
        }
    }

    // ── Who it gave the role to ────────────────────────────────────────────────────────────

    private async Task RememberGivenAsync(Guid pairingId, ListRoleChange change, CancellationToken ct)
    {
        var row = await _db.DiscordListRolesGiven
            .FirstOrDefaultAsync(g => g.ListRoleId == pairingId && g.DiscordUserId == change.DiscordUserId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            _db.DiscordListRolesGiven.Add(new DiscordListRoleGiven
            {
                ListRoleId = pairingId,
                DiscordUserId = change.DiscordUserId,
                VRChatUserId = change.VRChatUserId,
                GivenAt = _clock.UtcNow,
            });
        }
        else
        {
            row.VRChatUserId = change.VRChatUserId;
            row.GivenAt = _clock.UtcNow;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private Task ForgetGivenAsync(Guid pairingId, string discordUserId, CancellationToken ct)
        => _db.DiscordListRolesGiven
            .Where(g => g.ListRoleId == pairingId && g.DiscordUserId == discordUserId)
            .ExecuteDeleteAsync(ct);

    // ── Pieces ─────────────────────────────────────────────────────────────────────────────

    /// <summary>What a give or take fact carries: the list and the role, never a display name.</summary>
    private static JsonObject Payload(DiscordListRole pairing, ListRolePlan plan, string roleName, string? vrchatUserId)
    {
        var data = new JsonObject
        {
            ["listRoleId"] = pairing.Id.ToString(),
            ["listId"] = plan.ListId.ToString(),
            ["listName"] = plan.ListName,
            ["roleId"] = pairing.DiscordRoleId,
            ["roleName"] = roleName,
        };

        if (vrchatUserId is not null)
            data["vrchatUserId"] = vrchatUserId;

        return data;
    }

    private async Task NoteRanAsync(string? problem, CancellationToken ct)
    {
        var state = await _db.DiscordSyncState.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);

        if (state is null)
        {
            state = new DiscordSyncState { Id = 1 };
            _db.DiscordSyncState.Add(state);
        }

        state.ListRolesRanAt = _clock.UtcNow;
        state.ListRolesProblem = problem;

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

    private sealed class Tally
    {
        public int Given;
        public int Taken;
        public int Left;
        public int Stopped;
        public string? Problem;
    }
}
