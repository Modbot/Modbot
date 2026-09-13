namespace Modbot.Core.Email;

/// <summary>One plain-text message to one address.</summary>
public sealed record EmailMessage(string To, string Subject, string Body);

/// <summary>What happened when something was sent.</summary>
/// <param name="Sent">True when the message left Modbot. Not proof it arrived.</param>
/// <param name="Error">One sentence for the settings page when it did not.</param>
public sealed record SendOutcome(bool Sent, string? Error)
{
    public static SendOutcome Ok { get; } = new(true, null);

    public static SendOutcome Failed(string error) => new(false, error);

    /// <summary>The deployment has no way to send this kind of message.</summary>
    public static SendOutcome NotConfigured(string what) => new(false, $"{what} is not set up on this deployment.");
}

/// <summary>
/// Sends email through the operator's own SMTP relay (foundation §7.4).
/// </summary>
/// <remarks>
/// Used for reset links a person asked for (accounts and access design §4.2) and for the test
/// message on the settings page. Nothing else sends email yet; the notification pipeline of §4.5
/// will sit behind the same interface when it arrives.
/// </remarks>
public interface IEmailSender
{
    /// <summary>Whether the settings row holds enough to try. Read on every call; settings change.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    Task<SendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default);
}
