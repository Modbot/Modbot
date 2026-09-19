using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Email;

namespace Modbot.Core.Notifications;

/// <summary>
/// The ways a notification can reach a person (foundation §4.5).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="All"/> holds the channels this build actually has. <see cref="WebPush"/> is named
/// here and left out of that list on purpose: the name is fixed now so a preference saved by a
/// later build means the same thing, and the account screen shows no control for a channel that
/// cannot carry anything yet.
/// </para>
/// <para>
/// The client — the native toast and the SteamVR overlay — is deliberately not a channel. What a
/// moderator in a headset needs to see is a flagged join at the moment it happens, and that already
/// arrives on the live stream as a fact, with nothing between it and the overlay. Putting a quiet
/// time and a per-person setting in front of that path would make the one channel §4.5.2 calls
/// irreplaceable the slowest of the four.
/// </para>
/// </remarks>
public static class NotificationChannels
{
    public const string Email = "email";

    public const string Discord = "discord";

    /// <summary>Reserved. Needs keys, a service worker and a subscription per browser.</summary>
    public const string WebPush = "web-push";

    /// <summary>The channels this build has, in the order the account screen lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Email, Discord];

    public static bool IsKnown(string? channel) =>
        channel is not null && All.Contains(channel, StringComparer.Ordinal);

    public static string Label(string channel) => channel switch
    {
        Email => "Email",
        Discord => "Discord",
        WebPush => "Web push",
        _ => channel,
    };

    /// <summary>
    /// What a channel does for somebody who has never changed anything.
    /// </summary>
    /// <remarks>
    /// <strong>Deliberately quiet</strong> (§4.5.1). Email carries criticals and a daily summary of
    /// everything else, so an operator who never opens Modbot still hears about the things that stop
    /// it working and nothing else. Discord carries criticals and warnings as they happen, because a
    /// direct message is the cheaper interruption and it is where the moderators already are.
    /// </remarks>
    public static (string Level, bool DailySummary) Default(string channel) => channel switch
    {
        Email => (NotificationLevels.Critical, true),
        Discord => (NotificationLevels.Warning, false),
        WebPush => (NotificationLevels.Warning, false),
        _ => (NotificationLevels.Off, false),
    };
}

/// <summary>One way of reaching a person.</summary>
/// <remarks>
/// A channel knows nothing about severity, preferences or quiet times — the pipeline decides all of
/// that and hands the channel a person and some words. That is what makes adding one cheap.
/// </remarks>
public interface INotificationChannel
{
    /// <summary><see cref="NotificationChannels"/>.</summary>
    string Name { get; }

    /// <summary>
    /// Whether this channel could reach this person right now. Read every time: settings change,
    /// and a person who links a Discord account becomes reachable without anything being rebuilt.
    /// </summary>
    Task<bool> CanReachAsync(ModbotUser user, CancellationToken ct = default);

    /// <summary>Sends, or says why it could not. Never throws for a delivery problem.</summary>
    Task<SendOutcome> SendAsync(ModbotUser user, string title, string body, CancellationToken ct = default);
}

/// <summary>Notifications by email, through the ordinary sender.</summary>
/// <remarks>
/// <para>
/// <strong>Every message is <see cref="EmailKind.Other"/></strong>, so the daily email limit and
/// the twenty sends kept back for account email hold exactly as they did before (accounts and
/// access design §4.4). A deployment drowning in notifications still has room for a reset link.
/// </para>
/// <para>
/// "Sent" here means the email sender took it. It may have gone out or it may be in the queue; both
/// are the pipeline's job done, and the queue is what retries.
/// </para>
/// </remarks>
public sealed class EmailNotificationChannel : INotificationChannel
{
    private readonly IEmailSender _email;

    public EmailNotificationChannel(IEmailSender email)
    {
        ArgumentNullException.ThrowIfNull(email);
        _email = email;
    }

    public string Name => NotificationChannels.Email;

    public async Task<bool> CanReachAsync(ModbotUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user is { IsDisabled: false, Email: { Length: > 0 } }
               && await _email.IsConfiguredAsync(ct).ConfigureAwait(false);
    }

    public async Task<SendOutcome> SendAsync(
        ModbotUser user, string title, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.Email is not { Length: > 0 } address)
            return SendOutcome.NotConfigured("This account has no email address, so email");

        var outcome = await _email
            .SendAsync(new EmailMessage(address, title, body, EmailKind.Other), ct)
            .ConfigureAwait(false);

        // Queued is the daily limit working, not a failure: the queue sends it when there is room.
        return outcome.Queued ? SendOutcome.Ok : outcome;
    }
}

/// <summary>Notifications by Discord direct message, as the deployment's bot.</summary>
/// <remarks>
/// Reaches the Discord account on the person's Modbot account. A person with none is unreachable
/// here, which is one of the ways a critical notification ends up waiting at next sign-in.
/// </remarks>
public sealed class DiscordNotificationChannel : INotificationChannel
{
    private readonly IDiscordMessenger _messenger;

    public DiscordNotificationChannel(IDiscordMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(messenger);
        _messenger = messenger;
    }

    public string Name => NotificationChannels.Discord;

    public async Task<bool> CanReachAsync(ModbotUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user is { IsDisabled: false, DiscordUserId: { Length: > 0 } }
               && await _messenger.IsConfiguredAsync(ct).ConfigureAwait(false);
    }

    public async Task<SendOutcome> SendAsync(
        ModbotUser user, string title, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.DiscordUserId is not { Length: > 0 } discordUserId)
            return SendOutcome.NotConfigured("This account has no Discord account linked, so Discord");

        return await _messenger
            .SendDirectMessageAsync(discordUserId, $"**{title}**\n{body}", ct)
            .ConfigureAwait(false);
    }
}
