using Modbot.Core.Email;

namespace Modbot.Core.Discord;

/// <summary>
/// Sends a direct message to one Discord user, as the deployment's bot.
/// </summary>
/// <remarks>
/// Used for reset links a person asked for when email is not available (accounts and access
/// design §4.2). The Discord bot of foundation §9 will sit behind the same interface; today the
/// implementation is two REST calls with the stored token, because there was no bot to build on.
/// </remarks>
public interface IDiscordMessenger
{
    /// <summary>Whether a bot token is stored. Read on every call; settings change.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    /// <param name="discordUserId">An opaque snowflake; never parsed.</param>
    Task<SendOutcome> SendDirectMessageAsync(string discordUserId, string text, CancellationToken ct = default);
}
