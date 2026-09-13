namespace Modbot.Core.Data.Entities;

/// <summary>
/// Modbot's entire configuration, as a single row.
/// </summary>
/// <remarks>
/// Foundation spec section 2.6: the environment carries only what is needed <em>before</em> the
/// database is reachable. Everything else is entered through the onboarding wizard and lives here,
/// so deploying Modbot is "click the template, open the URL, follow the wizard" rather than a page
/// of environment variables. Secret-bearing columns are encrypted by
/// <see cref="Security.ISecretProtector"/>.
/// </remarks>
public class Settings
{
    /// <summary>Always 1. Enforced by a database check constraint, not by convention.</summary>
    public int Id { get; set; } = 1;

    public bool OnboardingComplete { get; set; }

    // --- VRChat account (spec 2.3) ---
    public string? VRChatUsername { get; set; }
    public string? VRChatPasswordEncrypted { get; set; }
    public string? VRChatTotpSecretEncrypted { get; set; }
    public string? VRChatAuthCookieEncrypted { get; set; }

    /// <summary>
    /// The display name VRChat returned the last time these credentials were accepted.
    /// </summary>
    /// <remarks>
    /// Stored so the wizard and the settings page can show <em>which</em> account Modbot acts as
    /// without a live call — an operator who mistyped a shared account's email finds out by
    /// reading a name, not by watching a sync produce the wrong group's data.
    /// </remarks>
    public string? VRChatDisplayName { get; set; }

    /// <summary>When VRChat last accepted these credentials (spec 7.1, step 2).</summary>
    public DateTimeOffset? VRChatVerifiedAt { get; set; }

    // --- Managed group (spec 2.4) ---
    public string? ManagedGroupId { get; set; }
    public string? ManagedGroupName { get; set; }

    // --- Optional egress proxy (spec 2.3.1) ---
    public string? ProxyUrl { get; set; }
    public string? ProxyUsername { get; set; }
    public string? ProxyPasswordEncrypted { get; set; }

    /// <summary>When the egress check last passed (spec 7.1.1).</summary>
    /// <remarks>
    /// Recorded so the wizard knows step 3 is genuinely done rather than merely skipped past, and
    /// so the settings page can say when the answer it is showing was last true. A host that was
    /// reachable in March and is WAF-blocked today is the exact case §7.1.1's re-runnable check
    /// exists for.
    /// </remarks>
    public DateTimeOffset? ConnectionCheckedAt { get; set; }

    // --- Optional integrations (spec 7.1 step 5, 9) ---

    /// <summary>
    /// The Discord bot token. Encrypted (spec 8.3); absent means the bot does not start and
    /// nothing else about Modbot is affected (spec 9).
    /// </summary>
    public string? DiscordBotTokenEncrypted { get; set; }

    /// <summary>The guild the bot serves. An opaque snowflake; never parsed or validated.</summary>
    public string? DiscordGuildId { get; set; }

    // --- Operator-supplied SMTP (spec 7.4) ---
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpPasswordEncrypted { get; set; }
    public string? SmtpFromAddress { get; set; }

    /// <summary>Defaults on: an SMTP relay that needs it turned off is the unusual one.</summary>
    public bool SmtpUseTls { get; set; } = true;

    // --- Retention, tiered per fact class (spec 5.5) ---
    public int ModerationFactRetentionDays { get; set; }         // 0 = keep forever
    public int PresenceFactRetentionDays { get; set; }            // 0 = keep forever

    /// <summary>
    /// Deduplication half-window for client-reported facts (spec 5.7.1). Bounded above by the
    /// 15-second genuine leave-and-rejoin, below by residual clock skew after IModbotClock sync.
    /// </summary>
    public int DedupWindowSeconds { get; set; } = 5;

    /// <summary>Spec 5.8.1 -- optional by default; groups may opt into requiring it.</summary>
    public bool RequireModerationClassification { get; set; }
}
