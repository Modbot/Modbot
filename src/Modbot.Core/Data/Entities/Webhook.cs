namespace Modbot.Core.Data.Entities;

/// <summary>
/// An address Modbot sends events to, and where its delivery has got to (API keys design §6).
/// </summary>
/// <remarks>
/// <para>
/// A webhook sends what <em>the account that set it up</em> may see, now. That is why only that
/// account or an administrator may repoint it: changing the address of somebody else's webhook
/// would hand you their view of the log.
/// </para>
/// <para>
/// The secret is encrypted rather than hashed because it has to be read back to sign each
/// delivery. It is never returned by the API after the response that made it.
/// </para>
/// </remarks>
public class Webhook
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>Exact types, <c>prefix.*</c>, or <c>*</c>.</summary>
    public List<string> EventTypes { get; set; } = [];

    /// <summary>Empty means every subject. Opaque ids, never checked for shape.</summary>
    public List<string> SubjectIds { get; set; } = [];

    public bool Enabled { get; set; }

    /// <summary>Encrypted with <c>ISecretProtector</c>.</summary>
    public string SecretEncrypted { get; set; } = string.Empty;

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // ── Delivery state ─────────────────────────────────────────────────────────────────────

    /// <summary>The fact id delivery has reached: everything up to here was sent or given up on.</summary>
    public long DeliveredThrough { get; set; }

    /// <summary>Attempts at the current event that have failed. Zero once it goes out.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>When the next attempt may be made. Null means now.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>When the current run of answers that were not 2xx began. Null while it is working.</summary>
    public DateTimeOffset? FailingSince { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Set when Modbot turned it off by itself.</summary>
    public DateTimeOffset? DisabledAt { get; set; }

    public string? DisabledReason { get; set; }
}

/// <summary>One attempt to deliver one event to a webhook. The last fifty per webhook are kept.</summary>
public class WebhookDelivery
{
    public long Id { get; set; }

    public Guid WebhookId { get; set; }

    /// <summary>The envelope's id: a fact id, or <c>test-…</c> for "Send test".</summary>
    public string EventId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public DateTimeOffset AttemptedAt { get; set; }

    /// <summary>1 for the first try at this event.</summary>
    public int Attempt { get; set; }

    /// <summary>Null when no answer came.</summary>
    public int? StatusCode { get; set; }

    public int DurationMs { get; set; }

    public string? Error { get; set; }

    public bool Test { get; set; }

    /// <summary><c>delivered</c>, <c>retrying</c> or <c>skipped</c>.</summary>
    public string Outcome { get; set; } = string.Empty;
}
