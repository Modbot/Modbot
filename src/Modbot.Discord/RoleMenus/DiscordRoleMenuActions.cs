using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.RoleMenus;

/// <summary>
/// <see cref="IDiscordRoleMenuActions"/> through the bot's live session: the Modbot page's Post and
/// Delete on a role menu.
/// </summary>
/// <param name="readyGateway">The bot's session while it is ready, or null.</param>
public sealed class DiscordRoleMenuActions(Func<IDiscordGateway?> readyGateway) : IDiscordRoleMenuActions
{
    public async Task<RoleMenuSiteOutcome> PostAsync(DiscordRoleMenu menu, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(menu);

        if (readyGateway() is not { } gateway)
            return RoleMenuSiteOutcome.Offline;

        var buttons = RoleMenuMessage.Buttons(menu);
        var select = RoleMenuMessage.Select(menu);

        var outcome = menu.MessageId is { } messageId
            ? await gateway.EditMenuAsync(menu.ChannelId, messageId, menu.Text, buttons, select, ct).ConfigureAwait(false)
            : await gateway.PostMenuAsync(menu.ChannelId, menu.Text, buttons, select, ct).ConfigureAwait(false);

        return outcome.Sent
            ? RoleMenuSiteOutcome.Ok(outcome.MessageId)
            : RoleMenuSiteOutcome.Failed(outcome.Error ?? "Discord refused.", gone: menu.MessageId is not null && outcome.NotFound);
    }

    public async Task<RoleMenuSiteOutcome> DeleteAsync(string channelId, string messageId, CancellationToken ct = default)
    {
        if (readyGateway() is not { } gateway)
            return RoleMenuSiteOutcome.Offline;

        var outcome = await gateway
            .DeleteMessageAsync(channelId, messageId, "Modbot: role menu deleted", ct)
            .ConfigureAwait(false);

        // A message already gone is what a delete wanted.
        return outcome.Sent || outcome.NotFound
            ? RoleMenuSiteOutcome.Ok()
            : RoleMenuSiteOutcome.Failed(outcome.Error ?? "Discord refused.");
    }
}
