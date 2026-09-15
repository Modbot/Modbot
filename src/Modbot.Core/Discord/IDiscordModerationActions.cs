namespace Modbot.Core.Discord;

/// <summary>Whether Discord did what was asked, and if not, why.</summary>
public sealed record DiscordActionOutcome(bool Done, string? Error)
{
    public static DiscordActionOutcome Ok { get; } = new(true, null);

    public static DiscordActionOutcome Failed(string error) => new(false, error);
}

/// <summary>
/// The two things an AI moderation rule may do on Discord (M8 §2): delete a message and time a
/// member out.
/// </summary>
/// <remarks>
/// In Core so the engine in <c>Modbot.AI</c> can ask without depending on the bot. The bot's
/// implementation goes through its live gateway; with the bot offline both answer a failure at once
/// rather than wait for it.
/// </remarks>
public interface IDiscordModerationActions
{
    Task<DiscordActionOutcome> DeleteMessageAsync(string channelId, string messageId, string reason, CancellationToken ct = default);

    Task<DiscordActionOutcome> TimeOutAsync(string guildId, string userId, TimeSpan duration, string reason, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. Every action fails and says so.</summary>
public sealed class NoDiscordModerationActions : IDiscordModerationActions
{
    private const string NoBot = "The Discord bot is not running.";

    public Task<DiscordActionOutcome> DeleteMessageAsync(string channelId, string messageId, string reason, CancellationToken ct = default)
        => Task.FromResult(DiscordActionOutcome.Failed(NoBot));

    public Task<DiscordActionOutcome> TimeOutAsync(string guildId, string userId, TimeSpan duration, string reason, CancellationToken ct = default)
        => Task.FromResult(DiscordActionOutcome.Failed(NoBot));
}
