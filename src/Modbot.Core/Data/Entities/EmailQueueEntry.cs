namespace Modbot.Core.Data.Entities;

/// <summary>
/// One email, from the moment it was asked for until a day after it went out or a few days after
/// it was given up on (accounts and access design §4.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every email gets a row, sent straight away or not.</strong> The rows in
/// <see cref="EmailStates.Sending"/> and <see cref="EmailStates.Sent"/> with a
/// <see cref="SentAt"/> in the last 24 hours are the count the daily limit is checked against,
/// so there is no second counter to drift from the first.
/// </para>
/// <para>
/// The body is stored only for a queued message, encrypted with <c>ISecretProtector</c>, and
/// cleared as soon as the message is sent, fails for good or expires. A reset link's token is in
/// the body, and the one-time link table stores only hashes so that reading the database yields
/// no working link; a plain body here would undo that.
/// </para>
/// </remarks>
public class EmailQueueEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary><see cref="EmailKinds.Account"/> or <see cref="EmailKinds.Other"/>.</summary>
    public string Kind { get; set; } = EmailKinds.Other;

    public string ToAddress { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    /// <summary>Encrypted. Null unless the message is waiting to be sent.</summary>
    public string? BodyEncrypted { get; set; }

    /// <summary>One of <see cref="EmailStates"/>.</summary>
    public string State { get; set; } = EmailStates.Queued;

    /// <summary>When the message was asked for. Queue order within a kind.</summary>
    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>When the relay was last handed it. Null while it is waiting.</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>When what it carries stops working. A queued message still waiting then is not sent.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Tries from the queue that the relay refused.</summary>
    public int Attempts { get; set; }

    /// <summary>After a refusal, the earliest the queue tries again. Null means as soon as there is room.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>The relay's last refusal, one sentence. Never the body.</summary>
    public string? LastError { get; set; }

    /// <summary>When it was sent, given up on, or found expired.</summary>
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>The values of <see cref="EmailQueueEntry.Kind"/>.</summary>
public static class EmailKinds
{
    public const string Account = "account";
    public const string Other = "other";
}

/// <summary>The values of <see cref="EmailQueueEntry.State"/>.</summary>
public static class EmailStates
{
    /// <summary>Waiting for room under the limit, or for its next try.</summary>
    public const string Queued = "queued";

    /// <summary>Handed to the relay and not answered yet. Counts against the limit.</summary>
    public const string Sending = "sending";

    public const string Sent = "sent";

    /// <summary>Given up on after the relay refused it too many times.</summary>
    public const string Failed = "failed";

    /// <summary>Still waiting when what it carried stopped working, so never sent.</summary>
    public const string Expired = "expired";
}
