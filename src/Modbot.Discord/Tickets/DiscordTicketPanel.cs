using Modbot.Core.Discord;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Tickets;

/// <summary>Posts the ticket panel through the bot's live session.</summary>
public sealed class DiscordTicketPanel : IDiscordTicketPanel
{
    private readonly Func<IDiscordGateway?> _gateway;

    /// <param name="gateway">The session when it is ready to post, or null.</param>
    public DiscordTicketPanel(Func<IDiscordGateway?> gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
    }

    public async Task<DiscordTicketPanelOutcome> PostAsync(
        string channelId, string text, IReadOnlyList<DiscordTicketPanelButton> buttons, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(buttons);

        if (_gateway() is not { } gateway)
            return DiscordTicketPanelOutcome.Offline;

        // The permissions are read from the session's memory, so the answer names what is missing
        // before anything is posted.
        if (await gateway.MissingTicketPermissionsAsync(channelId, ct).ConfigureAwait(false) is { Count: > 0 } missing)
            return DiscordTicketPanelOutcome.Failed($"The bot needs {string.Join(", ", missing)} in that channel.");

        var actions = buttons
            .Select(b => new DiscordActionButton(b.Label, DiscordTickets.OpenButtonId(b.TypeId)))
            .ToList();

        var posted = await gateway.PostWithActionsAsync(channelId, text, [], null, actions, ct).ConfigureAwait(false);

        return posted.Sent
            ? DiscordTicketPanelOutcome.Ok
            : DiscordTicketPanelOutcome.Failed(posted.Error ?? "Discord refused.");
    }
}
