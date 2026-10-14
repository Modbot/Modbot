using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Discord;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Snapshots;

/// <summary>
/// <see cref="IDiscordSnapshotActions"/> through the bot's live session: the snapshot page's read of
/// the server's roles and channels, and its putting back of a role's or a channel's permissions.
/// </summary>
/// <param name="readyGateway">The bot's session while it is ready, or null.</param>
/// <param name="scopes">Opens the database for the one call.</param>
public sealed class DiscordSnapshotActions(Func<IDiscordGateway?> readyGateway, IServiceScopeFactory scopes) : IDiscordSnapshotActions
{
    public async Task<DiscordStructureOutcome> ReadAsync(CancellationToken ct = default)
    {
        if (readyGateway() is not { } gateway)
            return DiscordStructureOutcome.Offline;

        if (await GuildAsync(ct).ConfigureAwait(false) is not { } guildId)
            return DiscordStructureOutcome.Failed("No Discord server is set.");

        return gateway.ReadStructure(guildId) is { } structure
            ? new DiscordStructureOutcome(structure, null)
            : DiscordStructureOutcome.Failed("The bot is not in that server.");
    }

    public async Task<DiscordEditResult> SetRolePermissionsAsync(
        string roleId, long permissions, string reason, CancellationToken ct = default)
    {
        if (readyGateway() is not { } gateway)
            return DiscordEditResult.Offline;

        if (await GuildAsync(ct).ConfigureAwait(false) is not { } guildId)
            return DiscordEditResult.Failed("No Discord server is set.");

        return await gateway.SetRolePermissionsAsync(guildId, roleId, permissions, reason, ct).ConfigureAwait(false);
    }

    public async Task<DiscordEditResult> SetChannelOverwritesAsync(
        string channelId,
        IReadOnlyList<SnapshotOverwriteSet> set,
        IReadOnlyList<string> removeRoleIds,
        string reason,
        CancellationToken ct = default)
    {
        if (readyGateway() is not { } gateway)
            return DiscordEditResult.Offline;

        if (await GuildAsync(ct).ConfigureAwait(false) is not { } guildId)
            return DiscordEditResult.Failed("No Discord server is set.");

        return await gateway.SetChannelOverwritesAsync(guildId, channelId, set, removeRoleIds, reason, ct).ConfigureAwait(false);
    }

    private async Task<string?> GuildAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ModbotContext>().Settings
            .AsNoTracking()
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}
