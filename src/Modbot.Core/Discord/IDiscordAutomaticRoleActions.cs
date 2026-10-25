namespace Modbot.Core.Discord;

/// <summary>What giving or taking an automatic role in Discord came to.</summary>
/// <param name="BotOffline">The bot has no session, so nothing was tried.</param>
/// <param name="NotInServer">The person is not in the server.</param>
/// <param name="RoleGone">The role no longer exists in the server.</param>
public sealed record DiscordAutomaticRoleOutcome(
    bool Done,
    string? Error,
    bool BotOffline = false,
    bool NotInServer = false,
    bool RoleGone = false)
{
    public static DiscordAutomaticRoleOutcome Ok { get; } = new(true, null);

    public static DiscordAutomaticRoleOutcome Offline { get; } = new(false, "The Discord bot is not connected.", BotOffline: true);

    public static DiscordAutomaticRoleOutcome Left { get; } = new(false, "That member is not in the server.", NotInServer: true);

    public static DiscordAutomaticRoleOutcome Gone { get; } = new(false, "That role does not exist any more.", RoleGone: true);

    public static DiscordAutomaticRoleOutcome Failed(string error) => new(false, error);
}

/// <summary>
/// What the automatic roles job does in Discord: give a role, take it away.
/// </summary>
/// <remarks>
/// In Core so the API can ask without depending on the bot, as it does through
/// <see cref="IDiscordBirthdayActions"/>. Each is one call to Discord and is never repeated by the
/// caller. The reason goes to the server's own audit log. A role given here is recorded by the bot
/// as Modbot's own, so the permission watch does not report it.
/// </remarks>
public interface IDiscordAutomaticRoleActions
{
    /// <summary>Gives a member the role.</summary>
    Task<DiscordAutomaticRoleOutcome> AddRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct = default);

    /// <summary>Takes the role away from a member.</summary>
    Task<DiscordAutomaticRoleOutcome> RemoveRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. Every action answers that the bot is not connected.</summary>
public sealed class NoDiscordAutomaticRoleActions : IDiscordAutomaticRoleActions
{
    public Task<DiscordAutomaticRoleOutcome> AddRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct = default)
        => Task.FromResult(DiscordAutomaticRoleOutcome.Offline);

    public Task<DiscordAutomaticRoleOutcome> RemoveRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct = default)
        => Task.FromResult(DiscordAutomaticRoleOutcome.Offline);
}
