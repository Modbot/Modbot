namespace Modbot.Core.Discord;

/// <summary>What changing a member's role, or deleting a ticket, for an 18+ check came to.</summary>
/// <param name="Done">Discord did it.</param>
/// <param name="Error">What went wrong, in a sentence, when it was not done.</param>
/// <param name="NotInServer">Discord does not know the member in the server.</param>
/// <param name="NotAllowed">Modbot would not delete this channel: it is not a ticket, or not in the chosen category.</param>
public sealed record DiscordAgeOutcome(bool Done, string? Error, bool NotInServer = false, bool NotAllowed = false)
{
    public static DiscordAgeOutcome Ok { get; } = new(true, null);

    public static DiscordAgeOutcome Offline { get; } = new(false, "The Discord bot is not connected.");

    public static DiscordAgeOutcome Left { get; } = new(false, "That member is not in the server.", NotInServer: true);

    public static DiscordAgeOutcome Failed(string error) => new(false, error);
}

/// <summary>What the 18+ check does in Discord: give or take a role, and delete the ticket.</summary>
/// <remarks>
/// In Core so the API can ask without depending on the bot, as it does through
/// <see cref="IDiscordBirthdayActions"/>. A role given here is recorded by the bot as Modbot's own,
/// so the permission watch does not report it.
/// </remarks>
public interface IDiscordAgeActions
{
    /// <summary>Gives a member the role, or takes it away.</summary>
    Task<DiscordAgeOutcome> ChangeRoleAsync(string guildId, string userId, string roleId, bool add, CancellationToken ct = default);

    /// <summary>
    /// Deletes the channel the check was typed in, but only if it is a Modbot ticket thread or sits
    /// inside the category the server picked. Any other channel is left alone and the answer says so.
    /// A ticket thread's deletion is logged like the ticket's own Delete button, with the checker as actor.
    /// </summary>
    /// <param name="channelId">The channel the check was typed in.</param>
    /// <param name="actorDiscordUserId">The staff member who ran the check.</param>
    Task<DiscordAgeOutcome> DeleteTicketAsync(string channelId, string actorDiscordUserId, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. Every action answers that the bot is not connected.</summary>
public sealed class NoDiscordAgeActions : IDiscordAgeActions
{
    public Task<DiscordAgeOutcome> ChangeRoleAsync(string guildId, string userId, string roleId, bool add, CancellationToken ct = default)
        => Task.FromResult(DiscordAgeOutcome.Offline);

    public Task<DiscordAgeOutcome> DeleteTicketAsync(string channelId, string actorDiscordUserId, CancellationToken ct = default)
        => Task.FromResult(DiscordAgeOutcome.Offline);
}
