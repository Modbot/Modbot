namespace Modbot.Core.Discord;

/// <summary>Whether a join gate action happened, and if not, why.</summary>
/// <param name="Done">It happened, or there was nothing to do.</param>
/// <param name="Error">What went wrong, in a sentence, when it did not.</param>
/// <param name="BotOffline">The bot is not connected, so nothing was asked of Discord.</param>
/// <param name="NotWaiting">The person is not at the gate.</param>
public sealed record JoinGateOutcome(bool Done, string? Error, bool BotOffline = false, bool NotWaiting = false)
{
    public static JoinGateOutcome Ok { get; } = new(true, null);

    public static JoinGateOutcome Offline { get; } = new(false, "The Discord bot is not connected.", BotOffline: true);

    public static JoinGateOutcome NotAtTheGate { get; } = new(false, "That person is not at the join gate.", NotWaiting: true);

    public static JoinGateOutcome Failed(string error) => new(false, error);
}

/// <summary>
/// What a moderator does at the join gate from Modbot (join gate design §7 and §8): let one person
/// in, remove one, hold new joiners, lift the hold, pause the server's invites.
/// </summary>
/// <remarks>
/// In Core so the API can ask without depending on the bot, as it does through
/// <see cref="IDiscordMemberActions"/>. The same code answers the buttons on a Discord alert, so a
/// press in Discord and a press in Modbot do the same thing and write the same fact.
/// </remarks>
public interface IJoinGateActions
{
    /// <param name="by">The Modbot account that asked.</param>
    Task<JoinGateOutcome> LetInAsync(string discordUserId, Guid by, CancellationToken ct = default);

    /// <param name="byName">The account's username, for Discord's audit log.</param>
    Task<JoinGateOutcome> RemoveAsync(string discordUserId, Guid by, string byName, CancellationToken ct = default);

    Task<JoinGateOutcome> HoldAsync(Guid by, CancellationToken ct = default);

    Task<JoinGateOutcome> LiftHoldAsync(Guid by, CancellationToken ct = default);

    Task<JoinGateOutcome> PauseInvitesAsync(Guid by, CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="work"/> while no pass of the gate is running and none can start: a
    /// settings save, so a pass never acts on settings that changed under it (join gate design §6).
    /// </summary>
    Task<T> RunAloneAsync<T>(Func<Task<T>> work, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. Every action answers that the bot is not connected.</summary>
public sealed class NoJoinGateActions : IJoinGateActions
{
    public Task<JoinGateOutcome> LetInAsync(string discordUserId, Guid by, CancellationToken ct = default)
        => Task.FromResult(JoinGateOutcome.Offline);

    public Task<JoinGateOutcome> RemoveAsync(string discordUserId, Guid by, string byName, CancellationToken ct = default)
        => Task.FromResult(JoinGateOutcome.Offline);

    public Task<JoinGateOutcome> HoldAsync(Guid by, CancellationToken ct = default)
        => Task.FromResult(JoinGateOutcome.Offline);

    public Task<JoinGateOutcome> LiftHoldAsync(Guid by, CancellationToken ct = default)
        => Task.FromResult(JoinGateOutcome.Offline);

    public Task<JoinGateOutcome> PauseInvitesAsync(Guid by, CancellationToken ct = default)
        => Task.FromResult(JoinGateOutcome.Offline);

    /// <summary>No bot, so no pass to wait for.</summary>
    public Task<T> RunAloneAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return work();
    }
}
