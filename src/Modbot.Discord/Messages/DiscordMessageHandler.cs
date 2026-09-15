using Modbot.Core.Logging;
using Modbot.Core.Moderation;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Messages;

/// <summary>
/// What happens to a message the bot sees: it is stored, and then AI moderation checks it
/// (<see cref="IModerationChecker"/>).
/// </summary>
/// <remarks>
/// <para>
/// Only messages that were new to the store, or edits that changed the text, are checked, so a
/// message seen live and again on a page of history is checked once. History read back on first
/// setup is not checked at all: acting on a message from last year is not moderation. Messages
/// found by catching up after a disconnect are, since they are hours old at most.
/// </para>
/// <para>
/// Bots' and webhooks' messages are stored but not checked, so a rule can never delete Modbot's own
/// posts. A checker that throws is logged; the message stays stored either way.
/// </para>
/// </remarks>
public sealed class DiscordMessageHandler
{
    private readonly DiscordMessageStore _store;
    private readonly IModerationChecker _checker;
    private readonly ILogger _log;

    public DiscordMessageHandler(DiscordMessageStore store, IModerationChecker checker, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(checker);

        _store = store;
        _checker = checker;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <summary>Stores messages seen live or found by catching up, and checks the new ones.</summary>
    /// <returns>How many were new.</returns>
    public async Task<int> ReceivedAsync(IReadOnlyList<DiscordMessageSnapshot> messages, CancellationToken ct)
    {
        var fresh = await _store.StoreAsync(messages, ct).ConfigureAwait(false);

        foreach (var message in fresh)
            await CheckAsync(message, edited: false, ct).ConfigureAwait(false);

        return fresh.Count;
    }

    public async Task EditedAsync(DiscordMessageSnapshot message, CancellationToken ct)
    {
        var outcome = await _store.EditAsync(message, ct).ConfigureAwait(false);

        if (outcome.Stored || outcome.PreviousText is not null)
            await CheckAsync(message, edited: outcome.PreviousText is not null, ct).ConfigureAwait(false);
    }

    public Task<int> DeletedAsync(IReadOnlyList<string> messageIds, CancellationToken ct)
        => _store.DeleteAsync(messageIds, ct);

    private async Task CheckAsync(DiscordMessageSnapshot message, bool edited, CancellationToken ct)
    {
        if (message.AuthorIsBot || string.IsNullOrWhiteSpace(message.Text))
            return;

        try
        {
            // The channel is the thread when there is one: that is where a delete has to be sent.
            await _checker.CheckDiscordMessageAsync(
                new DiscordMessageToCheck(
                    message.GuildId,
                    message.ReadFrom,
                    message.Id,
                    message.AuthorId,
                    message.AuthorName,
                    message.Text,
                    edited),
                ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warning(e, "AI moderation could not check Discord message {MessageId}", message.Id);
        }
    }
}
