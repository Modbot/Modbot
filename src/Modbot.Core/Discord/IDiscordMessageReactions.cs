namespace Modbot.Core.Discord;

/// <summary>
/// Puts a reaction on a message already in a Discord channel, as the deployment's bot: the tick on a
/// picture whose submission staff approved for the group gallery.
/// </summary>
/// <remarks>
/// In Core so the API can ask without depending on the bot, as it does through
/// <see cref="Posts.IDiscordPostActions"/>. The reaction is a courtesy: the answer says whether it
/// went on, and nothing depends on it.
/// </remarks>
public interface IDiscordMessageReactions
{
    /// <summary>Reacts with <paramref name="emoji"/>. False when the bot is offline or Discord refused.</summary>
    Task<bool> AddAsync(string channelId, string messageId, string emoji, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. No reaction goes on.</summary>
public sealed class NoDiscordMessageReactions : IDiscordMessageReactions
{
    public Task<bool> AddAsync(string channelId, string messageId, string emoji, CancellationToken ct = default)
        => Task.FromResult(false);
}
