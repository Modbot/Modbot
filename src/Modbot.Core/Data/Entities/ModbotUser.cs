namespace Modbot.Core.Data.Entities;

/// <summary>
/// A staff account: someone who signs in to Modbot.
/// </summary>
/// <remarks>
/// Foundation spec section 6.3 and 7.2. Not an Identity user -- Modbot registers
/// <c>PasswordHasher&lt;ModbotUser&gt;</c> standalone and owns everything else itself, because
/// ASP.NET Core Identity brings a user store, role store, token providers, sign-in manager and
/// two-factor stack for a table that holds a dozen rows.
/// </remarks>
public class ModbotUser
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>As the operator typed it. Shown in the UI and in audit entries.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Upper-invariant form of <see cref="Username"/>, uniquely indexed. Lookups go through this
    /// so "Alice" and "alice" are the same account regardless of the database's collation.
    /// </summary>
    public string UsernameNormalized { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// The roles this account holds. What it may do is their union — there is no per-user
    /// permission column any more (accounts and access design §3.3).
    /// </summary>
    public ICollection<ModbotUserRole> Roles { get; set; } = [];

    /// <summary>
    /// Optional Discord user id (spec 6.3). An opaque snowflake; never parsed. Typed in rather
    /// than proven, so it is only ever used to reach the person (a reset link by direct message),
    /// never to sign them in.
    /// </summary>
    public string? DiscordUserId { get; set; }

    /// <summary>Optional. Only ever used to send this person a reset link they asked for.</summary>
    public string? Email { get; set; }

    // ── The linked VRChat account (accounts and access design §4.3) ─────────────────────────
    //
    // Required before the account can do anything but finish the link. Two columns rather than a
    // row in a VRChat user table: that table is another workstream's, and these are the join key
    // for it, not a copy of it.

    /// <summary>The VRChat account this person proved they hold. Opaque; never validated (spec 3.1.1).</summary>
    public string? VRChatUserId { get; set; }

    /// <summary>The display name at link time. Names change; this one is what was seen then.</summary>
    public string? VRChatDisplayName { get; set; }

    public DateTimeOffset? VRChatLinkedAt { get; set; }

    public bool IsVRChatLinked => VRChatUserId is { Length: > 0 };

    /// <summary>The code this person has been asked to put in their bio, while a link is pending.</summary>
    public string? VRChatLinkCode { get; set; }

    public DateTimeOffset? VRChatLinkCodeExpiresAt { get; set; }

    /// <summary>The VRChat user id they pasted, which the code will be looked for on.</summary>
    public string? VRChatLinkPendingUserId { get; set; }

    /// <summary>How many times Check has been pressed for the current code. Capped.</summary>
    public int VRChatLinkChecks { get; set; }

    public DateTimeOffset? VRChatLinkLastCheckAt { get; set; }

    /// <summary>
    /// Disabled rather than deleted: facts reference the actor, and deleting the account would
    /// orphan the attribution that spec 5.9.1 exists to preserve.
    /// </summary>
    public bool IsDisabled { get; set; }

    /// <summary>
    /// Sessions started before this instant are dead. Moved forward by disabling the account,
    /// a password reset, an own password change, and "sign out everywhere" (design §5).
    /// </summary>
    /// <remarks>
    /// Compared against the <c>modbot:signed_in_at</c> claim, which is stamped from the same
    /// <c>IModbotClock</c>. Null means no session has ever been cut off.
    /// </remarks>
    public DateTimeOffset? SessionsValidAfter { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>The union of this account's roles. Requires <see cref="Roles"/> to be loaded.</summary>
    public ModbotPermissions EffectivePermissions
        => ModbotRole.Union(Roles.Select(r => r.Role.Permissions));
}
