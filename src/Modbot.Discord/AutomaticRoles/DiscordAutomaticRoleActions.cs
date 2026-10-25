using Modbot.Core.Discord;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.AutomaticRoles;

/// <summary>
/// <see cref="IDiscordAutomaticRoleActions"/> through the bot's live session.
/// </summary>
/// <param name="readyGateway">The bot's session while it is ready, or null.</param>
public sealed class DiscordAutomaticRoleActions(Func<IDiscordGateway?> readyGateway) : IDiscordAutomaticRoleActions
{
    public Task<DiscordAutomaticRoleOutcome> AddRoleAsync(
        string guildId, string userId, string roleId, string reason, CancellationToken ct = default)
        => ChangeAsync(guildId, userId, roleId, add: true, reason, ct);

    public Task<DiscordAutomaticRoleOutcome> RemoveRoleAsync(
        string guildId, string userId, string roleId, string reason, CancellationToken ct = default)
        => ChangeAsync(guildId, userId, roleId, add: false, reason, ct);

    private async Task<DiscordAutomaticRoleOutcome> ChangeAsync(
        string guildId, string userId, string roleId, bool add, string reason, CancellationToken ct)
    {
        if (readyGateway() is not { } gateway)
            return DiscordAutomaticRoleOutcome.Offline;

        // The gateway notes a role it gives as Modbot's own, so the permission watch stays quiet.
        var outcome = await gateway.ChangeRoleAsync(guildId, userId, roleId, add, reason, ct).ConfigureAwait(false);

        if (outcome.Done)
            return DiscordAutomaticRoleOutcome.Ok;

        if (outcome.NotInServer)
            return DiscordAutomaticRoleOutcome.Left;

        return outcome.RoleGone
            ? DiscordAutomaticRoleOutcome.Gone
            : DiscordAutomaticRoleOutcome.Failed(outcome.Error ?? "Discord refused.");
    }
}
