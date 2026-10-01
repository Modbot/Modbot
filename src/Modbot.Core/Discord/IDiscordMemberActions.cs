namespace Modbot.Core.Discord;

/// <summary>Whether Discord did what a person asked, and if not, why.</summary>
/// <param name="Done">Discord did it, or there was nothing to do.</param>
/// <param name="Error">What went wrong, in a sentence, when it was not done.</param>
/// <param name="BotOffline">The bot is not connected, so nothing was asked of Discord at all.</param>
/// <param name="NothingToDo">Already so: not banned, not in the server.</param>
public sealed record DiscordMemberOutcome(bool Done, string? Error, bool BotOffline = false, bool NothingToDo = false)
{
    public static DiscordMemberOutcome Ok { get; } = new(true, null);

    public static DiscordMemberOutcome Already { get; } = new(true, null, NothingToDo: true);

    public static DiscordMemberOutcome Offline { get; } = new(false, "The Discord bot is not connected.", BotOffline: true);

    public static DiscordMemberOutcome Failed(string error) => new(false, error);
}

/// <summary>The two Discord accounts no one may act on through the API: the bot itself and the server's owner.</summary>
/// <param name="BotUserId">The bot's own account, or null when it is not signed in.</param>
/// <param name="OwnerId">The owner of the server, or null when it is not known.</param>
public sealed record DiscordOffLimits(string? BotUserId, string? OwnerId)
{
    public static DiscordOffLimits None { get; } = new(null, null);
}

/// <summary>
/// Ban, unban, remove and time out one member of the Discord server, because a person asked
/// through the API (API conventions design §8).
/// </summary>
/// <remarks>
/// In Core so the API can ask without depending on the bot, as AutoMod does through
/// <see cref="IDiscordModerationActions"/>. The bot's implementation goes over REST through its
/// live session, one request each, never retried; with the bot offline every one answers
/// <see cref="DiscordMemberOutcome.Offline"/> at once.
/// </remarks>
public interface IDiscordMemberActions
{
    /// <summary>
    /// The bot's own account and the server's owner, for the API to turn down before it asks Discord
    /// anything. Nothing is known with the bot offline; the action then answers that it is offline.
    /// </summary>
    Task<DiscordOffLimits> OffLimitsAsync(string guildId, CancellationToken ct = default);

    /// <param name="deleteMessageDays">How many days of their messages Discord deletes too, 0 to 7.</param>
    Task<DiscordMemberOutcome> BanAsync(string guildId, string userId, string reason, int deleteMessageDays, CancellationToken ct = default);

    Task<DiscordMemberOutcome> UnbanAsync(string guildId, string userId, string reason, CancellationToken ct = default);

    Task<DiscordMemberOutcome> KickAsync(string guildId, string userId, string reason, CancellationToken ct = default);

    Task<DiscordMemberOutcome> TimeOutAsync(string guildId, string userId, TimeSpan duration, string reason, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. Every action answers that the bot is not connected.</summary>
public sealed class NoDiscordMemberActions : IDiscordMemberActions
{
    public Task<DiscordOffLimits> OffLimitsAsync(string guildId, CancellationToken ct = default)
        => Task.FromResult(DiscordOffLimits.None);

    public Task<DiscordMemberOutcome> BanAsync(string guildId, string userId, string reason, int deleteMessageDays, CancellationToken ct = default)
        => Task.FromResult(DiscordMemberOutcome.Offline);

    public Task<DiscordMemberOutcome> UnbanAsync(string guildId, string userId, string reason, CancellationToken ct = default)
        => Task.FromResult(DiscordMemberOutcome.Offline);

    public Task<DiscordMemberOutcome> KickAsync(string guildId, string userId, string reason, CancellationToken ct = default)
        => Task.FromResult(DiscordMemberOutcome.Offline);

    public Task<DiscordMemberOutcome> TimeOutAsync(string guildId, string userId, TimeSpan duration, string reason, CancellationToken ct = default)
        => Task.FromResult(DiscordMemberOutcome.Offline);
}
