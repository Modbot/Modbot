using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.Discord.Cards;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Linking;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Gate;

/// <summary>The ids of the join gate's buttons, before the server mark (join gate design §4 and §8).</summary>
public static class JoinGateButtons
{
    public const string GetIn = "modbot:gate:in";
    public const string Agree = "modbot:gate:agree";
    public const string Check = "modbot:gate:check";
    public const string Hold = "modbot:gate:hold";
    public const string LiftHold = "modbot:gate:lift";
    public const string PauseInvites = "modbot:gate:pause";

    /// <summary>
    /// Let in, under each person <c>/gate waiting</c> lists: this and <c>:</c> and their Discord id.
    /// The one gate button that names a person, so the only one that carries an id.
    /// </summary>
    public const string LetIn = "modbot:gate:letin";

    public const string GetInLabel = "Get in";
    public const string LetInLabel = "Let in";
    public const string LiftHoldLabel = "Lift hold";

    /// <summary>Whether a pressed button is one of the gate's, server mark or not.</summary>
    public static bool Is(string buttonId)
        => DiscordActionButton.Plain(buttonId).StartsWith("modbot:gate:", StringComparison.Ordinal);

    /// <summary>The id of the Let in button for one person.</summary>
    public static string LetInFor(string discordUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);
        return LetIn + ":" + discordUserId;
    }

    /// <summary>The Discord id a Let in button names, or null when this is not one.</summary>
    public static string? LetInTarget(string buttonId)
    {
        ArgumentNullException.ThrowIfNull(buttonId);

        var id = DiscordActionButton.Plain(buttonId);
        return id.Length > LetIn.Length + 1 && id.StartsWith(LetIn + ":", StringComparison.Ordinal)
            ? id[(LetIn.Length + 1)..]
            : null;
    }
}

/// <summary>What one pass of the gate did.</summary>
public sealed record JoinGatePass(int Passed, int LetInInDiscord, int Warned, int Removed, int Left, string? Problem)
{
    public static JoinGatePass Nothing { get; } = new(0, 0, 0, 0, 0, null);
}

/// <summary>
/// The join gate (join gate design): a new member does the steps the server asks for, and Modbot
/// gives them the member role; one who does not finish is warned halfway and removed at the end,
/// when the operator set a removal time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Modbot gives the member role; it never takes one away.</strong> So when Modbot is down,
/// new joiners wait and nobody walks in unchecked (owner's answer to Q3). A role given by anybody
/// else -- a moderator in Discord, the old captcha bot -- counts as let in.
/// </para>
/// <para>
/// <strong>Time counts only while the hold-up is the person's</strong> (§6): a pass adds at most two
/// minutes, and none while the bot cannot give the role, VRChat is refusing reads for a gate that
/// needs a link, linking is not set up, or the person has done every step and waits on Modbot or a
/// hold. A Modbot that was down for a day counts as two minutes.
/// </para>
/// <para>
/// <strong>Watch only does nothing in Discord</strong> (§9): no message, no role, no kick, no hold.
/// Its rows say when the person would have been warned and removed, so it can run beside an older
/// captcha bot for a week and be compared.
/// </para>
/// </remarks>
public sealed class JoinGate
{
    /// <summary>The most a pass adds to anybody's time, so a gap in passes never counts against them.</summary>
    public const int MostMinutesAPass = 2;

    /// <summary>Most removals in any hour; per minute, <see cref="JoinGateState.MostRemovalsAMinute"/>.</summary>
    public const int MostRemovalsAnHour = 30;

    public const int MostRowsAPass = 200;

    public const int MostCaughtUpAPass = 25;

    /// <summary>How long Discord lets invites be paused for at once.</summary>
    public static readonly TimeSpan InvitePause = TimeSpan.FromHours(24);

    public const string PassedReason = "Modbot: passed the join gate";
    public const string RemovedReason = "Modbot: did not finish the join gate";
    public const string YoureIn = "You're in.";
    public const string GateOff = "The join gate is off.";
    public const string NotInServer = "You are not in the server.";
    public const string Held = "New joiners are on hold. Staff will let you in.";
    public const string RulesFirst = "Accept the server's rules in Discord first.";
    public const string CouldNotGiveRole = "Modbot could not give you the role. Staff can see why.";
    public const string AskStaff = "Ask the server's staff to let you in.";
    public const string TooBusy = "Lots of people are getting in right now. You will be let in within a few minutes.";

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;
    private readonly JoinGateState _state;

    public JoinGate(
        ModbotContext db,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IModbotClock clock,
        JoinGateState state)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(state);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
        _state = state;
    }

    // ── Joining ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Somebody joined. Returns true when the gate took them and sent its own message, so the link
    /// prompt is not sent as well.
    /// </summary>
    public async Task<bool> JoinedAsync(IDiscordGateway gateway, DiscordMemberJoin member, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(member);

        if (member.IsBot)
            return false;

        GateSettings settings;

        // The row is made under the lock, with the settings read under it; the welcome goes out
        // after it is released, so a raid's worth of direct messages never makes a Hold or a
        // settings save wait.
        using (await _state.LockAsync(ct).ConfigureAwait(false))
        {
            settings = await SettingsAsync(ct).ConfigureAwait(false);
            if (settings.Mode == DiscordGateModes.Off || settings.GuildId != member.GuildId || settings.MemberRoleId is null)
                return false;

            // Somebody who walks in already holding the member role (given by Onboarding, or kept
            // across a rejoin by another bot) is in.
            if (member.Member?.RoleIds.Contains(settings.MemberRoleId, StringComparer.Ordinal) == true)
                return false;

            // Staff joining (a Modbot account's Discord) and the server's owner are not gated.
            var exempt = await ExemptionsAsync(gateway, settings.GuildId, ct).ConfigureAwait(false);
            if (exempt.Covers(member.UserId, [], member.IsBot))
                return false;

            // A join is a new time through the gate. A row still open from before means they left and
            // came back before a pass saw them go: that time ended when they left.
            if (await OpenEntryAsync(settings.GuildId, member.UserId, ct).ConfigureAwait(false) is { } earlier)
            {
                Close(earlier, DiscordGateOutcomes.Left, null);

                // Saved before the new row, which the one-open-row index would otherwise refuse.
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            var entry = await StartAsync(settings, member.UserId, member.Username, _clock.UtcNow, ct).ConfigureAwait(false);
            entry.Pending = member.Member?.IsPending ?? false;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (settings.Mode != DiscordGateModes.On)
                return false;
        }

        await WelcomeAsync(gateway, settings, member.UserId, ct).ConfigureAwait(false);

        _state.Wake();
        return true;
    }

    // ── The pass ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Welcomes the pass owes people it caught up on, sent once the lock is released.</summary>
    private sealed class Welcomes
    {
        public List<string> UserIds { get; } = [];

        public GateSettings? Settings { get; set; }
    }

    /// <summary>One pass: the gate message, a join spike, people missed while offline, and everybody waiting.</summary>
    public async Task<JoinGatePass> RunAsync(IDiscordGateway gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var welcomes = new Welcomes();
        JoinGatePass pass;

        // The lock first, then the settings: a settings save waits for a running pass and runs
        // alone, so a pass never acts on a mode or a removal time that changed under it.
        using (await _state.LockAsync(ct).ConfigureAwait(false))
            pass = await PassAsync(gateway, welcomes, ct).ConfigureAwait(false);

        // Welcomes go out after the lock, as a join's does.
        if (welcomes.Settings is { Mode: DiscordGateModes.On } settings)
        {
            foreach (var userId in welcomes.UserIds)
                await WelcomeAsync(gateway, settings, userId, ct).ConfigureAwait(false);
        }

        return pass;
    }

    private async Task<JoinGatePass> PassAsync(IDiscordGateway gateway, Welcomes welcomes, CancellationToken ct)
    {
        var settings = await SettingsAsync(ct).ConfigureAwait(false);
        if (settings.GuildId is null)
            return JoinGatePass.Nothing;

        // Rows made under another mode end: Watch only never turns into removals.
        await CloseOtherModesAsync(settings, ct).ConfigureAwait(false);

        string? problem = await KeepMessageAsync(gateway, settings, ct).ConfigureAwait(false);

        if (settings.Mode == DiscordGateModes.Off || settings.MemberRoleId is null)
            return JoinGatePass.Nothing with { Problem = problem };

        if (settings.Mode == DiscordGateModes.On)
        {
            problem ??= await SpikeAsync(gateway, settings, ct).ConfigureAwait(false);

            // A spike may have just held new joiners; nobody gets in on the old answer.
            settings = await SettingsAsync(ct).ConfigureAwait(false);
        }

        if (settings is not { GuildId: { } guildId, MemberRoleId: { } memberRoleId })
            return JoinGatePass.Nothing with { Problem = problem };

        welcomes.Settings = settings;
        welcomes.UserIds.AddRange(await CatchUpAsync(gateway, settings, ct).ConfigureAwait(false));

        var now = _clock.UtcNow;

        // A page of the open rows at a time, carrying on from where the last pass stopped and
        // starting over at the oldest once it reaches the end, so a crowd of more than a page never
        // keeps the newest joiners from being looked at. A row not looked at this pass only counts
        // less time (a pass adds at most two minutes), never more.
        var after = _state.PageAfter;
        var entries = await _db.DiscordGateEntries
            .Where(e => e.GuildId == guildId && e.ClosedAt == null && (after == null || e.JoinedAt > after))
            .OrderBy(e => e.JoinedAt)
            .Take(MostRowsAPass)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        _state.PageAfter = entries.Count < MostRowsAPass ? null : entries[^1].JoinedAt;

        if (entries.Count == 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return JoinGatePass.Nothing with { Problem = problem };
        }

        var ids = entries.Select(e => e.DiscordUserId).ToList();
        var members = await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && ids.Contains(m.UserId))
            .Select(m => new { m.UserId, m.Roles, m.LeftAt, m.IsPending })
            .ToDictionaryAsync(m => m.UserId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var links = await LinksAsync(ids, ct).ConfigureAwait(false);
        var timeIsTheirs = await ModbotIsWorkingAsync(settings, now, ct).ConfigureAwait(false);
        var exempt = await ExemptionsAsync(gateway, guildId, ct).ConfigureAwait(false);
        var removalsThisHour = MostRemovalsAnHour - await RemovedThisHourAsync(guildId, now, ct).ConfigureAwait(false);

        if (removalsThisHour <= 0 && settings.RemoveAfterMinutes is not null && settings.Mode == DiscordGateModes.On)
            problem ??= $"The join gate stopped removing people: {MostRemovalsAnHour} were removed in the last hour.";

        int passed = 0, inDiscord = 0, warned = 0, removed = 0, left = 0;

        // Somebody done would not get in either, so time stops for everybody while the bot cannot
        // give the role.
        var roleBlocked = await RoleBlockedAsync(guildId, memberRoleId, now, ct).ConfigureAwait(false);

        if (roleBlocked && settings.Mode == DiscordGateModes.On)
            problem ??= _state.RoleProblem ?? "The bot cannot give the join gate's member role.";

        foreach (var entry in entries)
        {
            members.TryGetValue(entry.DiscordUserId, out var member);

            if (member?.LeftAt is not null)
            {
                Close(entry, DiscordGateOutcomes.Left, null);
                left++;
                continue;
            }

            if (member is not null && HasRole(member.Roles, memberRoleId))
            {
                await LetInInDiscordAsync(entry, ct).ConfigureAwait(false);
                inDiscord++;
                continue;
            }

            links.TryGetValue(entry.DiscordUserId, out var link);
            var done = StepsDone(settings, entry, link);

            // The stored member list's word, or the join's when the list has no row for them yet.
            var pending = member?.IsPending ?? entry.Pending;

            if (settings.Mode == DiscordGateModes.On && done && !pending && settings.HeldAt is null && _state.TryRoleChange(now))
            {
                if (await GiveRoleAsync(gateway, settings, entry, ct).ConfigureAwait(false))
                {
                    await PassedAsync(settings, entry, ct).ConfigureAwait(false);
                    passed++;
                    continue;
                }

                if (entry.ClosedAt is not null)
                {
                    left++;
                    continue;
                }

                problem ??= _state.RoleProblem;
                roleBlocked = true;
            }

            // Removal set to Never: no clock at all.
            if (settings.RemoveAfterMinutes is not { } removeAfter)
            {
                entry.LastCountedAt = now;
                continue;
            }

            CountTime(entry, now, timeIsTheirs && !roleBlocked && (!done || pending));

            // The warning, halfway. It counts only once it reached them; anything else is tried again
            // next pass, and nobody is removed before it is delivered.
            if (entry.WarnedAt is null && DiscordGateTimes.WarningDue(entry.MinutesCounted, removeAfter))
            {
                if (entry.WatchOnly)
                {
                    entry.WarnedAt = now;
                }
                else
                {
                    // What the message promises is what the list shows: the earliest removal once
                    // this warning has gone out now.
                    var deadline = DiscordGateTimes.EarliestRemoval(entry, removeAfter, now) ?? now;

                    var warning = await WarnAsync(gateway, settings, entry.DiscordUserId, deadline, ct).ConfigureAwait(false);

                    if (warning.Delivered)
                    {
                        entry.WarnedAt = now;
                        entry.Problem = null;
                        await RecordAsync(FactType.DiscordGateWarned, entry.DiscordUserId, null, new JsonObject { ["via"] = warning.Via }, ct)
                            .ConfigureAwait(false);
                        warned++;
                    }
                    else
                    {
                        entry.Problem = "Could not warn them: " + (warning.Error ?? "Discord refused.");
                    }
                }
            }

            if (entry.MinutesCounted < removeAfter || entry.WarnedAt is not { } warnedAt || now - warnedAt < DiscordGateTimes.WarningWindow(removeAfter))
                continue;

            if (entry.WatchOnly)
            {
                entry.WouldRemoveAt ??= now;
                continue;
            }

            if (removalsThisHour <= 0 || !_state.TryRemoval(now))
                continue;

            removalsThisHour--;

            // Read live, at the moment of removal: a member role given a second ago, staff, a bot
            // or the server's owner is never removed, whatever the stored list says.
            var removal = await gateway
                .RemoveCheckedAsync(guildId, entry.DiscordUserId, RemovedReason, live => MayRemove(live, memberRoleId, exempt, done), ct)
                .ConfigureAwait(false);

            if (removal.Kept)
            {
                if (removal.Member is { } live && live.RoleIds.Contains(memberRoleId, StringComparer.Ordinal))
                {
                    await LetInInDiscordAsync(entry, ct).ConfigureAwait(false);
                    inDiscord++;
                }
                else if (removal.Member is { } accepted && !exempt.Covers(accepted.UserId, accepted.RoleIds, accepted.IsBot))
                {
                    // Done with the steps and, as Discord has it now, through its rules too: the
                    // stored list was behind. They get the role on a coming pass instead.
                    entry.Pending = false;
                }
                else
                {
                    Close(entry, DiscordGateOutcomes.NotGated, null);
                }

                continue;
            }

            var outcome = removal.Outcome;

            if (outcome.Done && outcome.NothingToDo)
            {
                Close(entry, DiscordGateOutcomes.Left, null);
                left++;
            }
            else if (outcome.Done)
            {
                Close(entry, DiscordGateOutcomes.Removed, null);
                await RecordAsync(FactType.DiscordGateRemoved, entry.DiscordUserId, null, new JsonObject
                {
                    ["by"] = "gate",
                    ["minutes"] = entry.MinutesCounted,
                }, ct).ConfigureAwait(false);
                removed++;
            }
            else if (outcome.NotAllowed)
            {
                // Discord will refuse this one every time (they outrank the bot, or Kick Members is
                // missing): stop trying, say so once, and leave the removal slots for others.
                var why = outcome.Error ?? "Discord refused the removal.";
                Close(entry, DiscordGateOutcomes.CannotRemove, null);
                entry.Problem = why;
                await RecordAsync(FactType.DiscordGateRemoveRefused, entry.DiscordUserId, null, new JsonObject { ["error"] = why }, ct)
                    .ConfigureAwait(false);
                problem ??= "Could not remove somebody at the join gate: " + why;
            }
            else
            {
                entry.Problem = outcome.Error ?? "Discord did not answer the removal.";
                problem ??= "Could not remove somebody at the join gate: " + entry.Problem;
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new JoinGatePass(passed, inDiscord, warned, removed, left, problem);
    }

    // ── Buttons ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A press on one of the gate's buttons: the member's own, or staff's on an alert.</summary>
    public async Task<DiscordReply> PressAsync(IDiscordGateway? gateway, DiscordButtonPress press, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(press);

        var id = DiscordActionButton.Plain(press.ButtonId);

        if (id is JoinGateButtons.Hold or JoinGateButtons.LiftHold or JoinGateButtons.PauseInvites
            || JoinGateButtons.LetInTarget(id) is not null)
        {
            return await StaffPressAsync(gateway, press, id, ct).ConfigureAwait(false);
        }

        if (id is not (JoinGateButtons.GetIn or JoinGateButtons.Agree or JoinGateButtons.Check))
            return DiscordReply.Say("Modbot does not know that button.");

        DiscordReply reply;

        // Settings, the member and the row are all read under the lock, so a press never acts on a
        // mode a settings save has just changed.
        using (await _state.LockAsync(ct).ConfigureAwait(false))
        {
            var settings = await SettingsAsync(ct).ConfigureAwait(false);

            if (settings.Mode != DiscordGateModes.On || settings.GuildId is null || settings.MemberRoleId is null)
                return DiscordReply.Say(GateOff);

            var member = await _db.DiscordMembers.AsNoTracking()
                .Where(m => m.GuildId == settings.GuildId && m.UserId == press.DiscordUserId)
                .Select(m => new { m.Roles, m.IsPending, m.LeftAt })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            // A press on an old direct message, from somebody who has left since.
            if (member?.LeftAt is not null)
                return DiscordReply.Say(NotInServer);

            if (member is not null && HasRole(member.Roles, settings.MemberRoleId))
                return DiscordReply.Say(YoureIn);

            // Only somebody the gate saw join is at the gate. Anybody else -- in the server before it
            // went on, or whose member role a moderator took away -- gets in only when staff let them
            // in: Get in must never hand back a role that was taken away on purpose, and no clock
            // ever starts for somebody already in the server.
            var entry = await OpenEntryAsync(settings.GuildId, press.DiscordUserId, ct).ConfigureAwait(false);
            if (entry is null)
                return DiscordReply.Say(AskStaff);

            if (id == JoinGateButtons.Agree)
                entry.AgreedAt ??= _clock.UtcNow;

            var links = await LinksAsync([press.DiscordUserId], ct).ConfigureAwait(false);
            links.TryGetValue(press.DiscordUserId, out var link);

            if (!StepsDone(settings, entry, link))
            {
                reply = await StepsReplyAsync(settings, entry, link, ct).ConfigureAwait(false);
            }
            else if (member?.IsPending ?? entry.Pending)
            {
                reply = DiscordReply.Say(RulesFirst);
            }
            else if (settings.HeldAt is not null)
            {
                reply = DiscordReply.Say(Held);
            }
            else if (gateway is null)
            {
                reply = DiscordReply.Say(CouldNotGiveRole);
            }
            else if (!_state.TryRoleChange(_clock.UtcNow))
            {
                // The same allowance the pass uses; the pass lets them in once there is room.
                reply = DiscordReply.Say(TooBusy);
            }
            else if (await GiveRoleAsync(gateway, settings, entry, ct).ConfigureAwait(false))
            {
                await PassedAsync(settings, entry, ct).ConfigureAwait(false);
                reply = DiscordReply.Say(YoureIn);
            }
            else
            {
                reply = DiscordReply.Say(entry.ClosedAt is null ? CouldNotGiveRole : NotInServer);
            }

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return reply;
    }

    /// <summary>
    /// Hold, Lift hold and Pause invites, pressed on an alert or on a reply to one. The presser is
    /// the Modbot account that proved that Discord account, and needs Manage the join gate.
    /// </summary>
    private async Task<DiscordReply> StaffPressAsync(IDiscordGateway? gateway, DiscordButtonPress press, string id, CancellationToken ct)
    {
        var user = await StaffDiscord.AccountForAsync(_db, press.DiscordUserId, _clock.UtcNow, ct).ConfigureAwait(false);

        if (user is null)
            return DiscordReply.Say(DiscordCommandHandler.NotLinkedMessage);

        if (user.IsDisabled)
            return DiscordReply.Say("Your Modbot account is disabled.");

        if (!DiscordCommands.Allows(user.EffectivePermissions, ModbotPermissions.ManageJoinGate))
            return DiscordReply.Say("You need the \"Manage the join gate\" permission in Modbot.");

        var letIn = JoinGateButtons.LetInTarget(id);

        var outcome = id switch
        {
            JoinGateButtons.Hold => await HoldAsync(user.Id, ct).ConfigureAwait(false),
            JoinGateButtons.LiftHold => await LiftHoldAsync(user.Id, ct).ConfigureAwait(false),
            _ when letIn is not null => gateway is null
                ? JoinGateOutcome.Offline
                : await LetInAsync(gateway, letIn, user.Id, ct).ConfigureAwait(false),
            _ => gateway is null ? JoinGateOutcome.Offline : await PauseInvitesAsync(gateway, user.Id, ct).ConfigureAwait(false),
        };

        if (!outcome.Done)
            return DiscordReply.Say(outcome.Error ?? "That did not work.");

        if (letIn is not null)
            return LetInReply(letIn);

        return id switch
        {
            JoinGateButtons.Hold => await HeldReplyAsync(ct).ConfigureAwait(false),
            JoinGateButtons.LiftHold => DiscordReply.Say(HoldLifted),
            _ => DiscordReply.Say($"Invites are paused until {DiscordTime.Absolute(_clock.UtcNow + InvitePause)}."),
        };
    }

    public const string HoldLifted = "Hold lifted.";

    /// <summary>The answer to somebody let in: who, and nothing else.</summary>
    public static DiscordReply LetInReply(string discordUserId) => DiscordReply.Say($"Let in <@{discordUserId}>.");

    /// <summary>
    /// The answer to a hold, wherever it was asked from -- the button on an alert or <c>/gate hold</c>:
    /// that new joiners are held, with the button that lifts it. No confirmation comes first (Discord
    /// commands design 3.3, decision 12).
    /// </summary>
    public async Task<DiscordReply> HeldReplyAsync(CancellationToken ct)
    {
        var settings = await SettingsAsync(ct).ConfigureAwait(false);
        var guild = settings.GuildId ?? string.Empty;

        return new DiscordReply(
            "New joiners are held.",
            [],
            Actions: [new DiscordActionButton(JoinGateButtons.LiftHoldLabel, DiscordActionButton.Marked(JoinGateButtons.LiftHold, guild))]);
    }

    // ── Staff actions (IJoinGateActions goes through these) ──────────────────────────────────

    public async Task<JoinGateOutcome> LetInAsync(IDiscordGateway gateway, string discordUserId, Guid by, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        using (await _state.LockAsync(ct).ConfigureAwait(false))
        {
            var settings = await SettingsAsync(ct).ConfigureAwait(false);
            if (settings.Mode != DiscordGateModes.On || settings.GuildId is null || settings.MemberRoleId is null)
                return JoinGateOutcome.Failed("The join gate is not on.");

            var entry = await OpenEntryAsync(settings.GuildId, discordUserId, ct).ConfigureAwait(false);
            if (entry is null)
                return JoinGateOutcome.NotAtTheGate;

            if (!await GiveRoleAsync(gateway, settings, entry, ct).ConfigureAwait(false))
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                return entry.ClosedAt is not null
                    ? JoinGateOutcome.Failed("That person is not in the server.")
                    : JoinGateOutcome.Failed(_state.RoleProblem ?? "Discord refused the role.");
            }

            Close(entry, DiscordGateOutcomes.LetIn, by);
            await RecordAsync(FactType.DiscordGateLetIn, entry.DiscordUserId, by, new JsonObject { ["by"] = "person" }, ct).ConfigureAwait(false);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return JoinGateOutcome.Ok;
    }

    public async Task<JoinGateOutcome> RemoveAsync(IDiscordGateway gateway, string discordUserId, Guid by, string byName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        using (await _state.LockAsync(ct).ConfigureAwait(false))
        {
            var settings = await SettingsAsync(ct).ConfigureAwait(false);
            if (settings.Mode != DiscordGateModes.On || settings.GuildId is null)
                return JoinGateOutcome.Failed("The join gate is not on.");

            var entry = await OpenEntryAsync(settings.GuildId, discordUserId, ct).ConfigureAwait(false);
            if (entry is null)
                return JoinGateOutcome.NotAtTheGate;

            var reason = $"Modbot: removed at the join gate by {byName}";
            var exempt = await ExemptionsAsync(gateway, settings.GuildId, ct).ConfigureAwait(false);
            var memberRole = settings.MemberRoleId ?? string.Empty;

            // The same live check as the pass: somebody who got the member role a moment ago, staff,
            // a bot or the owner is not removed from here either.
            var removal = await gateway
                .RemoveCheckedAsync(settings.GuildId, discordUserId, reason.Length <= 512 ? reason : reason[..511] + "…",
                    live => MayRemove(live, memberRole, exempt), ct)
                .ConfigureAwait(false);

            if (removal.Kept)
            {
                if (removal.Member is { } live && live.RoleIds.Contains(memberRole, StringComparer.Ordinal))
                    await LetInInDiscordAsync(entry, ct).ConfigureAwait(false);
                else
                    Close(entry, DiscordGateOutcomes.NotGated, null);

                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                return JoinGateOutcome.NotAtTheGate;
            }

            var outcome = removal.Outcome;

            if (!outcome.Done)
                return JoinGateOutcome.Failed(outcome.Error ?? "Discord refused the removal.");

            Close(entry, outcome.NothingToDo ? DiscordGateOutcomes.Left : DiscordGateOutcomes.Removed, outcome.NothingToDo ? null : by);

            if (!outcome.NothingToDo)
            {
                await RecordAsync(FactType.DiscordGateRemoved, entry.DiscordUserId, by, new JsonObject
                {
                    ["by"] = "person",
                    ["minutes"] = entry.MinutesCounted,
                }, ct).ConfigureAwait(false);
            }

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return JoinGateOutcome.Ok;
    }

    /// <summary>A moderator holds new joiners. Waits for a running pass, so it lands between two.</summary>
    public async Task<JoinGateOutcome> HoldAsync(Guid by, CancellationToken ct)
    {
        using (await _state.LockAsync(ct).ConfigureAwait(false))
            return await HoldWithinPassAsync(by, ct).ConfigureAwait(false);
    }

    /// <param name="by">The Modbot account, or null when a join spike held them.</param>
    private async Task<JoinGateOutcome> HoldWithinPassAsync(Guid? by, CancellationToken ct)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (settings.DiscordGateMode != DiscordGateModes.On)
            return JoinGateOutcome.Failed("The join gate is not on.");

        if (settings.DiscordGateHeldAt is not null)
            return JoinGateOutcome.Ok;

        settings.DiscordGateHeldAt = _clock.UtcNow;
        await RecordAsync(FactType.DiscordGateHeld, settings.DiscordGuildId ?? string.Empty, by, new JsonObject
        {
            ["by"] = by is null ? "spike" : "person",
        }, ct, onServer: true).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return JoinGateOutcome.Ok;
    }

    public async Task<JoinGateOutcome> LiftHoldAsync(Guid by, CancellationToken ct)
    {
        using (await _state.LockAsync(ct).ConfigureAwait(false))
        {
            var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

            if (settings.DiscordGateHeldAt is null)
                return JoinGateOutcome.Ok;

            settings.DiscordGateHeldAt = null;
            await RecordAsync(FactType.DiscordGateHoldLifted, settings.DiscordGuildId ?? string.Empty, by, new JsonObject(), ct, onServer: true)
                .ConfigureAwait(false);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _state.Wake();
        return JoinGateOutcome.Ok;
    }

    /// <param name="by">The Modbot account, or null when a join spike paused them.</param>
    public async Task<JoinGateOutcome> PauseInvitesAsync(IDiscordGateway gateway, Guid? by, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var settings = await SettingsAsync(ct).ConfigureAwait(false);

        if (settings.Mode != DiscordGateModes.On || settings.GuildId is null)
            return JoinGateOutcome.Failed("The join gate is not on.");

        if (!settings.PauseInvites)
            return JoinGateOutcome.Failed("Pausing invites is not allowed in the join gate's settings.");

        var until = _clock.UtcNow + InvitePause;
        var outcome = await gateway.PauseInvitesAsync(settings.GuildId, until, ct).ConfigureAwait(false);

        if (!outcome.Sent)
            return JoinGateOutcome.Failed(outcome.Error ?? "Discord refused.");

        await RecordAsync(FactType.DiscordGateInvitesPaused, settings.GuildId, by, new JsonObject
        {
            ["until"] = until.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["by"] = by is null ? "spike" : "person",
        }, ct, onServer: true).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return JoinGateOutcome.Ok;
    }

    // ── The pieces ───────────────────────────────────────────────────────────────────────────

    /// <summary>The settings a pass needs, read once.</summary>
    private sealed record GateSettings(
        string? GuildId,
        string Mode,
        string? MemberRoleId,
        string? ChannelId,
        string? Message,
        string? MessageId,
        string? MessageChannelId,
        string? MessagePosted,
        bool NeedsLink,
        bool NeedsEighteenPlus,
        int? RemoveAfterMinutes,
        bool HoldOnSpike,
        bool PauseInvites,
        DateTimeOffset? HeldAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? SpikeSeenAt,
        bool LinkingReady,
        string? PublicAddress);

    private async Task<GateSettings> SettingsAsync(CancellationToken ct)
    {
        var s = await _db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == 1, ct)
            .ConfigureAwait(false);

        if (s is null)
        {
            return new GateSettings(
                null, DiscordGateModes.Off, null, null, null, null, null, null, false, false, null, false, false, null, null, null, false, null);
        }

        return new GateSettings(
            Blank(s.DiscordGuildId),
            DiscordGateModes.IsKnown(s.DiscordGateMode) ? s.DiscordGateMode : DiscordGateModes.Off,
            Blank(s.DiscordGateMemberRoleId),
            Blank(s.DiscordGateChannelId),
            s.DiscordGateMessage,
            Blank(s.DiscordGateMessageId),
            Blank(s.DiscordGateMessageChannelId),
            s.DiscordGateMessagePosted,
            s.DiscordGateNeedsLink,
            s.DiscordGateNeedsLink && s.DiscordGateNeedsEighteenPlus,
            s.DiscordGateRemoveAfterMinutes,
            s.DiscordGateHoldOnSpike,
            s.DiscordGatePauseInvites,
            s.DiscordGateHeldAt,
            s.DiscordGateStartedAt,
            s.DiscordGateSpikeSeenAt,
            !string.IsNullOrWhiteSpace(s.DiscordOAuthClientId)
                && s.DiscordOAuthClientSecretEncrypted is not null
                && DiscordInvite.LinkPageFor(s.PublicAddress) is not null,
            s.PublicAddress);
    }

    private Task<DiscordGateEntry?> OpenEntryAsync(string guildId, string userId, CancellationToken ct)
        => _db.DiscordGateEntries
            .FirstOrDefaultAsync(e => e.GuildId == guildId && e.DiscordUserId == userId && e.ClosedAt == null, ct);

    /// <summary>
    /// A new row for somebody at the gate. Somebody who got in through the gate before, and comes
    /// back, has already agreed.
    /// </summary>
    private async Task<DiscordGateEntry> StartAsync(GateSettings settings, string userId, string username, DateTimeOffset joinedAt, CancellationToken ct)
    {
        var agreedBefore = await _db.DiscordGateEntries.AsNoTracking()
            .Where(e => e.GuildId == settings.GuildId && e.DiscordUserId == userId
                        && (e.Outcome == DiscordGateOutcomes.Passed || e.Outcome == DiscordGateOutcomes.LetIn || e.Outcome == DiscordGateOutcomes.LetInInDiscord))
            .AnyAsync(ct)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;
        var entry = new DiscordGateEntry
        {
            GuildId = settings.GuildId!,
            DiscordUserId = userId,
            DiscordUsername = username,
            JoinedAt = joinedAt,
            WatchOnly = settings.Mode == DiscordGateModes.Watch,
            AgreedAt = agreedBefore ? now : null,
            LastCountedAt = now,
        };

        _db.DiscordGateEntries.Add(entry);
        return entry;
    }

    private void Close(DiscordGateEntry entry, string outcome, Guid? by)
    {
        entry.ClosedAt = _clock.UtcNow;
        entry.Outcome = outcome;
        entry.ClosedByUserId = by;
        entry.Problem = null;
    }

    /// <summary>
    /// Adds the whole minutes since the last pass, at most <see cref="MostMinutesAPass"/>, when time
    /// counts; when it does not, the clock starts again from now.
    /// </summary>
    public static void CountTime(DiscordGateEntry entry, DateTimeOffset now, bool counts)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!counts || entry.LastCountedAt is not { } last || now <= last)
        {
            entry.LastCountedAt = now;
            return;
        }

        var whole = (int)Math.Floor((now - last).TotalMinutes);

        // Less than a minute: keep the start, so passes woken every few seconds still add up.
        if (whole <= 0)
            return;

        entry.MinutesCounted += Math.Min(MostMinutesAPass, whole);
        entry.LastCountedAt = whole > MostMinutesAPass ? now : last.AddMinutes(whole);
    }

    /// <summary>Whether Modbot itself is in a state where time may count against the people waiting.</summary>
    private async Task<bool> ModbotIsWorkingAsync(GateSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        if (!settings.NeedsLink)
            return true;

        if (!settings.LinkingReady)
            return false;

        var since = now - TimeSpan.FromHours(1);
        return !await _db.Events.AsNoTracking()
            .AnyAsync(e => e.Type == FactType.RateLimitColdStop && e.OccurredAt > since, ct)
            .ConfigureAwait(false);
    }

    /// <summary>How long a refused role change keeps time from counting when nobody tries again.</summary>
    public static readonly TimeSpan RoleRefusalHolds = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether the bot cannot give the member role: it was refused within <see cref="RoleRefusalHolds"/>,
    /// or the bot's stored role list says it cannot assign it. A refusal nobody tried again since ages
    /// out, so fixing the role in Discord is found out without anybody having to finish first.
    /// </summary>
    private async Task<bool> RoleBlockedAsync(string guildId, string roleId, DateTimeOffset now, CancellationToken ct)
    {
        var cannotAssign = await _db.DiscordRoles.AsNoTracking()
            .AnyAsync(r => r.GuildId == guildId && r.RoleId == roleId && r.RemovedAt == null && !r.BotCanAssign, ct)
            .ConfigureAwait(false);

        if (cannotAssign)
            return true;

        if (_state.RoleProblemAt is { } at && now - at < RoleRefusalHolds)
            return true;

        _state.ClearRoleProblem();
        return false;
    }

    private Task<int> RemovedThisHourAsync(string guildId, DateTimeOffset now, CancellationToken ct)
    {
        var since = now - TimeSpan.FromHours(1);
        return _db.DiscordGateEntries.AsNoTracking()
            .CountAsync(e => e.GuildId == guildId && e.Outcome == DiscordGateOutcomes.Removed && e.ClosedAt > since, ct);
    }

    /// <summary>Each person's active link, and whether its VRChat account is 18+ verified.</summary>
    private async Task<Dictionary<string, (bool Linked, bool EighteenPlus)>> LinksAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var rows = await _db.DiscordAccountLinks.AsNoTracking()
            .Where(l => l.UnlinkedAt == null && ids.Contains(l.DiscordUserId))
            .Select(l => new
            {
                l.DiscordUserId,
                EighteenPlus = _db.VRChatUsers.Any(u => u.UserId == l.VRChatUserId && u.Is18PlusVerified),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.DiscordUserId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (true, g.Any(r => r.EighteenPlus)), StringComparer.Ordinal);
    }

    private static bool StepsDone(GateSettings settings, DiscordGateEntry entry, (bool Linked, bool EighteenPlus) link)
        => entry.AgreedAt is not null
           && (!settings.NeedsLink || link.Linked)
           && (!settings.NeedsEighteenPlus || link.EighteenPlus);

    private static JsonArray Steps(GateSettings settings)
    {
        var steps = new JsonArray { "agree" };
        if (settings.NeedsLink)
            steps.Add("link");
        if (settings.NeedsEighteenPlus)
            steps.Add("18+");
        return steps;
    }

    /// <summary>Whether a stored member's roles (a JSON list of ids) hold this role.</summary>
    private static bool HasRole(string roles, string roleId)
        => roles.Contains('"' + roleId + '"', StringComparison.Ordinal);

    /// <summary>A stored member's role ids, from the JSON list they are kept as.</summary>
    private static IReadOnlyList<string> StoredRoles(string roles)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(roles) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Gives the member role. False when it was not given: the row's problem says why, and when the
    /// person is not in the server any more the row is closed as left.
    /// </summary>
    private async Task<bool> GiveRoleAsync(IDiscordGateway gateway, GateSettings settings, DiscordGateEntry entry, CancellationToken ct)
    {
        var outcome = await gateway
            .ChangeRoleAsync(settings.GuildId!, entry.DiscordUserId, settings.MemberRoleId!, add: true, PassedReason, ct)
            .ConfigureAwait(false);

        if (outcome.Done)
        {
            _state.ClearRoleProblem();
            return true;
        }

        if (outcome.NotInServer)
        {
            Close(entry, DiscordGateOutcomes.Left, null);
            return false;
        }

        var why = outcome.RoleGone
            ? "The join gate's member role does not exist any more."
            : "Could not give the join gate's member role: " + (outcome.Error ?? "Discord refused.");

        _state.RoleRefused(why, _clock.UtcNow);
        entry.Problem = why;
        return false;
    }

    /// <summary>The DM a new member gets, or a mention in the gate channel when their DMs are closed.</summary>
    private async Task WelcomeAsync(IDiscordGateway gateway, GateSettings settings, string userId, CancellationToken ct)
    {
        var server = await ServerNameAsync(settings.GuildId, ct).ConfigureAwait(false);
        var getIn = new[] { new DiscordActionButton(JoinGateButtons.GetInLabel, DiscordActionButton.Marked(JoinGateButtons.GetIn, settings.GuildId!)) };

        var dm = await gateway.SendDirectMessageAsync(userId, WelcomeText(server), null, getIn, ct).ConfigureAwait(false);

        if (!dm.Sent && dm.DirectMessagesClosed && settings.ChannelId is { } channel)
            await gateway.MentionAsync(channel, userId, "welcome.", null, getIn, ct).ConfigureAwait(false);
    }

    /// <summary>The warning halfway to removal. Returns how it went: <c>dm</c>, <c>channel</c> or <c>none</c>.</summary>
    private sealed record Warning(bool Delivered, string Via, string? Error);

    /// <summary>
    /// Sends the warning: a direct message, or, when their DMs are closed, a mention in the gate
    /// channel. Delivered only when one of them was actually sent.
    /// </summary>
    private async Task<Warning> WarnAsync(IDiscordGateway gateway, GateSettings settings, string userId, DateTimeOffset deadline, CancellationToken ct)
    {
        var server = await ServerNameAsync(settings.GuildId, ct).ConfigureAwait(false);
        var getIn = new[] { new DiscordActionButton(JoinGateButtons.GetInLabel, DiscordActionButton.Marked(JoinGateButtons.GetIn, settings.GuildId!)) };
        var text = WarningText(server, deadline);

        var dm = await gateway.SendDirectMessageAsync(userId, text, null, getIn, ct).ConfigureAwait(false);
        if (dm.Sent)
            return new Warning(true, LinkPromptVia.DirectMessage, null);

        if (!dm.DirectMessagesClosed)
            return new Warning(false, LinkPromptVia.None, dm.Error);

        if (settings.ChannelId is not { } channel)
            return new Warning(false, LinkPromptVia.None, "Their DMs are closed and no gate channel is set.");

        var mention = await gateway.MentionAsync(channel, userId, text, null, getIn, ct).ConfigureAwait(false);
        return mention.Sent
            ? new Warning(true, LinkPromptVia.BackupChannel, null)
            : new Warning(false, LinkPromptVia.None, "Their DMs are closed and the gate channel refused the mention: " + mention.Error);
    }

    /// <summary>The server's owner, Modbot staff accounts' Discord ids, and roles that moderate: none of them is ever gated.</summary>
    public sealed record Exemptions(string? OwnerId, IReadOnlySet<string> StaffIds, IReadOnlySet<string> StaffRoleIds)
    {
        public bool Covers(string userId, IEnumerable<string> roleIds, bool isBot)
            => isBot
               || string.Equals(userId, OwnerId, StringComparison.Ordinal)
               || StaffIds.Contains(userId)
               || roleIds.Any(StaffRoleIds.Contains);
    }

    /// <summary>
    /// Roles that hold any of these are staff: Administrator, Kick Members, Ban Members, Manage
    /// Server, Manage Roles, Timeout Members.
    /// </summary>
    public const long StaffPermissions = (1L << 3) | (1L << 1) | (1L << 2) | (1L << 5) | (1L << 28) | (1L << 40);

    private async Task<Exemptions> ExemptionsAsync(IDiscordGateway? gateway, string guildId, CancellationToken ct)
    {
        var staff = await _db.Users.AsNoTracking()
            .Where(u => u.DeletedAt == null && u.DiscordUserId != null && u.DiscordUserId != "")
            .Select(u => u.DiscordUserId!)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var roles = await _db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId && r.RemovedAt == null && r.Permissions != null && (r.Permissions.Value & StaffPermissions) != 0)
            .Select(r => r.RoleId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new Exemptions(
            gateway?.GuildOwnerId(guildId),
            staff.Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal),
            roles.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Whether somebody, as Discord has them now, may be removed at the gate. Not when they hold the
    /// member role, are exempt, or have done every step and, live, accepted the server's rules: then
    /// the only thing between them and the role was a stored list running behind.
    /// </summary>
    /// <param name="stepsDone">They have done every step the gate asks for (a moderator's Remove passes false).</param>
    public static bool MayRemove(DiscordMemberSnapshot live, string memberRoleId, Exemptions exempt, bool stepsDone = false)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(exempt);

        return !live.RoleIds.Contains(memberRoleId, StringComparer.Ordinal)
               && !exempt.Covers(live.UserId, live.RoleIds, live.IsBot)
               && !(stepsDone && !live.IsPending);
    }

    private async Task LetInInDiscordAsync(DiscordGateEntry entry, CancellationToken ct)
    {
        Close(entry, DiscordGateOutcomes.LetInInDiscord, null);
        await RecordAsync(FactType.DiscordGateLetIn, entry.DiscordUserId, null, new JsonObject
        {
            ["by"] = "discord",
            ["watchOnly"] = entry.WatchOnly,
        }, ct).ConfigureAwait(false);
    }

    private async Task PassedAsync(GateSettings settings, DiscordGateEntry entry, CancellationToken ct)
    {
        Close(entry, DiscordGateOutcomes.Passed, null);
        await RecordAsync(FactType.DiscordGatePassed, entry.DiscordUserId, null, new JsonObject
        {
            ["roleId"] = settings.MemberRoleId,
            ["steps"] = Steps(settings),
        }, ct).ConfigureAwait(false);
    }

    public static string WelcomeText(string? serverName)
        => string.IsNullOrWhiteSpace(serverName)
            ? "Welcome!"
            : $"Welcome to **{CardText.Fit(CardText.EscapeName(serverName), CardText.MaxNameLength)}**!";

    public static string WarningText(string? serverName, DateTimeOffset deadline)
    {
        var server = string.IsNullOrWhiteSpace(serverName)
            ? "the server"
            : $"**{CardText.Fit(CardText.EscapeName(serverName), CardText.MaxNameLength)}**";

        return $"You have not finished getting in to {server}. Get in by {DiscordTime.Absolute(deadline)}, or you will be removed.";
    }

    private async Task<string?> ServerNameAsync(string? guildId, CancellationToken ct)
        => guildId is null
            ? null
            : await _db.DiscordServers.AsNoTracking()
                .Where(s => s.GuildId == guildId)
                .Select(s => s.Name)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

    /// <summary>The private answer to Get in: each step, done or not, with a button for what is left.</summary>
    private async Task<DiscordReply> StepsReplyAsync(GateSettings settings, DiscordGateEntry entry, (bool Linked, bool EighteenPlus) link, CancellationToken ct)
    {
        var server = await ServerNameAsync(settings.GuildId, ct).ConfigureAwait(false);
        var guild = settings.GuildId!;

        var fields = new List<DiscordEmbedField>
        {
            new("Rules", entry.AgreedAt is null ? "Not yet" : "Agreed", Inline: true),
        };

        if (settings.NeedsLink)
            fields.Add(new("VRChat account", link.Linked ? "Linked" : "Not linked", Inline: true));

        if (settings.NeedsEighteenPlus)
            fields.Add(new("18+", link.EighteenPlus ? "Verified on VRChat" : "Not verified on VRChat", Inline: true));

        var actions = new List<DiscordActionButton>();
        if (entry.AgreedAt is null)
            actions.Add(new DiscordActionButton("I agree", DiscordActionButton.Marked(JoinGateButtons.Agree, guild)));
        actions.Add(new DiscordActionButton("Check", DiscordActionButton.Marked(JoinGateButtons.Check, guild)));

        var links = new List<DiscordLinkButton>();
        if (settings.NeedsLink && !link.Linked && DiscordInvite.LinkPageFor(settings.PublicAddress) is { } page)
            links.Add(new DiscordLinkButton(LinkPrompt.ButtonLabel, page));

        var embed = new DiscordEmbedContent(
            string.IsNullOrWhiteSpace(server) ? "Getting in" : CardText.Plain($"Getting in to {server}", 256),
            null,
            CardColour.Violet,
            fields,
            null,
            null,
            null);

        return new DiscordReply(null, [embed], links, Actions: actions);
    }

    /// <summary>
    /// Keeps the one gate message in the gate channel while the gate is on, and takes it down when
    /// it is not. Rewritten when its words change, and looked at once an hour so a deleted one is
    /// put back.
    /// </summary>
    private async Task<string?> KeepMessageAsync(IDiscordGateway gateway, GateSettings settings, CancellationToken ct)
    {
        var row = await _db.Settings.FirstAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        var now = _clock.UtcNow;

        var wanted = settings.Mode == DiscordGateModes.On && settings.ChannelId is not null && settings.MemberRoleId is not null;

        // Gone from the old channel, or no longer wanted at all.
        if (settings.MessageId is { } oldId && settings.MessageChannelId is { } oldChannel
            && (!wanted || oldChannel != settings.ChannelId))
        {
            await gateway.DeleteMessageAsync(oldChannel, oldId, "Modbot: join gate message moved or turned off", ct).ConfigureAwait(false);
            row.DiscordGateMessageId = null;
            row.DiscordGateMessageChannelId = null;
            row.DiscordGateMessagePosted = null;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            settings = settings with { MessageId = null, MessageChannelId = null, MessagePosted = null };
        }

        if (!wanted)
            return null;

        var server = await ServerNameAsync(settings.GuildId, ct).ConfigureAwait(false);
        var text = MessageText(settings.Message, server);
        var getIn = new[] { new DiscordActionButton(JoinGateButtons.GetInLabel, DiscordActionButton.Marked(JoinGateButtons.GetIn, settings.GuildId!)) };

        if (settings.MessageId is { } id)
        {
            var due = settings.MessagePosted != text || _state.MessageCheckedAt is not { } checkedAt || now - checkedAt >= TimeSpan.FromHours(1);
            if (!due)
                return null;

            var edit = await gateway.EditWithActionsAsync(settings.ChannelId!, id, text, [], null, getIn, ct).ConfigureAwait(false);

            if (edit.Sent)
            {
                _state.MessageCheckedAt = now;
                row.DiscordGateMessagePosted = text;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                return null;
            }

            if (!edit.Permanent)
                return "Could not update the join gate message: " + edit.Error;

            // Deleted, or the bot may not touch it: post a new one.
            row.DiscordGateMessageId = null;
        }

        var post = await gateway.PostWithActionsAsync(settings.ChannelId!, text, [], null, getIn, ct).ConfigureAwait(false);

        if (!post.Sent)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return "Could not post the join gate message: " + post.Error;
        }

        _state.MessageCheckedAt = now;
        row.DiscordGateMessageId = post.MessageId;
        row.DiscordGateMessageChannelId = settings.ChannelId;
        row.DiscordGateMessagePosted = text;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// The gate message's words: the operator's own, or the server's name when they wrote none,
    /// because Discord does not send a message that is only a button.
    /// </summary>
    public static string MessageText(string? message, string? serverName)
    {
        if (!string.IsNullOrWhiteSpace(message))
            return message.Trim();

        return string.IsNullOrWhiteSpace(serverName)
            ? "Welcome!"
            : $"**{CardText.Fit(CardText.EscapeName(serverName), CardText.MaxNameLength)}**";
    }

    /// <summary>
    /// A new "People joining Discord" alert: held by itself when the operator asked for that, and
    /// invites paused too when that is allowed. Each alert is looked at once.
    /// </summary>
    private async Task<string?> SpikeAsync(IDiscordGateway gateway, GateSettings settings, CancellationToken ct)
    {
        var seen = settings.SpikeSeenAt ?? settings.StartedAt ?? _clock.UtcNow;

        var newest = await _db.Alerts.AsNoTracking()
            .Where(a => a.Watcher == AlertWatchers.DiscordJoins && a.At > seen)
            .OrderByDescending(a => a.At)
            .Select(a => (DateTimeOffset?)a.At)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (newest is null)
            return null;

        var row = await _db.Settings.FirstAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        row.DiscordGateSpikeSeenAt = newest;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (!settings.HoldOnSpike)
            return null;

        // Inside the pass, which already holds the lock.
        await HoldWithinPassAsync(null, ct).ConfigureAwait(false);

        if (!settings.PauseInvites)
            return null;

        var paused = await PauseInvitesAsync(gateway, null, ct).ConfigureAwait(false);
        return paused.Done ? null : "Could not pause invites on a join spike: " + paused.Error;
    }

    /// <summary>
    /// People who joined while the bot was away: in the stored member list since the gate went on,
    /// without the member role, and with no row covering this time in the server. They get a row,
    /// from now, and are returned so the pass can welcome them once it has let go of the lock.
    /// </summary>
    private async Task<List<string>> CatchUpAsync(IDiscordGateway gateway, GateSettings settings, CancellationToken ct)
    {
        if (settings.StartedAt is not { } started)
            return [];

        var listed = await _db.DiscordServers.AsNoTracking()
            .AnyAsync(s => s.GuildId == settings.GuildId && s.MembersListedAt != null, ct)
            .ConfigureAwait(false);

        if (!listed)
            return [];

        // Containment, as jsonb understands it: the stored list holds this role id. A string
        // Contains on the jsonb column would be sent as LIKE, which PostgreSQL has no operator for.
        var role = System.Text.Json.JsonSerializer.Serialize(new[] { settings.MemberRoleId });
        var guild = settings.GuildId!;
        var exempt = await ExemptionsAsync(gateway, guild, ct).ConfigureAwait(false);
        var staff = exempt.StaffIds.ToList();

        var missed = await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guild && m.LeftAt == null && !m.IsBot
                        && m.JoinedAt != null && m.JoinedAt >= started
                        && !EF.Functions.JsonContains(m.Roles, role)
                        && !staff.Contains(m.UserId)
                        && !_db.DiscordGateEntries.Any(e => e.GuildId == guild && e.DiscordUserId == m.UserId
                                                            && (e.ClosedAt == null || e.ClosedAt >= m.JoinedAt)))
            .OrderBy(m => m.JoinedAt)
            .Take(MostCaughtUpAPass)
            .Select(m => new { m.UserId, m.Username, m.Roles, m.IsPending })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var gated = new List<string>();

        foreach (var member in missed)
        {
            // The owner and anybody holding a role that moderates are not gated.
            if (exempt.Covers(member.UserId, StoredRoles(member.Roles), isBot: false))
                continue;

            var entry = await StartAsync(settings, member.UserId, member.Username, _clock.UtcNow, ct).ConfigureAwait(false);
            entry.Pending = member.IsPending;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            gated.Add(entry.DiscordUserId);
        }

        return gated;
    }

    /// <summary>Rows made under another mode, or while the gate was off, end as "gate changed".</summary>
    private async Task CloseOtherModesAsync(GateSettings settings, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var watch = settings.Mode == DiscordGateModes.Watch;
        var off = settings.Mode == DiscordGateModes.Off || settings.MemberRoleId is null;

        await _db.DiscordGateEntries
            .Where(e => e.ClosedAt == null && (off || e.WatchOnly != watch || e.GuildId != settings.GuildId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.ClosedAt, now)
                .SetProperty(e => e.Outcome, DiscordGateOutcomes.GateChanged), ct)
            .ConfigureAwait(false);
    }

    private async Task RecordAsync(string type, string subjectId, Guid? actor, JsonObject data, CancellationToken ct, bool onServer = false)
    {
        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        if (onServer)
            data["server"] = true;

        await _facts.WriteAsync(new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = subjectId,
                ActorPlatform = actor is null ? null : FactPlatform.Modbot,
                ActorId = actor?.ToString(),
                Source = actor is null ? FactSource.Modbot : FactSource.Manual,
                Data = data,
            }, ct)
            .ConfigureAwait(false);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
