namespace Modbot.Core.Data.Entities;

/// <summary>
/// What a <see cref="ModbotUser"/> is allowed to do, as a bitfield.
/// </summary>
/// <remarks>
/// <para>
/// Foundation spec section 7.3. A bitfield rather than a roles table because the deployment is
/// single-tenant and single-group (§2.4): there is no tenancy to scope a role to, and a staff list
/// of a dozen people does not need a join.
/// </para>
/// <para>
/// <strong>These values are persisted. Never renumber one.</strong> Changing a flag's bit silently
/// re-grants or revokes that permission on every existing account, and nothing errors.
/// <c>ModbotPermissionsTests</c> pins the assignments.
/// </para>
/// <para>
/// Flags for milestones not yet built are allocated here deliberately, for the same reason: a bit
/// claimed later would have to avoid every bit already written into the database, and that is
/// easier to get right once, now, than under pressure later.
/// </para>
/// </remarks>
[Flags]
public enum ModbotPermissions : long
{
    None = 0,

    // --- Reading ---
    ViewMembers = 1L << 0,
    ViewDossier = 1L << 1,
    ViewAnalytics = 1L << 2,

    /// <summary>Moderation history: bans, kicks, role changes (spec 5.9.4).</summary>
    ViewAuditLog = 1L << 3,

    /// <summary>
    /// Modbot's own operational record: sync failures, settings changes, API keys. Separate from
    /// <see cref="ViewAuditLog"/> because a moderator who should see bans does not automatically
    /// need to see that the owner reconfigured SMTP (spec 5.9.4).
    /// </summary>
    ViewOperationalLog = 1L << 4,

    // --- Administration ---
    ManageSettings = 1L << 5,
    ManageUsers = 1L << 6,
    ManageApiKeys = 1L << 7,

    // --- Moderation actions (M4, spec 8.3). Reserved now so the bits stay stable. ---
    Kick = 1L << 8,
    Ban = 1L << 9,
    Unban = 1L << 10,
    Warn = 1L << 11,
    BulkAction = 1L << 12,

    /// <summary>
    /// Deliberately separate from the action flags: the people being reviewed should not be the
    /// people closing the reviews (M4 spec 8.3).
    /// </summary>
    ReviewTickets = 1L << 13,

    EditClassifications = 1L << 14,

    /// <summary>
    /// Satisfies every requirement, including flags added after this account was created. Checked
    /// explicitly rather than defined as an OR of the others, so a new flag does not quietly go
    /// ungranted to the one account that is supposed to have everything.
    /// </summary>
    Administrator = 1L << 62,
}
