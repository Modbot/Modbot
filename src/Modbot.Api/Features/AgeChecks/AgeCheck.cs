using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Auth.VRChatLink;
using Modbot.Api.Features.GroupPage;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.VRChat;
using Modbot.VRChat.Moderation;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Users;
using SettingsRow = Modbot.Core.Data.Entities.Settings;

namespace Modbot.Api.Features.AgeChecks;

/// <summary>
/// The 18+ check: sets the flag, gives and takes the roles the server chose, and logs method, date
/// and checker. <c>/ageverified</c> in a ticket and the profile's button both call it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The flag comes first and stays.</strong> A role that cannot be changed is a line in the
/// answer, not a reason to undo the check: the person was checked. Nothing about the ID itself is
/// kept, only the method, the date (the fact's) and the checker (its actor).
/// </para>
/// <para>
/// <strong>Which VRChat account.</strong> A Discord member with a linked account is that account,
/// and a different one typed alongside is refused. A member with no link needs their VRChat profile
/// link, taken as typed (an id is never validated, foundation §3.1.1). From a profile there is no
/// Discord member typed, so it is whoever is linked to the account, if anybody.
/// </para>
/// <para>
/// <strong>Never retried.</strong> The Discord roles stop at the first refusal; the VRChat group
/// role is one request at interactive priority, and a 429 is told as it is (foundation §4.3.1).
/// </para>
/// </remarks>
public sealed class AgeCheck(
    ModbotContext db,
    IModbotClock clock,
    IDiscordAgeActions discord,
    VRChatUserProfiles? profiles = null,
    GroupRoles? groupRoles = null,
    IFactWriter? facts = null,
    EventPartitionMaintainer? partitions = null) : IAgeCheck
{
    public const string NotSetUpMessage = "User records are not available in this process.";

    public async Task<AgeCheckAnswer> RunAsync(AgeCheckRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!AgeCheckMethods.IsKnown(request.Method))
            return AgeCheckAnswer.Refused("Pick badge or photo ID.");

        if (profiles is null)
            return AgeCheckAnswer.Refused(NotSetUpMessage);

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        var typed = VRChatLinkEndpoints.ParseUserIdOrUrl(request.VRChatUser);

        var (vrchatId, discordId, refusal) = await WhoAsync(request, typed, ct).ConfigureAwait(false);

        if (refusal is not null || vrchatId is null)
            return AgeCheckAnswer.Refused(refusal ?? "Say which VRChat account.");

        await profiles.SetAgeFlagAsync(vrchatId, true, request.CheckerId, null, request.Method, ct).ConfigureAwait(false);

        var lines = new List<string> { $"Marked 18+ verified ({AgeCheckMethods.Label(request.Method)})." };

        await ChangeDiscordRolesAsync(settings, discordId, lines, ct).ConfigureAwait(false);
        await GiveGroupRoleAsync(settings, vrchatId, request.CheckerId, lines, ct).ConfigureAwait(false);

        return new AgeCheckAnswer(true, lines);
    }

    /// <summary>The VRChat account and the Discord member the check is about, or why it cannot be.</summary>
    private async Task<(string? VRChatId, string? DiscordId, string? Refusal)> WhoAsync(
        AgeCheckRequest request, string? typed, CancellationToken ct)
    {
        if (request.DiscordUserId is { Length: > 0 } discordId)
        {
            var linked = await db.DiscordAccountLinks.AsNoTracking()
                .Where(l => l.DiscordUserId == discordId && l.UnlinkedAt == null)
                .Select(l => l.VRChatUserId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (linked is not null)
            {
                // Compared as text: the id is opaque (foundation §3.1.1).
                return typed is not null && !string.Equals(typed, linked, StringComparison.Ordinal)
                    ? (null, null, "That VRChat profile is not the one linked to this member.")
                    : (linked, discordId, null);
            }

            return typed is null
                ? (null, null, "This member has no linked VRChat account. Add their VRChat profile link.")
                : (typed, discordId, null);
        }

        if (typed is null)
            return (null, null, "Say which VRChat account.");

        var member = await db.DiscordAccountLinks.AsNoTracking()
            .Where(l => l.VRChatUserId == typed && l.UnlinkedAt == null)
            .Select(l => l.DiscordUserId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return (typed, member, null);
    }

    private async Task ChangeDiscordRolesAsync(SettingsRow? settings, string? discordId, List<string> lines, CancellationToken ct)
    {
        var give = settings?.AgeCheckGiveRoleIds ?? [];
        var remove = settings?.AgeCheckRemoveRoleIds ?? [];

        if (give.Count + remove.Count == 0)
            return;

        if (discordId is null || settings?.DiscordGuildId is not { Length: > 0 } guildId)
        {
            lines.Add("No Discord account is linked to this person, so no Discord roles were changed.");
            return;
        }

        var gave = new List<string>();
        var took = new List<string>();

        foreach (var roleId in give)
        {
            var name = await NameAsync(roleId, ct).ConfigureAwait(false);

            // A role can gain a permission after it was chosen: look again before giving it.
            if (await AgeCheckRoleCheck.RefusalAsync(db, roleId, ct).ConfigureAwait(false) is { } refusal)
            {
                lines.Add($"Did not give {name}: {refusal}");
                continue;
            }

            var outcome = await discord.ChangeRoleAsync(guildId, discordId, roleId, add: true, ct).ConfigureAwait(false);

            if (!outcome.Done)
            {
                lines.Add(DiscordRefusal(outcome, $"give {name}", gave, took));
                return;
            }

            gave.Add(name);
        }

        foreach (var roleId in remove)
        {
            var name = await NameAsync(roleId, ct).ConfigureAwait(false);
            var outcome = await discord.ChangeRoleAsync(guildId, discordId, roleId, add: false, ct).ConfigureAwait(false);

            if (!outcome.Done)
            {
                lines.Add(DiscordRefusal(outcome, $"take away {name}", gave, took));
                return;
            }

            took.Add(name);
        }

        if (gave.Count > 0)
            lines.Add($"Gave {string.Join(", ", gave)}.");

        if (took.Count > 0)
            lines.Add($"Took away {string.Join(", ", took)}.");
    }

    /// <summary>What the first Discord refusal says, with what was changed before it.</summary>
    private static string DiscordRefusal(DiscordAgeOutcome outcome, string what, List<string> gave, List<string> took)
    {
        var before = new List<string>();

        if (gave.Count > 0)
            before.Add($"gave {string.Join(", ", gave)}");

        if (took.Count > 0)
            before.Add($"took away {string.Join(", ", took)}");

        var said = outcome.NotInServer
            ? "That member is not in the Discord server, so the rest of the roles were left."
            : $"Could not {what}: {outcome.Error} The rest of the roles were left.";

        return before.Count == 0 ? said : $"Before that, {string.Join(" and ", before)}. {said}";
    }

    private async Task GiveGroupRoleAsync(SettingsRow? settings, string vrchatId, Guid checkerId, List<string> lines, CancellationToken ct)
    {
        if (settings?.AgeCheckGroupRoleId is not { Length: > 0 } roleId)
            return;

        if (groupRoles is null || facts is null || partitions is null)
        {
            lines.Add("Could not give the VRChat group role: this process is not set up to act in VRChat.");
            return;
        }

        if (settings.ManagedGroupId is not { Length: > 0 } groupId)
        {
            lines.Add("Could not give the VRChat group role: no VRChat group is set up yet.");
            return;
        }

        var roleName = GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot)?.Roles
            .FirstOrDefault(r => string.Equals(r.Id, roleId, StringComparison.Ordinal))?.Name;

        // One request, never retried; a refusal is VRChat's own words.
        var answer = await groupRoles.GiveAsync(groupId, vrchatId, roleId, ct, VRChatCallPriority.Interactive).ConfigureAwait(false);

        if (!answer.Success)
        {
            lines.Add($"Could not give the VRChat group role {roleName ?? roleId}: {GroupPageAnswers.Said(answer)}");
            return;
        }

        await GroupPageAnswers.WriteFactAsync(
                facts, partitions, FactType.GroupRoleGiven, groupId, checkerId, clock.UtcNow,
                new JsonObject
                {
                    ["groupId"] = groupId,
                    ["roleId"] = roleId,
                    ["roleName"] = roleName,
                },
                ct,
                subjectId: vrchatId)
            .ConfigureAwait(false);

        lines.Add($"Gave the VRChat group role {roleName ?? roleId}.");
    }

    private async Task<string> NameAsync(string roleId, CancellationToken ct)
        => await AgeCheckRoleCheck.NameOfAsync(db, roleId, ct).ConfigureAwait(false) ?? roleId;
}
