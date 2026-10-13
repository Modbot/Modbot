using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.RoleMenus;

/// <summary>
/// What happens when somebody presses a role menu's button or picks from its drop-down list: the
/// role is given, or taken back if they already hold it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The message is only a handle.</strong> The press names a menu and a role by id; the menu
/// and what its roles are allowed to be are read again from Modbot's own tables. A role dropped from
/// the menu since the message was posted is ignored, and a role that has gained a dangerous
/// permission since is refused (the permission watch's list,
/// <see cref="DiscordDangerousPermissions"/>), however the menu was saved.
/// </para>
/// <para>
/// <strong>Taking is always allowed.</strong> Only giving is checked: somebody holding a role the
/// menu once handed out can always hand it back.
/// </para>
/// <para>
/// <strong>Pick one.</strong> On a menu set to pick one, giving a role takes away the other roles of
/// that menu the member holds, after the new one went through, so a refused role never costs them
/// the one they had.
/// </para>
/// </remarks>
public sealed class RoleMenuPress
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;

    public RoleMenuPress(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions)
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

    public async Task<DiscordReply> HandleAsync(IDiscordGateway? gateway, DiscordButtonPress press, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(press);

        if (!RoleMenuMessage.TryRead(press.ButtonId, out var menuId, out var buttonRoleId))
            return DiscordReply.Say("That menu does not work any more.");

        var menu = await _db.DiscordRoleMenus.AsNoTracking()
            .Include(m => m.Roles)
            .FirstOrDefaultAsync(m => m.Id == menuId, ct)
            .ConfigureAwait(false);

        if (menu is null)
            return DiscordReply.Say("That menu is gone.");

        var guildId = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(guildId))
            return DiscordReply.Say("Modbot is not set up for this server.");

        if (gateway is null)
            return DiscordReply.Say("Modbot is not connected to Discord right now. Try again in a minute.");

        if (press.MemberRoleIds is not { } heldNow)
            return DiscordReply.Say("Modbot could not read your roles. Try again.");

        guildId = guildId.Trim();

        // What was asked for: the button's role, or every choice picked in the list. Roles no longer
        // on the menu are dropped.
        var asked = (buttonRoleId is not null ? [buttonRoleId] : press.Values)
            .Distinct(StringComparer.Ordinal)
            .Where(id => menu.Roles.Any(r => r.RoleId == id))
            .ToList();

        if (asked.Count == 0)
            return DiscordReply.Say("That choice is not on the menu any more.");

        var held = new HashSet<string>(heldNow, StringComparer.Ordinal);
        var menuRoleIds = menu.Roles.Select(r => r.RoleId).ToList();

        var known = await _db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId && menuRoleIds.Contains(r.RoleId))
            .ToDictionaryAsync(r => r.RoleId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var lines = new List<string>();
        var reason = $"Modbot: role menu “{menu.Name}”";

        foreach (var roleId in asked)
        {
            var label = Name(menu, known, roleId);

            if (held.Contains(roleId))
            {
                if (await ChangeAsync(gateway, guildId, press, menu, roleId, label, add: false, reason, lines, ct).ConfigureAwait(false))
                    held.Remove(roleId);

                continue;
            }

            if (Refusal(known.GetValueOrDefault(roleId), label) is { } refusal)
            {
                lines.Add(refusal);
                continue;
            }

            if (!await ChangeAsync(gateway, guildId, press, menu, roleId, label, add: true, reason, lines, ct).ConfigureAwait(false))
                continue;

            held.Add(roleId);

            if (!menu.SingleChoice)
                continue;

            foreach (var other in menuRoleIds.Where(id => id != roleId && held.Contains(id)).ToList())
            {
                if (await ChangeAsync(gateway, guildId, press, menu, other, Name(menu, known, other), add: false, reason, lines, ct).ConfigureAwait(false))
                    held.Remove(other);
            }
        }

        return DiscordReply.Say(string.Join('\n', lines));
    }

    /// <summary>Why this role cannot be given from a menu, as a sentence, or null when it can.</summary>
    private static string? Refusal(DiscordRole? role, string label)
    {
        if (role is null || role.RemovedAt is not null)
            return $"“{label}” is not in the server any more.";

        if (role.Everyone || role.Managed || role.Permissions is not { } permissions)
            return $"Modbot cannot give you “{label}”.";

        if (DiscordDangerousPermissions.In(permissions) != 0)
            return $"Modbot does not give out “{label}” from a menu.";

        return null;
    }

    private static string Name(DiscordRoleMenu menu, Dictionary<string, DiscordRole> known, string roleId)
        => known.TryGetValue(roleId, out var role) && role.Name.Length > 0
            ? role.Name
            : menu.Roles.First(r => r.RoleId == roleId).Label;

    /// <summary>Gives or takes one role, adds the sentence to <paramref name="lines"/> and writes the fact. True when it went through.</summary>
    private async Task<bool> ChangeAsync(
        IDiscordGateway gateway,
        string guildId,
        DiscordButtonPress press,
        DiscordRoleMenu menu,
        string roleId,
        string label,
        bool add,
        string reason,
        List<string> lines,
        CancellationToken ct)
    {
        var outcome = await gateway.ChangeRoleAsync(guildId, press.DiscordUserId, roleId, add, reason, ct).ConfigureAwait(false);

        if (outcome.Done)
        {
            lines.Add(add ? $"You now have “{label}”." : $"Took “{label}” away from you.");

            await WriteAsync(
                    add ? FactType.RoleMenuRoleGiven : FactType.RoleMenuRoleTaken,
                    press.DiscordUserId,
                    new JsonObject
                    {
                        ["menuId"] = menu.Id.ToString(),
                        ["menuName"] = menu.Name,
                        ["roleId"] = roleId,
                        ["roleName"] = label,
                    },
                    ct)
                .ConfigureAwait(false);

            return true;
        }

        var verb = add ? "give you" : "take away";

        if (outcome.NotInServer)
            lines.Add("Discord says you are not in the server.");
        else if (outcome.RoleGone)
            lines.Add($"“{label}” is not in the server any more.");
        else
            lines.Add($"Could not {verb} “{label}”: {outcome.Error}");

        return false;
    }

    private async Task WriteAsync(string type, string subjectId, JsonObject data, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        await _facts.WriteAsync(new FactRecord
        {
            Type = type,
            OccurredAt = now,
            SubjectPlatform = FactPlatform.Discord,
            SubjectId = subjectId,
            Source = FactSource.Modbot,
            Data = data,
        }, ct).ConfigureAwait(false);
    }
}
