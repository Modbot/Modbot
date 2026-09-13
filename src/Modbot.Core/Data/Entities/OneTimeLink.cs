namespace Modbot.Core.Data.Entities;

/// <summary>What a <see cref="OneTimeLink"/> is for.</summary>
/// <remarks><strong>Persisted as smallint. Never renumber a member.</strong></remarks>
public enum OneTimeLinkKind : short
{
    /// <summary>Lets somebody who has no account create one, with the roles the link carries.</summary>
    Invite = 1,

    /// <summary>Lets the holder set a new password on one existing account.</summary>
    PasswordReset = 2,
}

/// <summary>
/// A link an administrator hands to somebody, good once and for a limited time.
/// </summary>
/// <remarks>
/// <para>
/// Accounts and access design §4.1. Modbot sends no email — many groups never configure SMTP —
/// so the administrator copies the link and passes it on. That is why it must be one-time and
/// short-lived: a link pasted into a Discord channel is a link everyone in that channel has.
/// </para>
/// <para>
/// Only the hash of the token is stored. The link itself is shown to the administrator once, at
/// creation, and cannot be read back — a database read must not yield working links.
/// </para>
/// </remarks>
public class OneTimeLink
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public OneTimeLinkKind Kind { get; set; }

    /// <summary>SHA-256 of the token, hex. The token never touches the database.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Who made the link. An invite from an account since disabled is no longer good.</summary>
    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when the link is used. A used link is never good again.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>
    /// For a reset link, the account it resets, set at creation. For an invite, the account it
    /// created, set when it is used.
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>The roles an invite hands out. Empty for reset links.</summary>
    public List<Guid> RoleIds { get; set; } = [];

    /// <summary>Whether the link can still be used at <paramref name="now"/>.</summary>
    public bool IsUsable(DateTimeOffset now) => UsedAt is null && now < ExpiresAt;
}
