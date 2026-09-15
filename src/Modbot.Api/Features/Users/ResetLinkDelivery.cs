using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Email;

namespace Modbot.Api.Features.Users;

/// <summary>How a reset link the person asked for was sent, or why it was not.</summary>
/// <param name="Via">"email" or "discord" when something was attempted; null when nothing could be.</param>
public sealed record DeliveryResult(string? Via, SendOutcome? Outcome)
{
    public bool Sent => Outcome?.Sent == true;

    /// <summary>Waiting in the email queue under the daily limit (design §4.4).</summary>
    public bool Queued => Outcome?.Queued == true;
}

/// <summary>
/// Gets a reset link to the person who asked for it (accounts and access design §4.2).
/// </summary>
/// <remarks>
/// Email first, if SMTP is set up and the account has an address; otherwise a Discord direct
/// message, if a bot token is stored and the account has a Discord user id. The message names the
/// account so a person who did not ask can tell what happened, and it says nothing has changed.
/// An email is account email under the daily limit and carries the link's expiry, so the queue
/// does not send it once the link has stopped working (design §4.4).
/// </remarks>
public sealed class ResetLinkDelivery
{
    public const string EmailWay = "email";
    public const string DiscordWay = "discord";

    private readonly IEmailSender _email;
    private readonly IDiscordMessenger _discord;

    public ResetLinkDelivery(IEmailSender email, IDiscordMessenger discord)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(discord);

        _email = email;
        _discord = discord;
    }

    /// <summary>The ways this deployment can send, in order of preference. Empty means none.</summary>
    public async Task<IReadOnlyList<string>> WaysAsync(CancellationToken ct)
    {
        var ways = new List<string>();

        if (await _email.IsConfiguredAsync(ct)) ways.Add(EmailWay);
        if (await _discord.IsConfiguredAsync(ct)) ways.Add(DiscordWay);

        return ways;
    }

    /// <param name="expiresAt">When the link stops working. A queued email still waiting then is not sent.</param>
    public async Task<DeliveryResult> SendAsync(ModbotUser user, string url, DateTimeOffset expiresAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var text = Text(user.Username, url);

        if (user.Email is { Length: > 0 } email && await _email.IsConfiguredAsync(ct))
        {
            var outcome = await _email.SendAsync(
                new EmailMessage(email, "Reset your Modbot password", text, EmailKind.Account, expiresAt),
                ct);
            return new DeliveryResult(EmailWay, outcome);
        }

        if (user.DiscordUserId is { Length: > 0 } discord && await _discord.IsConfiguredAsync(ct))
        {
            var outcome = await _discord.SendDirectMessageAsync(discord, text, ct);
            return new DeliveryResult(DiscordWay, outcome);
        }

        return new DeliveryResult(null, null);
    }

    private static string Text(string username, string url) =>
        $"Somebody asked to reset the password for the Modbot account \"{username}\".\n\n"
        + $"If that was you, open this link within 24 hours:\n{url}\n\n"
        + "If it wasn't you, you can ignore this message. Nothing has changed.";
}
