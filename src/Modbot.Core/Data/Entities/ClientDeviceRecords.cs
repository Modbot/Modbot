namespace Modbot.Core.Data.Entities;

/// <summary>
/// One moderator's install of the Windows client, as this deployment knows it.
/// </summary>
/// <remarks>
/// <para>
/// Every fact a client reports records which device reported it, which is what makes a
/// compromised or misbehaving install identifiable and its facts revocable as a set.
/// </para>
/// <para>
/// One row per device per server, individually revocable (M3 §4). A moderator leaving the team
/// must not require rotating every other moderator's token.
/// </para>
/// </remarks>
public class ClientDeviceRecord
{
    public Guid Id { get; set; }

    /// <summary>
    /// A hash of the token. The token itself is shown once, to the client that redeemed the code,
    /// and never reaches this deployment's disk — so a database dump does not hand anybody the
    /// ability to report facts as somebody else's moderator.
    /// </summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// What the moderator called this install, so an operator can tell a desktop from a laptop
    /// and revoke the right one. Untrusted display text like any other — it is typed by a person.
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    public string ClientVersion { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    /// <summary>
    /// Whose client this is, inherited from the pairing code rather than from the device name —
    /// so "which moderator does this belong to" has an answer that does not depend on what they
    /// typed.
    /// </summary>
    public Guid IssuedToUserId { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>
    /// When this device last spoke. An operator reading a list of installs needs to tell the ones
    /// still reporting from the ones left behind on a machine somebody stopped using.
    /// </summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>
    /// Set, never deleted.
    /// </summary>
    /// <remarks>
    /// The facts this device reported point at it, and deleting the row would leave a moderation
    /// record where nobody can say where it came from — which is the opposite of what §5.8's
    /// accountability story needs. A revoked device is refused at every endpoint from the moment
    /// this is set; it just remains explicable.
    /// </remarks>
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// A pairing code, between being shown in the web UI and being redeemed by a client.
/// </summary>
/// <remarks>
/// Short, single-use and short-lived, because it is the one credential in this flow a human reads
/// off a screen and retypes. The long-lived token is never displayed — a token somebody has to
/// read ends up pasted into a Discord message.
/// </remarks>
public class ClientPairingCodeRecord
{
    /// <summary>
    /// Hashed, like the token. A code is only useful before it is redeemed, but a database dump
    /// taken during that window should not contain a usable one.
    /// </summary>
    public string CodeHash { get; set; } = string.Empty;

    public Guid IssuedToUserId { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Set atomically on redemption.
    /// </summary>
    /// <remarks>
    /// Two clients racing on one code is precisely the case a single-use credential exists to
    /// prevent, so redemption is one conditional update rather than a read followed by a write.
    /// </remarks>
    public DateTimeOffset? RedeemedAt { get; set; }
}
