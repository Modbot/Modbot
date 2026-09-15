using Modbot.Core.Data.Entities;

namespace Modbot.Api.Auth;

/// <summary>One permission, described for a person.</summary>
/// <param name="Name">The enum member, which is also how the API refers to it.</param>
/// <param name="Value">The bit, for callers that want to compose the bitfield.</param>
/// <param name="Label">Short, plain, shown beside a checkbox.</param>
/// <param name="Description">One line under the label.</param>
/// <param name="Group">Which heading it sits under on the roles page.</param>
public sealed record PermissionInfo(string Name, long Value, string Label, string Description, string Group);

/// <summary>
/// Every permission, with the words the roles page shows for it.
/// </summary>
/// <remarks>
/// Server-side rather than in the SPA for the same reason fact labels are: the enum lives here,
/// the list is served here, and a browser-side table would drift from an enum it cannot see. The
/// API also returns permissions as <em>names</em> because the bitfield is 64 bits wide and
/// <c>Administrator</c> is bit 62, which JavaScript's number type cannot carry alongside any other
/// bit without rounding.
/// </remarks>
public static class PermissionCatalog
{
    public static IReadOnlyList<PermissionInfo> All { get; } =
    [
        Describe(ModbotPermissions.ViewMembers, "See members", "The member list and who is in the group.", "Reading"),
        Describe(ModbotPermissions.ViewProfile, "See profiles", "A member's history, notes and past actions.", "Reading"),
        Describe(ModbotPermissions.ViewAnalytics, "See analytics", "Charts and daily totals.", "Reading"),
        Describe(ModbotPermissions.ViewLiveRooms, "See live instances", "Open instances right now and who is in each.", "Reading"),
        Describe(ModbotPermissions.ViewAuditLog, "See the audit log", "Bans, kicks, role changes and other moderation history.", "Reading"),
        Describe(ModbotPermissions.ViewOperationalLog, "See the operational log", "Sign-ins, settings changes, sync problems and account changes.", "Reading"),
        Describe(ModbotPermissions.ViewEvidence, "View evidence", "Open the screenshots and video attached to a case.", "Evidence"),
        Describe(ModbotPermissions.UploadEvidence, "Upload evidence", "Attach screenshots and video to a case.", "Evidence"),
        Describe(ModbotPermissions.DestroyEvidence, "Destroy evidence", "Permanently remove the files from a case. The record that they existed stays.", "Evidence"),
        Describe(ModbotPermissions.Kick, "Kick", "Remove somebody from an instance or the group.", "Moderation"),
        Describe(ModbotPermissions.Warn, "Warn", "Send somebody a warning.", "Moderation"),
        Describe(ModbotPermissions.Ban, "Ban", "Ban somebody from the group. Needs a written report.", "Moderation"),
        Describe(ModbotPermissions.Unban, "Unban", "Lift a ban.", "Moderation"),
        Describe(ModbotPermissions.BulkAction, "Act on many at once", "Kick, ban or warn a whole list in one go.", "Moderation"),
        Describe(ModbotPermissions.ReviewTickets, "Review tickets", "Close the reviews that open when a moderator's pattern looks unusual.", "Moderation"),
        Describe(ModbotPermissions.EditClassifications, "Edit the reason list", "Change the reasons moderators pick from when they act.", "Moderation"),
        Describe(ModbotPermissions.EditAgeVerification, "Edit 18+ verified", "Set or clear the 18+ verified mark on a VRChat user by hand. Syncs can only set it.", "Moderation"),
        Describe(ModbotPermissions.ManageUsers, "Manage users", "Add people, invite them, disable them and change their roles.", "Administration"),
        Describe(ModbotPermissions.ManageRoles, "Manage roles", "Create roles and decide what each one allows.", "Administration"),
        Describe(ModbotPermissions.ManageSettings, "Change settings", "VRChat account, group, proxy, retention, evidence storage, AI and integrations.", "Administration"),
        Describe(ModbotPermissions.ManageApiKeys, "Manage API keys", "Create and revoke keys for the read API.", "Administration"),
        Describe(ModbotPermissions.Administrator, "Administrator", "Everything, including things added in future versions.", "Administration"),
    ];

    /// <summary>The names of every permission <paramref name="held"/> includes, in catalogue order.</summary>
    public static IReadOnlyList<string> NamesOf(ModbotPermissions held)
        => All.Where(p => held.HasFlag((ModbotPermissions)p.Value)).Select(p => p.Name).ToList();

    /// <summary>
    /// Parses names back into a bitfield, refusing any name that is not in the catalogue. The
    /// error names the offender so the caller can fix the right box.
    /// </summary>
    public static ModbotPermissions Parse(IEnumerable<string> names, out string? error)
    {
        ArgumentNullException.ThrowIfNull(names);

        var held = ModbotPermissions.None;
        error = null;

        foreach (var name in names)
        {
            var match = All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
            if (match is null)
            {
                error = $"'{name}' is not a permission.";
                return ModbotPermissions.None;
            }

            held |= (ModbotPermissions)match.Value;
        }

        return held;
    }

    private static PermissionInfo Describe(ModbotPermissions flag, string label, string description, string group)
        => new(flag.ToString(), (long)flag, label, description, group);
}
