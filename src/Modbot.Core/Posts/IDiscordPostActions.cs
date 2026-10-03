namespace Modbot.Core.Posts;

/// <summary>Whether a change to a post on Discord went through, and if not, why.</summary>
/// <param name="Done">It happened, or there was nothing to do.</param>
/// <param name="Error">What went wrong, in a sentence, when it did not.</param>
/// <param name="BotOffline">The bot is not connected, so nothing was asked of Discord.</param>
/// <param name="Gone">Discord says the message is not there any more.</param>
public sealed record PostSiteOutcome(bool Done, string? Error, bool BotOffline = false, bool Gone = false)
{
    public static PostSiteOutcome Ok { get; } = new(true, null);

    public static PostSiteOutcome Offline { get; } = new(false, "The Discord bot is not connected.", BotOffline: true);

    public static PostSiteOutcome Failed(string error, bool gone = false) => new(false, error, Gone: gone);
}

/// <summary>
/// What a person does to a post already on Discord from the Marketing tab (posts design §4.5):
/// edit its text, delete it, or publish it to followers after that failed.
/// </summary>
/// <remarks>
/// In Core so the API can ask without depending on the bot, as it does through
/// <see cref="Discord.IJoinGateActions"/>. Each is one call to Discord, made at once, so the person
/// sees whether it worked. The API writes the row and the fact.
/// </remarks>
public interface IDiscordPostActions
{
    /// <summary>Rewrites the message's text. Its files stay, and nobody is pinged.</summary>
    Task<PostSiteOutcome> EditAsync(string channelId, string messageId, string text, CancellationToken ct = default);

    /// <summary>Deletes the message. One that is already gone counts as done.</summary>
    Task<PostSiteOutcome> DeleteAsync(string channelId, string messageId, string reason, CancellationToken ct = default);

    /// <summary>Publishes the message to the channel's followers.</summary>
    Task<PostSiteOutcome> PublishAsync(string channelId, string messageId, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. Every action answers that the bot is not connected.</summary>
public sealed class NoDiscordPostActions : IDiscordPostActions
{
    public Task<PostSiteOutcome> EditAsync(string channelId, string messageId, string text, CancellationToken ct = default)
        => Task.FromResult(PostSiteOutcome.Offline);

    public Task<PostSiteOutcome> DeleteAsync(string channelId, string messageId, string reason, CancellationToken ct = default)
        => Task.FromResult(PostSiteOutcome.Offline);

    public Task<PostSiteOutcome> PublishAsync(string channelId, string messageId, CancellationToken ct = default)
        => Task.FromResult(PostSiteOutcome.Offline);
}
