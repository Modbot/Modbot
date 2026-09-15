namespace Modbot.Core.Email;

/// <summary>Which share of the daily email limit a message may use (accounts and access design §4.4).</summary>
public enum EmailKind
{
    /// <summary>
    /// Everything that is not about getting into an account: the test message, alerts,
    /// notifications. May use the limit less <see cref="EmailLimit.KeptForAccountEmails"/>.
    /// </summary>
    Other = 0,

    /// <summary>
    /// Reset links, invite links, email checks, sign-in and security notices. May use the whole
    /// limit, and goes ahead of other email in the queue.
    /// </summary>
    Account = 1,
}

/// <summary>One plain-text message to one address.</summary>
/// <param name="Kind">Which share of the daily limit it may use. Every message says.</param>
/// <param name="ExpiresAt">
/// When what the message carries stops working -- a reset link's expiry, say. A queued message
/// still waiting at that moment is not sent. Null for a message that does not go stale.
/// </param>
public sealed record EmailMessage(
    string To,
    string Subject,
    string Body,
    EmailKind Kind,
    DateTimeOffset? ExpiresAt = null);

/// <summary>What happened when something was sent.</summary>
/// <param name="Sent">True when the message left Modbot. Not proof it arrived.</param>
/// <param name="Error">One sentence for the settings page when it did not.</param>
public sealed record SendOutcome(bool Sent, string? Error)
{
    public static SendOutcome Ok { get; } = new(true, null);

    public static SendOutcome Failed(string error) => new(false, error);

    /// <summary>The deployment has no way to send this kind of message.</summary>
    public static SendOutcome NotConfigured(string what) => new(false, $"{what} is not set up on this deployment.");

    /// <summary>Held in the email queue because the daily limit is used up.</summary>
    /// <param name="sendsAt">When it should go out, or null when the limit leaves its kind no room at all.</param>
    public static SendOutcome Held(DateTimeOffset? sendsAt) => new(false, null) { Queued = true, SendsAt = sendsAt };

    /// <summary>Not sent yet, but stored and waiting its turn. Not a failure.</summary>
    public bool Queued { get; init; }

    /// <summary>When a queued message should go out. An estimate: account email asked for later goes first.</summary>
    public DateTimeOffset? SendsAt { get; init; }
}

/// <summary>
/// Sends email, within the deployment's daily email limit (accounts and access design §4.4).
/// </summary>
/// <remarks>
/// <para>
/// The one way anything in Modbot sends email. The implementation, <see cref="EmailSender"/>,
/// counts every message against the limit and holds what does not fit in the email queue; the
/// SMTP relay itself sits behind <see cref="IMailRelay"/>, which nothing else calls.
/// </para>
/// <para>
/// Used for reset links a person asked for (design §4.2) and the test message on the settings
/// page. The notification pipeline of foundation §4.5 will send through the same interface.
/// </para>
/// </remarks>
public interface IEmailSender
{
    /// <summary>Whether the settings row holds enough to try. Read on every call; settings change.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    /// <summary>Sends now if the limit has room for this kind, otherwise queues it.</summary>
    Task<SendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default);

    /// <summary>
    /// Whether a message of this kind asked for now would be queued. Says nothing about any
    /// account, so forgot-password can decide its answer from it before looking one up.
    /// </summary>
    Task<bool> WouldQueueAsync(EmailKind kind, CancellationToken ct = default);
}

/// <summary>
/// The SMTP relay, with no limit. Only <see cref="EmailSender"/> and the email queue call it.
/// </summary>
public interface IMailRelay
{
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    Task<SendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default);
}
