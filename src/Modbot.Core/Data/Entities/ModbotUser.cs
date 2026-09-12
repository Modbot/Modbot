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

    public ModbotPermissions Permissions { get; set; }

    /// <summary>Optional Discord link (spec 6.3). An opaque snowflake; never parsed.</summary>
    public string? DiscordUserId { get; set; }

    /// <summary>
    /// Disabled rather than deleted: facts reference the actor, and deleting the account would
    /// orphan the attribution that spec 5.9.1 exists to preserve.
    /// </summary>
    public bool IsDisabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}
