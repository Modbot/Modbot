namespace Modbot.Core.Data.Entities;

/// <summary>
/// A credential for a program: sent as <c>Authorization: Bearer mbk_…</c> and accepted by the same
/// <c>/api</c> endpoints the web app uses.
/// </summary>
/// <remarks>
/// <para>
/// API keys design §3. Only the SHA-256 of the key is stored; the key is shown once, when it is
/// made. A plain hash rather than a password hash because the key is 256 random bits -- there is
/// no dictionary to slow down, and a slow hash would be paid on every API request.
/// </para>
/// <para>
/// A key may do what its <see cref="Permissions"/> allow, narrowed on every request to what the
/// account that made it holds at that moment (§3.2). Keys are never deleted, only revoked: facts
/// name them.
/// </para>
/// </remarks>
public class ApiKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>What the person called it, so a list of keys says which is which.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The first twelve characters of the key, in the clear. Enough to recognise a key in a list
    /// or a config file; nothing like enough to use it.
    /// </summary>
    public string Start { get; set; } = string.Empty;

    /// <summary>SHA-256 of the whole key, lowercase hex. The key itself never touches the database.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>What the key may do, before the cap by its account's current permissions.</summary>
    public ModbotPermissions Permissions { get; set; }

    /// <summary>The account that made it. Requests made with the key are attributed to this account.</summary>
    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Null for a key that does not expire.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Written at most once a minute (§3.5).</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? RevokedByUserId { get; set; }

    /// <summary>Whether the key authenticates at <paramref name="now"/>, before its account is checked.</summary>
    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || now < ExpiresAt);
}
