using Modbot.Core.Email;

namespace Modbot.Core.Discord;

/// <summary>
/// The messenger a host gets when it has not registered the Discord project: never configured,
/// never sends. Exists so the forgot-password flow can ask "can I reach this person on Discord?"
/// without a null check, and so a test host need not carry the Discord project.
/// </summary>
public sealed class NoDiscordMessenger : IDiscordMessenger
{
    public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<SendOutcome> SendDirectMessageAsync(string discordUserId, string text, CancellationToken ct = default)
        => Task.FromResult(SendOutcome.NotConfigured("The Discord bot"));
}
