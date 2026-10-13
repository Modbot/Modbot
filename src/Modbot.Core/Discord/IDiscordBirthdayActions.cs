namespace Modbot.Core.Discord;

/// <summary>What posting the day's birthdays, or giving or taking the birthday role, came to.</summary>
/// <param name="Done">Discord did it.</param>
/// <param name="Error">What went wrong, in a sentence, when it was not done.</param>
/// <param name="BotOffline">The bot has no session, so nothing was tried. The pass tries again shortly.</param>
/// <param name="NotInServer">Discord does not know the member in the server.</param>
/// <param name="TryAgain">Nothing was made and a later try may work (a rate limit); the pass tries again shortly.</param>
public sealed record DiscordBirthdayOutcome(
    bool Done,
    string? Error,
    bool BotOffline = false,
    bool NotInServer = false,
    bool TryAgain = false)
{
    public static DiscordBirthdayOutcome Ok { get; } = new(true, null);

    public static DiscordBirthdayOutcome Offline { get; } = new(false, "The Discord bot is not connected.", BotOffline: true);

    public static DiscordBirthdayOutcome Left { get; } = new(false, "That member is not in the server.", NotInServer: true);

    public static DiscordBirthdayOutcome Failed(string error, bool tryAgain = false) => new(false, error, TryAgain: tryAgain);
}

/// <summary>
/// What the daily birthday pass does in Discord: post the day's message and give or take the
/// birthday role.
/// </summary>
/// <remarks>
/// In Core so the API can ask without depending on the bot, as it does through
/// <see cref="IDiscordRoleMenuActions"/>. Each is one call to Discord. A role given here is recorded
/// by the bot as Modbot's own, so the permission watch does not report it.
/// </remarks>
public interface IDiscordBirthdayActions
{
    /// <summary>Posts <paramref name="text"/> in the channel. It names members with their mentions, and pings nobody.</summary>
    Task<DiscordBirthdayOutcome> PostAsync(string channelId, string text, CancellationToken ct = default);

    /// <summary>Gives a member the role, or takes it away.</summary>
    Task<DiscordBirthdayOutcome> ChangeRoleAsync(string guildId, string userId, string roleId, bool add, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. Every action answers that the bot is not connected.</summary>
public sealed class NoDiscordBirthdayActions : IDiscordBirthdayActions
{
    public Task<DiscordBirthdayOutcome> PostAsync(string channelId, string text, CancellationToken ct = default)
        => Task.FromResult(DiscordBirthdayOutcome.Offline);

    public Task<DiscordBirthdayOutcome> ChangeRoleAsync(string guildId, string userId, string roleId, bool add, CancellationToken ct = default)
        => Task.FromResult(DiscordBirthdayOutcome.Offline);
}
