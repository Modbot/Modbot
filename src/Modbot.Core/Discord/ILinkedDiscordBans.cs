namespace Modbot.Core.Discord;

/// <summary>What happened to the Discord half of a ban or unban made through Modbot.</summary>
public enum LinkedDiscordStatus
{
    /// <summary>
    /// Nothing to do: Discord is not set up, or the person has no linked Discord account. Not a
    /// failure and never shown as one.
    /// </summary>
    Skipped,

    /// <summary>Discord did it.</summary>
    Done,

    /// <summary>Discord already had it that way: already banned, or not banned to begin with.</summary>
    Unchanged,

    /// <summary>Discord did not do it. <see cref="LinkedDiscordOutcome.Error"/> says why.</summary>
    Failed,
}

/// <summary>How the Discord half of a ban or unban went.</summary>
public sealed record LinkedDiscordOutcome(LinkedDiscordStatus Status, string? Error = null)
{
    public static LinkedDiscordOutcome Skipped { get; } = new(LinkedDiscordStatus.Skipped);

    public static LinkedDiscordOutcome Failed(string error) => new(LinkedDiscordStatus.Failed, error);
}

/// <summary>
/// Bans and unbans a person's linked Discord account, for a ban or unban made through Modbot.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Modbot is the one place a ban lands everywhere.</strong> A ban somebody presses in
/// VRChat and a ban somebody types in Discord stay separate unless an operator switches copying
/// on. A ban made <em>through Modbot</em> — a moderator's, or AutoMod's — is different: Modbot is
/// the tool that knows both accounts belong to one person, so it acts on both, without needing
/// that switch.
/// </para>
/// <para>
/// In Core so the moderation service in the API and the AutoMod engine can ask without depending
/// on the bot. The bot's implementation goes through its live session; a process without it
/// answers <see cref="LinkedDiscordStatus.Skipped"/>.
/// </para>
/// <para>
/// The Discord half never takes the VRChat half down with it. The caller has already done the
/// VRChat action by the time it asks, and what this answers is added to what the person is told.
/// </para>
/// </remarks>
public interface ILinkedDiscordBans
{
    /// <summary>Bans the Discord account linked to a VRChat person.</summary>
    /// <param name="vrchatUserId">The person, by their VRChat id.</param>
    /// <param name="by">Who banned them through Modbot: a moderator's name, or <c>AutoMod</c>.</param>
    /// <param name="why">The reasons picked, as words, or null. Written to Discord's audit log.</param>
    /// <param name="causedByFactId">The fact that recorded the VRChat ban, when there is one.</param>
    Task<LinkedDiscordOutcome> BanAsync(
        string vrchatUserId, string by, string? why, long? causedByFactId, CancellationToken ct = default);

    /// <summary>Lifts the ban on the Discord account linked to a VRChat person.</summary>
    Task<LinkedDiscordOutcome> UnbanAsync(
        string vrchatUserId, string by, string? why, long? causedByFactId, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. There is no Discord half.</summary>
public sealed class NoLinkedDiscordBans : ILinkedDiscordBans
{
    public Task<LinkedDiscordOutcome> BanAsync(
        string vrchatUserId, string by, string? why, long? causedByFactId, CancellationToken ct = default)
        => Task.FromResult(LinkedDiscordOutcome.Skipped);

    public Task<LinkedDiscordOutcome> UnbanAsync(
        string vrchatUserId, string by, string? why, long? causedByFactId, CancellationToken ct = default)
        => Task.FromResult(LinkedDiscordOutcome.Skipped);
}
