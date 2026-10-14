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
/// when given. A role somebody gave by hand, or that a supporter held before, is never touched.
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

        // Everybody who holds roles or should: the supporters who are paid up and matched, and anybody
        // whose roles are still to be taken back.
        var supporters = await db.Supporters
            .Where(s => s.DiscordUserId != null
                && (s.State == SupporterStates.Active || s.GivenDiscordRoleId != null || s.GivenVRChatRoleId != null))
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

        var tally = new Tally();

        foreach (var supporter in supporters)
        {
            if (supporter.State == SupporterStates.Active && SupporterRules.HasLapsed(supporter.PaidThrough, now))
            {
                supporter.State = SupporterStates.Ended;
                supporter.UpdatedAt = now;
            }

            var tier = supporter.State == SupporterStates.Active && supporter.TierId is { } tierId
                ? tiers.GetValueOrDefault(tierId)
                : null;

            var wantDiscord = Blank(tier?.DiscordRoleId);
            var wantVRChat = groupId is null ? null : Blank(tier?.VRChatRoleId);
            var wantUser = wantVRChat is null
                ? null
                : await db.LinkedVRChatUserIdAsync(supporter.DiscordUserId!, ct).ConfigureAwait(false);

            var discordOutOfStep = supporter.GivenDiscordRoleId != wantDiscord;
            var vrchatOutOfStep = supporter.GivenVRChatRoleId != (wantUser is null ? null : wantVRChat)
                || supporter.GivenVRChatUserId != wantUser;

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
                    (problem, notInServer) = await ChangeDiscordAsync(gateway, guildId, supporter, wantDiscord, roles, tally, ct).ConfigureAwait(false);

                if (vrchatOutOfStep && problem is null)
                    problem = await ChangeVRChatAsync(supporter, wantVRChat, wantUser, tally, ct).ConfigureAwait(false);

                supporter.Problem = problem;
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

    /// <summary>Takes back the role Modbot gave and gives the one that is due. Returns the refusal, if any, and whether the person is not in the server.</summary>
    private async Task<(string? Problem, bool NotInServer)> ChangeDiscordAsync(
        IDiscordGateway gateway,
        string guildId,
        Supporter supporter,
        string? want,
        IReadOnlyDictionary<string, DiscordRole> roles,
        Tally tally,
        CancellationToken ct)
    {
        var userId = supporter.DiscordUserId!;

        if (supporter.GivenDiscordRoleId is { } given && given != want)
        {
            var outcome = await gateway.ChangeRoleAsync(guildId, userId, given, false, RoleReason, ct).ConfigureAwait(false);

            // A person who left, or a role that is gone, has nothing left to take.
            if (!outcome.Done && !outcome.NotInServer && !outcome.RoleGone)
                return (outcome.Error ?? "Discord refused to take the role away.", false);

            if (outcome.Done)
                tally.Taken++;

            supporter.GivenDiscordRoleId = null;
        }

        if (want is not null && supporter.GivenDiscordRoleId != want)
        {
            // A role the bot cannot assign is named here instead of discovered as a refusal each time.
            if (roles.TryGetValue(want, out var role) && !role.BotCanAssign)
                return ($"The bot cannot assign {role.Name}. Give it Manage Roles and keep its own role above that one.", false);

            var outcome = await gateway.ChangeRoleAsync(guildId, userId, want, true, RoleReason, ct).ConfigureAwait(false);

            if (outcome.Done)
            {
                supporter.GivenDiscordRoleId = want;
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
        Supporter supporter, string? wantRole, string? wantUser, Tally tally, CancellationToken ct)
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

        if (!wantNothing && supporter.GivenVRChatRoleId is null)
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
            supporter.Problem = outcome.DirectMessagesClosed
                ? "The thank-you was not sent: they do not accept direct messages from the server."
                : "The thank-you was not sent: " + (outcome.Error ?? "Discord refused it.");
    }

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
