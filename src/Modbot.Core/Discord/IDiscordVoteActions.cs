using Modbot.Core.Data.Entities;

namespace Modbot.Core.Discord;

/// <summary>What posting, rewriting or deleting a vote's message came to.</summary>
/// <param name="MessageId">The message that now carries the vote, when it was posted.</param>
/// <param name="BotOffline">The bot has no session, so nothing was tried.</param>
public sealed record VoteSiteOutcome(bool Done, string? Error, string? MessageId = null, bool BotOffline = false)
{
    public static VoteSiteOutcome Offline { get; } = new(false, "The Discord bot is not connected.", BotOffline: true);

    public static VoteSiteOutcome Ok(string? messageId = null) => new(true, null, messageId);

    public static VoteSiteOutcome Failed(string error) => new(false, error);
}

/// <summary>
/// What a vote does to its message on Discord: post it with its options, and rewrite it with the
/// result when staff close it.
/// </summary>
/// <remarks>
/// In Core so the API and the <c>/vote</c> command can ask without the API depending on the bot, as
/// <see cref="IDiscordRoleMenuActions"/> does for role menus.
/// </remarks>
public interface IDiscordVoteActions
{
    /// <summary>
    /// Posts the vote's message with its options as buttons, or as drop-down lists when there are
    /// more than a message holds buttons for. The vote's ids are already made, so nothing has to be
    /// saved first.
    /// </summary>
    Task<VoteSiteOutcome> PostAsync(Vote vote, CancellationToken ct = default);

    /// <summary>
    /// Rewrites the open vote's message with the count for each option, keeping the buttons. A
    /// message that was already rewritten with the result is left as it is.
    /// </summary>
    /// <param name="counts">The picks for each option, by option id.</param>
    Task<VoteSiteOutcome> RefreshAsync(Vote vote, IReadOnlyDictionary<Guid, int> counts, CancellationToken ct = default);

    /// <summary>
    /// Rewrites the vote's message with the result and takes the buttons away.
    /// </summary>
    /// <param name="counts">The picks for each option, by option id.</param>
    Task<VoteSiteOutcome> ShowResultAsync(Vote vote, IReadOnlyDictionary<Guid, int> counts, CancellationToken ct = default);

    /// <summary>Deletes a message. One that is already gone counts as done.</summary>
    Task<VoteSiteOutcome> DeleteAsync(string channelId, string messageId, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. Every action answers that the bot is not connected.</summary>
public sealed class NoDiscordVoteActions : IDiscordVoteActions
{
    public Task<VoteSiteOutcome> PostAsync(Vote vote, CancellationToken ct = default)
        => Task.FromResult(VoteSiteOutcome.Offline);

    public Task<VoteSiteOutcome> RefreshAsync(Vote vote, IReadOnlyDictionary<Guid, int> counts, CancellationToken ct = default)
        => Task.FromResult(VoteSiteOutcome.Offline);

    public Task<VoteSiteOutcome> ShowResultAsync(Vote vote, IReadOnlyDictionary<Guid, int> counts, CancellationToken ct = default)
        => Task.FromResult(VoteSiteOutcome.Offline);

    public Task<VoteSiteOutcome> DeleteAsync(string channelId, string messageId, CancellationToken ct = default)
        => Task.FromResult(VoteSiteOutcome.Offline);
}
