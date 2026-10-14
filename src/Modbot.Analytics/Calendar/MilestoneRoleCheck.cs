using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Discord;

namespace Modbot.Analytics.Calendar;

/// <summary>
/// Whether a role may be the Regular role, and whether Modbot can give it. Used when the setting is
/// saved and again by each pass, since a role can gain a permission after it was chosen.
/// </summary>
public static class MilestoneRoleCheck
{
    /// <summary>
    /// Why this role may not be the Regular role, or null when it may: it must be in the server, not
    /// @everyone, not owned by a bot or an integration, and hold no permission the permission watch
    /// tells staff about (<see cref="DiscordDangerousPermissions"/>).
    /// </summary>
    public static async Task<string?> RefusalAsync(ModbotContext db, string roleId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var role = await FindAsync(db, roleId, ct).ConfigureAwait(false);

        if (role is null || role.RemovedAt is not null)
            return "That role is not in the Discord server.";

        if (role.Everyone || role.RoleId == role.GuildId)
            return "@everyone cannot be the Regular role.";

        if (role.Managed)
            return $"{role.Name} belongs to a bot or an integration, so nobody can be given it.";

        if (role.Permissions is not { } permissions)
            return $"Modbot has not read {role.Name}'s permissions yet. Try again in a minute.";

        if (DiscordDangerousPermissions.In(permissions) != 0)
        {
            var names = string.Join(", ", DiscordDangerousPermissions.Names(permissions));
            return $"{role.Name} holds {names}, so Modbot will not give it to Regulars.";
        }

        return null;
    }

    /// <summary>
    /// Why Modbot cannot give this role right now, or null when it can: the bot needs Manage Roles
    /// and its own highest role above this one. Fixed in Discord, so the role stays chosen.
    /// </summary>
    public static async Task<string?> CannotGiveAsync(ModbotContext db, string roleId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var role = await FindAsync(db, roleId, ct).ConfigureAwait(false);

        return role is { RemovedAt: null, BotCanAssign: false }
            ? $"Modbot cannot give {role.Name}. It needs Manage Roles, and its own role must be above {role.Name}."
            : null;
    }

    private static async Task<Core.Data.Entities.DiscordRole?> FindAsync(ModbotContext db, string roleId, CancellationToken ct)
    {
        var guildId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return guildId is null
            ? null
            : await db.DiscordRoles.AsNoTracking()
                .FirstOrDefaultAsync(r => r.GuildId == guildId && r.RoleId == roleId, ct)
                .ConfigureAwait(false);
    }
}
