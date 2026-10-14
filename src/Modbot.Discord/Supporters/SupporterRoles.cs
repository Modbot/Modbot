using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Moderation;
using Modbot.Core.Supporters;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Supporters;

/// <summary>What one pass of <see cref="SupporterRoles"/> did.</summary>
/// <param name="Given">Roles given, in Discord and in the VRChat group.</param>
/// <param name="Taken">Roles taken away.</param>
/// <param name="Thanked">Thank-you messages sent.</param>
/// <param name="Left">Changes left for the next pass because this one reached its limit.</param>
/// <param name="Problem">The last refusal, as a sentence, or null.</param>
public sealed record SupporterPass(int Given, int Taken, int Thanked, int Left, string? Problem)
{
    public static SupporterPass Empty { get; } = new(0, 0, 0, 0, null);
}

/// <summary>
/// Gives each supporter the Discord role and the VRChat group role of their tier, takes them away
/// when the next payment did not come, and sends the thank-you message.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A pass compares what a supporter should hold with what Modbot gave</strong>, and fixes
/// the difference. That makes it safe to run again and again: a pass that has done its work finds
/// nothing to do, and a refused change is simply tried again later.
/// </para>
/// <para>
/// <strong>Only roles Modbot gave are taken away.</strong> They are written on the supporter's row
/// when given, and only when Modbot added them: a role the person already holds (given by hand, or by Ko-fi's
/// own Discord link) is not recorded, so it is never taken. That is judged from the roles Modbot last saw.
/// </para>
/// <para>
/// <strong>The roles come off three days after the paid month is up.</strong> Ko-fi sends no message
/// when a membership stops, so the only sign is a payment that did not come
/// (<see cref="SupporterRules.RolesEndAt"/>).
/// </para>
/// <para>
/// <strong>The VRChat role goes to the VRChat account linked to the supporter's Discord account.</strong>
/// With no link there is nothing to give yet; the role follows once they link. The call goes
/// through the gate at background priority, on the same budget as every other group role change.
/// </para>
/// <para>
/// <strong>Pace.</strong> At most <see cref="MaxChangesPerPass"/> role changes and
/// <see cref="MaxThanksPerPass"/> messages a pass, one pass a minute. After a refusal a supporter is
/// left alone for <see cref="TryAgainAfter"/>.
/// </para>
/// </remarks>
public sealed class SupporterRoles(ModbotContext db, IModbotClock clock, IVRChatModerationActions vrchat)
{
    public const int MaxChangesPerPass = 20;

    public const int MaxThanksPerPass = 3;

    /// <summary>The length of the <c>supporter.problem</c> column.</summary>
    public const int MostProblemLength = 300;

    public static readonly TimeSpan TryAgainAfter = TimeSpan.FromMinutes(10);

    private const string RoleReason = "Modbot: supporter perks";

    public async Task<SupporterPass> RunAsync(IDiscordGateway gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var settings = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.DiscordGuildId, s.ManagedGroupId, s.SupporterThanksText })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (settings?.DiscordGuildId is not { Length: > 0 } guildId)
            return SupporterPass.Empty;

        var groupId = string.IsNullOrWhiteSpace(settings.ManagedGroupId) ? null : settings.ManagedGroupId;
        var now = clock.UtcNow;

        // Everybody who is paid up (matched or not, so a lapsed one is marked ended either way), and
        // anybody whose roles are still to be taken back.
        var supporters = await db.Supporters
            .Where(s => s.State == SupporterStates.Active || s.GivenDiscordRoleId != null || s.GivenVRChatRoleId != null)
            .OrderBy(s => s.UpdatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (supporters.Count == 0)
            return SupporterPass.Empty;

        var tiers = await db.SupporterTiers.AsNoTracking().ToDictionaryAsync(t => t.Id, ct).ConfigureAwait(false);

        var roles = await db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId)
            .ToDictionaryAsync(r => r.RoleId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        // What the matched supporters hold in the server now, as Modbot last saw it.
        var matchedIds = supporters.Where(s => s.DiscordUserId != null).Select(s => s.DiscordUserId!).Distinct().ToList();
        var heldInServer = (await db.DiscordMembers.AsNoTracking()
                .Where(m => m.GuildId == guildId && m.LeftAt == null && matchedIds.Contains(m.UserId))
                .Select(m => new { m.UserId, m.Roles })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToDictionary(m => m.UserId, m => RoleIds(m.Roles), StringComparer.Ordinal);

        var tally = new Tally();

        foreach (var supporter in supporters)
        {
            if (supporter.State == SupporterStates.Active && SupporterRules.HasLapsed(supporter.PaidThrough, now))
            {
                supporter.State = SupporterStates.Ended;
                supporter.UpdatedAt = now;
            }

            // Nobody to give roles to until they are matched; only the lapse above applies. A role
            // still recorded from before their account was cleared is let go of, so it is not left stuck.
            if (supporter.DiscordUserId is null)
            {
                await LetGoOfRolesWithoutAccountAsync(supporter, now, tally, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                continue;
            }

            var tier = supporter.State == SupporterStates.Active && supporter.TierId is { } tierId
                ? tiers.GetValueOrDefault(tierId)
                : null;

            var wantDiscord = Blank(tier?.DiscordRoleId);
            var wantVRChat = groupId is null ? null : Blank(tier?.VRChatRoleId);
            var wantUser = wantVRChat is null
                ? null
                : await db.LinkedVRChatUserIdAsync(supporter.DiscordUserId!, ct).ConfigureAwait(false);

            // A role they already hold was not given by Modbot, so Modbot neither records nor later takes it.
            // "Already hold" is read from the roles stored for the member, which can lag a little behind
            // Discord: a role just added by hand may not show yet, and then Modbot adds it and records
            // it as its own. That is rare and the cost is small (the role is taken back when it ends).
            var heldDiscord = wantDiscord is not null
                && heldInServer.TryGetValue(supporter.DiscordUserId, out var serverRoles)
                && serverRoles.Contains(wantDiscord);

            // Staff moved them to another account: the role Modbot gave the old one is still out there.
            var onOtherAccount = supporter.GivenDiscordRoleId is not null
                && supporter.GivenDiscordUserId is { } givenTo
                && givenTo != supporter.DiscordUserId;
            var discordOutOfStep = onOtherAccount
                || (supporter.GivenDiscordRoleId != wantDiscord
                    && !(heldDiscord && supporter.GivenDiscordRoleId is null));

            var vrchatDiffers = supporter.GivenVRChatRoleId != (wantUser is null ? null : wantVRChat)
                || supporter.GivenVRChatUserId != wantUser;
            var heldVRChat = vrchatDiffers && wantUser is not null && wantVRChat is not null
                && await HoldsGroupRoleAsync(groupId!, wantUser, wantVRChat, ct).ConfigureAwait(false);
            var vrchatOutOfStep = vrchatDiffers
                && !(heldVRChat && supporter.GivenVRChatRoleId is null && supporter.GivenVRChatUserId is null);

            if (discordOutOfStep || vrchatOutOfStep)
            {
                if (supporter.TryAgainAt is { } waitUntil && now < waitUntil)
                    continue;

                if (tally.Given + tally.Taken >= MaxChangesPerPass)
                {
                    tally.Left++;
                    continue;
                }

                string? problem = null;
                var notInServer = false;

                if (discordOutOfStep)
                    (problem, notInServer) = await ChangeDiscordAsync(gateway, guildId, supporter, wantDiscord, heldDiscord, roles, tally, ct).ConfigureAwait(false);

                if (vrchatOutOfStep && problem is null)
                    problem = await ChangeVRChatAsync(supporter, wantVRChat, wantUser, heldVRChat, tally, ct).ConfigureAwait(false);

                supporter.Problem = Clip(problem);
                supporter.TryAgainAt = problem is null && !notInServer ? null : now + TryAgainAfter;
                supporter.UpdatedAt = now;

                if (problem is not null)
                    tally.Problem = problem;

                // Somebody who is not in the server cannot be thanked there either: later.
                if (notInServer)
                {
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    continue;
                }
            }

            if (supporter.State == SupporterStates.Active
                && supporter.ThanksSentAt is null
                && (supporter.TryAgainAt is null || now >= supporter.TryAgainAt))
                await ThankAsync(gateway, supporter, tier, settings.SupporterThanksText, now, tally, ct).ConfigureAwait(false);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new SupporterPass(tally.Given, tally.Taken, tally.Thanked, tally.Left, tally.Problem);
    }

    /// <summary>
    /// A supporter with no Discord account that still has a role on record (their account was cleared
    /// after Modbot gave it). The Discord role cannot be taken back, because the account it went to is
    /// no longer known, so the record is cleared. The VRChat role names its own VRChat account, so it
    /// is taken from there; a refusal keeps the record and is tried again later.
    /// </summary>
    private async Task LetGoOfRolesWithoutAccountAsync(Supporter supporter, DateTimeOffset now, Tally tally, CancellationToken ct)
    {
        supporter.GivenDiscordRoleId = null;
        supporter.GivenDiscordUserId = null;

        if (supporter.GivenVRChatRoleId is not { } role || supporter.GivenVRChatUserId is not { } user)
        {
            // Nothing recorded, or half a record that cannot be acted on: start clean.
            supporter.GivenVRChatRoleId = null;
            supporter.GivenVRChatUserId = null;
            return;
        }

        if (supporter.TryAgainAt is { } waitUntil && now < waitUntil)
            return;

        if (tally.Given + tally.Taken >= MaxChangesPerPass)
        {
            tally.Left++;
            return;
        }

        var outcome = await vrchat.TakeGroupRoleAsync(user, role, ct).ConfigureAwait(false);

        if (outcome.Done)
        {
            tally.Taken++;
            supporter.GivenVRChatRoleId = null;
            supporter.GivenVRChatUserId = null;
            supporter.Problem = null;
            supporter.TryAgainAt = null;
        }
        else
        {
            var problem = outcome.Error ?? "VRChat refused to take the group role away.";
            supporter.Problem = Clip(problem);
            supporter.TryAgainAt = now + TryAgainAfter;
            tally.Problem = problem;
        }

        supporter.UpdatedAt = now;
    }

    /// <summary>Takes back the role Modbot gave and gives the one that is due. Returns the refusal, if any, and whether the person is not in the server.</summary>
    private async Task<(string? Problem, bool NotInServer)> ChangeDiscordAsync(
        IDiscordGateway gateway,
        string guildId,
        Supporter supporter,
        string? want,
        bool alreadyHeld,
        IReadOnlyDictionary<string, DiscordRole> roles,
        Tally tally,
        CancellationToken ct)
    {
        var userId = supporter.DiscordUserId!;

        // The role is taken from the account Modbot gave it to, which staff may have moved the supporter off.
        var holder = supporter.GivenDiscordUserId ?? userId;

        if (supporter.GivenDiscordRoleId is { } given && (given != want || holder != userId))
        {
            var outcome = await gateway.ChangeRoleAsync(guildId, holder, given, false, RoleReason, ct).ConfigureAwait(false);

            // A person who left, or a role that is gone, has nothing left to take.
            if (!outcome.Done && !outcome.NotInServer && !outcome.RoleGone)
                return (outcome.Error ?? "Discord refused to take the role away.", false);

            if (outcome.Done)
                tally.Taken++;

            supporter.GivenDiscordRoleId = null;
            supporter.GivenDiscordUserId = null;
        }

        if (want is not null && supporter.GivenDiscordRoleId != want && !alreadyHeld)
        {
            // A role the bot cannot assign is named here instead of discovered as a refusal each time.
            if (roles.TryGetValue(want, out var role) && !role.BotCanAssign)
                return ($"The bot cannot assign {role.Name}. Give it Manage Roles and keep its own role above that one.", false);

            var outcome = await gateway.ChangeRoleAsync(guildId, userId, want, true, RoleReason, ct).ConfigureAwait(false);

            if (outcome.Done)
            {
                supporter.GivenDiscordRoleId = want;
                supporter.GivenDiscordUserId = userId;
                tally.Given++;
            }
            else if (outcome.NotInServer)
            {
                return ("Not in the Discord server yet.", true);
            }
            else
            {
                return (outcome.RoleGone ? "That Discord role does not exist any more." : outcome.Error ?? "Discord refused to give the role.", false);
            }
        }

        return (null, false);
    }

    /// <summary>The same for the group role, on the VRChat account linked to the supporter's Discord account.</summary>
    private async Task<string?> ChangeVRChatAsync(
        Supporter supporter, string? wantRole, string? wantUser, bool alreadyHeld, Tally tally, CancellationToken ct)
    {
        var wantNothing = wantUser is null || wantRole is null;

        if (supporter.GivenVRChatRoleId is { } givenRole
            && supporter.GivenVRChatUserId is { } givenUser
            && (wantNothing || givenRole != wantRole || givenUser != wantUser))
        {
            var outcome = await vrchat.TakeGroupRoleAsync(givenUser, givenRole, ct).ConfigureAwait(false);

            if (!outcome.Done)
                return outcome.Error ?? "VRChat refused to take the group role away.";

            tally.Taken++;
            supporter.GivenVRChatRoleId = null;
            supporter.GivenVRChatUserId = null;
        }
        else if (supporter.GivenVRChatRoleId is null != supporter.GivenVRChatUserId is null)
        {
            // Half a record cannot be acted on; start clean.
            supporter.GivenVRChatRoleId = null;
            supporter.GivenVRChatUserId = null;
        }

        if (!wantNothing && supporter.GivenVRChatRoleId is null && !alreadyHeld)
        {
            var outcome = await vrchat.GiveGroupRoleAsync(wantUser!, wantRole!, ct).ConfigureAwait(false);

            if (!outcome.Done)
                return outcome.Error ?? "VRChat refused to give the group role.";

            supporter.GivenVRChatRoleId = wantRole;
            supporter.GivenVRChatUserId = wantUser;
            tally.Given++;
        }

        return null;
    }

    /// <summary>Whether the person already holds the group role, as the group's member list last said.</summary>
    private async Task<bool> HoldsGroupRoleAsync(string groupId, string userId, string roleId, CancellationToken ct)
    {
        var json = await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId && m.UserId == userId && m.LeftAt == null)
            .Select(m => m.Roles)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return json is not null && RoleIds(json).Contains(roleId);
    }

    private static HashSet<string> RoleIds(string json)
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

    private async Task ThankAsync(
        IDiscordGateway gateway,
        Supporter supporter,
        SupporterTier? tier,
        string? template,
        DateTimeOffset now,
        Tally tally,
        CancellationToken ct)
    {
        // No message set: mark it done, so writing one later does not thank everybody who paid before.
        if (string.IsNullOrWhiteSpace(template) || tier is null)
        {
            supporter.ThanksSentAt = now;
            return;
        }

        if (tally.Thanked >= MaxThanksPerPass)
        {
            tally.Left++;
            return;
        }

        var text = SupporterRules.Thanks(template, supporter.Name, tier.Name);
        var outcome = await gateway.SendDirectMessageAsync(supporter.DiscordUserId!, text, null, ct).ConfigureAwait(false);

        // Discord asked to slow down: the message waits, nothing is marked.
        if (outcome.RateLimited)
        {
            supporter.TryAgainAt = now + TryAgainAfter;
            return;
        }

        // One try. Somebody who does not accept messages from the server is not asked again.
        supporter.ThanksSentAt = now;

        if (outcome.Sent)
            tally.Thanked++;
        else if (supporter.Problem is null)
            supporter.Problem = Clip(outcome.DirectMessagesClosed
                ? "The thank-you was not sent: they do not accept direct messages from the server."
                : "The thank-you was not sent: " + (outcome.Error ?? "Discord refused it."));
    }

    /// <summary>The column holds <see cref="MostProblemLength"/> characters; a longer refusal would fail the save on every pass.</summary>
    private static string? Clip(string? text)
        => text is { Length: > MostProblemLength } ? text[..MostProblemLength] : text;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class Tally
    {
        public int Given;
        public int Taken;
        public int Thanked;
        public int Left;
        public string? Problem;
    }
}
