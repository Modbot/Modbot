using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.AutomaticRoles;

/// <summary>A role a rule gives, by its name where Modbot knows it and its id where it does not.</summary>
/// <param name="Id">The VRChat group role id or the Discord role id.</param>
/// <param name="Name">The role's name, or the id when the name is not known.</param>
public sealed record AutomaticRoleName(string Id, string Name);

/// <summary>
/// The names of the VRChat and Discord roles that rules give, read from what Modbot has already
/// stored (the group info poll and the server's role list), so nothing is asked of VRChat or Discord.
/// </summary>
public sealed class AutomaticRoleNames
{
    private readonly Dictionary<string, string> _vrchat;
    private readonly Dictionary<string, string> _discord;

    private AutomaticRoleNames(Dictionary<string, string> vrchat, Dictionary<string, string> discord)
    {
        _vrchat = vrchat;
        _discord = discord;
    }

    public static async Task<AutomaticRoleNames> ReadAsync(ModbotContext db, IEnumerable<AutomaticRole> rules, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rules);

        var settings = await db.GetSettingsAsync(ct);

        var vrchat = (GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot)?.Roles ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name!, StringComparer.Ordinal);

        var discordIds = rules.Select(r => r.DiscordRoleId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var discord = settings.DiscordGuildId is { Length: > 0 } guildId && discordIds.Count > 0
            ? await db.DiscordRoles.AsNoTracking()
                .Where(r => r.GuildId == guildId && discordIds.Contains(r.RoleId))
                .ToDictionaryAsync(r => r.RoleId, r => r.Name, StringComparer.Ordinal, ct)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return new AutomaticRoleNames(vrchat, discord);
    }

    public AutomaticRoleName? VRChat(string? roleId)
        => roleId is null ? null : new AutomaticRoleName(roleId, _vrchat.GetValueOrDefault(roleId, roleId));

    public AutomaticRoleName? Discord(string? roleId)
        => roleId is null ? null : new AutomaticRoleName(roleId, _discord.GetValueOrDefault(roleId, roleId));
}
