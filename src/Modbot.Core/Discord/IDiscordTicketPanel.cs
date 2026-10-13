namespace Modbot.Core.Discord;

/// <summary>One button on a ticket panel: the ticket type it opens and the words on it.</summary>
public sealed record DiscordTicketPanelButton(Guid TypeId, string Label);

/// <summary>Whether the ticket panel was posted, and if not, why.</summary>
/// <param name="Done">The panel is in the channel.</param>
/// <param name="Error">What went wrong, in a sentence, when it did not.</param>
/// <param name="BotOffline">The bot is not connected, so nothing was asked of Discord.</param>
public sealed record DiscordTicketPanelOutcome(bool Done, string? Error, bool BotOffline = false)
{
    public static DiscordTicketPanelOutcome Ok { get; } = new(true, null);

    public static DiscordTicketPanelOutcome Offline { get; } = new(false, "The Discord bot is not connected.", BotOffline: true);

    public static DiscordTicketPanelOutcome Failed(string error) => new(false, error);
}

/// <summary>
/// Posts the message members press to open a ticket: some words and one button for each ticket
/// type. In Core so the API can ask without depending on the bot, as it does through
/// <see cref="IJoinGateActions"/>.
/// </summary>
public interface IDiscordTicketPanel
{
    Task<DiscordTicketPanelOutcome> PostAsync(
        string channelId, string text, IReadOnlyList<DiscordTicketPanelButton> buttons, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. The panel cannot be posted.</summary>
public sealed class NoDiscordTicketPanel : IDiscordTicketPanel
{
    public Task<DiscordTicketPanelOutcome> PostAsync(
        string channelId, string text, IReadOnlyList<DiscordTicketPanelButton> buttons, CancellationToken ct = default)
        => Task.FromResult(DiscordTicketPanelOutcome.Offline);
}
