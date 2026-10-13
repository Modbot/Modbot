using Modbot.Core.Data.Entities;

namespace Modbot.Core.Discord;

/// <summary>What posting, rewriting or deleting a role menu's message came to.</summary>
/// <param name="MessageId">The message that now carries the menu, when it was posted or rewritten.</param>
/// <param name="BotOffline">The bot has no session, so nothing was tried.</param>
/// <param name="Gone">The message to rewrite or delete no longer exists.</param>
public sealed record RoleMenuSiteOutcome(bool Done, string? Error, string? MessageId = null, bool BotOffline = false, bool Gone = false)
{
    public static RoleMenuSiteOutcome Offline { get; } = new(false, "The Discord bot is not connected.", BotOffline: true);

    public static RoleMenuSiteOutcome Ok(string? messageId = null) => new(true, null, messageId);

    public static RoleMenuSiteOutcome Failed(string error, bool gone = false) => new(false, error, Gone: gone);
}

/// <summary>
/// What the Modbot page does to a role menu's message on Discord: post it, rewrite it, delete it.
/// </summary>
/// <remarks>
/// In Core so the API can ask without depending on the bot, as it does through
/// <see cref="Posts.IDiscordPostActions"/>. Each is one call to Discord, made at once, so the person
/// sees whether it worked. The API writes the row and the record of the change.
/// </remarks>
public interface IDiscordRoleMenuActions
{
    /// <summary>
    /// Posts the menu, or rewrites its message when it has one. A message somebody deleted comes
    /// back <see cref="RoleMenuSiteOutcome.Gone"/>, and the caller posts it again.
    /// </summary>
    Task<RoleMenuSiteOutcome> PostAsync(DiscordRoleMenu menu, CancellationToken ct = default);

    /// <summary>Deletes the menu's message. One that is already gone counts as done.</summary>
    Task<RoleMenuSiteOutcome> DeleteAsync(string channelId, string messageId, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. Every action answers that the bot is not connected.</summary>
public sealed class NoDiscordRoleMenuActions : IDiscordRoleMenuActions
{
    public Task<RoleMenuSiteOutcome> PostAsync(DiscordRoleMenu menu, CancellationToken ct = default)
        => Task.FromResult(RoleMenuSiteOutcome.Offline);

    public Task<RoleMenuSiteOutcome> DeleteAsync(string channelId, string messageId, CancellationToken ct = default)
        => Task.FromResult(RoleMenuSiteOutcome.Offline);
}
