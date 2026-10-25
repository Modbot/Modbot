namespace Modbot.Core.Data.Entities;

/// <summary>Which service an automatic role lives in. <strong>Stored. Never renumber one.</strong></summary>
public enum AutomaticRoleKind
{
    /// <summary>A VRChat group role.</summary>
    VRChat = 0,

    /// <summary>A Discord role.</summary>
    Discord = 1,
}

/// <summary>
/// One time Modbot gave a moderator an automatic role. The table is <c>automatic_role_grant</c>.
/// </summary>
/// <remarks>
/// <para>
/// Modbot only takes back a role it gave itself (or one a rule's take-back list names), so it has
/// to remember which ones those are. A grant stays open while the role is held and is closed, not
/// deleted, when Modbot takes the role away: the closed rows say when Modbot last took a role from
/// someone, which the give-back wait reads.
/// </para>
/// <para>
/// The rule id is not a foreign key: deleting a rule must not forget that Modbot gave the role.
/// Role ids are text and never parsed or checked for a shape.
/// </para>
/// </remarks>
public class AutomaticRoleGrant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The rule that gave the role. When two rules give the same role, the first one to hold.</summary>
    public Guid RuleId { get; set; }

    /// <summary>The Modbot account of the moderator who was given the role.</summary>
    public Guid ModbotUserId { get; set; }

    public AutomaticRoleKind Kind { get; set; }

    /// <summary>The VRChat group role or the Discord role, depending on <see cref="Kind"/>.</summary>
    public string RoleId { get; set; } = string.Empty;

    public DateTimeOffset GivenAt { get; set; }

    /// <summary>When Modbot took the role away. Null while the grant is open.</summary>
    public DateTimeOffset? TakenAt { get; set; }

    /// <summary>Why Modbot took it, in the plain words the audit log uses. Null while the grant is open.</summary>
    public string? TakeReason { get; set; }
}
