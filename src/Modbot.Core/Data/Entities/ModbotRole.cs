namespace Modbot.Core.Data.Entities;

/// <summary>
/// A named set of permissions. A user holds one or more; what they may do is the union.
/// </summary>
/// <remarks>
/// <para>
/// Accounts and access design §3. The <see cref="ModbotPermissions"/> bitfield stays the primitive
/// that <c>RequiresFlag</c> enforces (foundation §7.3); a role is only a name for a value of it,
/// so an administrator assigns "Moderator" rather than ticking eleven boxes per person and the
/// roles page can answer "why can Alice do that?" in one look.
/// </para>
/// <para>
/// There is deliberately no per-user override on top of roles. The foundation spec never asked for
/// one, and it is the thing that makes the roles page stop being the answer.
/// </para>
/// </remarks>
public class ModbotRole
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>As typed. Shown on badges and in the audit log.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Upper-invariant form of <see cref="Name"/>, uniquely indexed.</summary>
    public string NameNormalized { get; set; } = string.Empty;

    /// <summary>One line, shown under the name on the roles page. May be empty.</summary>
    public string Description { get; set; } = string.Empty;

    public ModbotPermissions Permissions { get; set; }

    /// <summary>
    /// Seeded by the migration rather than created by a person. Built-in roles can be edited
    /// (except Administrator, which always means everything) and never deleted.
    /// </summary>
    public bool IsBuiltIn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<ModbotUserRole> Users { get; set; } = [];

    /// <summary>What a set of roles allows, taken together.</summary>
    public static ModbotPermissions Union(IEnumerable<ModbotPermissions> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var union = ModbotPermissions.None;
        foreach (var p in permissions)
            union |= p;

        return union;
    }

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();
}

/// <summary>Which roles a user holds. One row per (user, role).</summary>
public class ModbotUserRole
{
    public Guid UserId { get; set; }

    public ModbotUser User { get; set; } = null!;

    public Guid RoleId { get; set; }

    public ModbotRole Role { get; set; } = null!;
}

/// <summary>
/// The three roles every deployment has from the start.
/// </summary>
/// <remarks>
/// The ids are fixed so the migration that seeds the rows and the code that refers to them
/// cannot disagree about which row is which. They are visibly synthetic on purpose.
/// </remarks>
public static class BuiltInRoles
{
    public static readonly Guid AdministratorId = new("00000000-0000-0000-0000-000000000001");
    public static readonly Guid ModeratorId = new("00000000-0000-0000-0000-000000000002");
    public static readonly Guid ViewerId = new("00000000-0000-0000-0000-000000000003");

    /// <summary>
    /// Everything, including permissions that do not exist yet. Checked as a flag rather than
    /// expanded (foundation §7.3), so this role never needs a migration when a flag is added.
    /// </summary>
    public const ModbotPermissions AdministratorPermissions = ModbotPermissions.Administrator;

    /// <summary>Moderation plus the evidence to back it up, and the history to check first.</summary>
    public const ModbotPermissions ModeratorPermissions =
        ModbotPermissions.ViewMembers
        | ModbotPermissions.ViewProfile
        | ModbotPermissions.ViewAnalytics
        | ModbotPermissions.ViewAuditLog
        | ModbotPermissions.Kick
        | ModbotPermissions.Ban
        | ModbotPermissions.Unban
        | ModbotPermissions.Warn
        | ModbotPermissions.ViewEvidence
        | ModbotPermissions.UploadEvidence;

    /// <summary>Read-only: the same views a moderator has, and nothing that changes anything.</summary>
    public const ModbotPermissions ViewerPermissions =
        ModbotPermissions.ViewMembers
        | ModbotPermissions.ViewProfile
        | ModbotPermissions.ViewAnalytics
        | ModbotPermissions.ViewAuditLog;

    public static IReadOnlyList<(Guid Id, string Name, string Description, ModbotPermissions Permissions)> All { get; } =
    [
        (AdministratorId, "Administrator", "Can do everything, including things added in future versions.", AdministratorPermissions),
        (ModeratorId, "Moderator", "Kick, ban, warn and unban; attach and view evidence; read the audit log.", ModeratorPermissions),
        (ViewerId, "Viewer", "Can look at members, history and analytics, and change nothing.", ViewerPermissions),
    ];
}
