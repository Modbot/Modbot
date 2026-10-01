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
        Describe(ModbotPermissions.ViewProfile, "See profiles", "A member's profile, their history and their past actions.", "Reading"),
        Describe(ModbotPermissions.ViewAnalytics, "See analytics", "Charts and daily totals.", "Reading"),
        Describe(ModbotPermissions.ViewLiveInstances, "See live instances", "Open instances right now and who is in each.", "Reading"),
        Describe(ModbotPermissions.ViewCalendar, "See calendar", "Planned events and where each is published.", "Reading"),
        Describe(ModbotPermissions.ViewGiveaways, "See giveaways", "Giveaways, their rules, who entered and how each draw went.", "Reading"),
        Describe(ModbotPermissions.ViewJoinRequests, "See join requests", "The people waiting to be let into the group.", "Reading"),
        Describe(ModbotPermissions.ViewAuditLog, "See the audit log", "Bans, kicks, role changes and other moderation history.", "Reading"),
        Describe(ModbotPermissions.UseAiChat, "Use AI chat", "Ask questions in Chat. Answers only use what this person can already see.", "Reading"),
        Describe(ModbotPermissions.UseAiPastLimits, "Use AI in excess of usage limits", "Not stopped by spend limits on this person or their roles, or by the monthly AI allowance. The limit for everyone still applies.", "Reading"),
        Describe(ModbotPermissions.ReadDiscordMessages, "Read Discord messages", "A member's stored Discord messages, deleted ones included.", "Reading"),
        Describe(ModbotPermissions.ViewOperationalLog, "See the operational log", "Sign-ins, settings changes, sync problems and account changes.", "Reading"),
        Describe(ModbotPermissions.ViewEvidence, "View evidence", "Open the screenshots and video attached to a case.", "Evidence"),
        Describe(ModbotPermissions.UploadEvidence, "Upload evidence", "Attach screenshots and video to a case.", "Evidence"),
        Describe(ModbotPermissions.DestroyEvidence, "Destroy evidence", "Permanently remove the files from a case. The record that they existed stays.", "Evidence"),
        Describe(ModbotPermissions.Kick, "Kick", "Remove somebody from an instance or the group.", "Moderation"),
        Describe(ModbotPermissions.Warn, "Warn", "Does nothing.", "Moderation"),
        Describe(ModbotPermissions.Ban, "Ban", "Ban somebody from the group. Needs a written report.", "Moderation"),
        Describe(ModbotPermissions.Unban, "Unban", "Lift a ban.", "Moderation"),
        Describe(ModbotPermissions.AnswerJoinRequests, "Answer join requests", "Approve or reject somebody asking to join the group.", "Moderation"),
        Describe(ModbotPermissions.BulkAction, "Act on many at once", "Does nothing.", "Moderation"),
        Describe(ModbotPermissions.ReviewTickets, "Review tickets", "Close the reviews that open when a moderator's pattern looks unusual, and dismiss moderation flags.", "Moderation"),
        Describe(ModbotPermissions.EditClassifications, "Edit the reason list", "Change the reasons moderators pick from when they act.", "Moderation"),
        Describe(ModbotPermissions.WriteNotes, "Write notes", "Write a note about somebody, and take one back. Reading notes needs the audit log.", "Moderation"),
        Describe(ModbotPermissions.ManageDiscordLinks, "Manage Discord links", "Unlink a member's Discord and VRChat accounts. Removes the roles Modbot gave them.", "Moderation"),
        Describe(ModbotPermissions.EditAgeVerification, "Edit 18+ verified", "Set or clear the 18+ verified mark on a VRChat user by hand. Syncs can only set it.", "Moderation"),
        Describe(ModbotPermissions.PairCompanion, "Pair a companion", "Pair the Windows companion to this account. Taking it away stops their companions.", "Moderation"),
        Describe(ModbotPermissions.EditGroupProfile, "Edit the group's profile", "Change the group's name, description, rules, languages, links and who can join, on VRChat.", "Administration"),
        Describe(ModbotPermissions.ManageGroupPosts, "Manage group posts", "Post in the group on VRChat, change a post and delete one.", "Administration"),
        Describe(ModbotPermissions.ManageGroupRoles, "Manage group roles", "Create, change and delete the group's roles and their permissions, on VRChat.", "Administration"),
        Describe(ModbotPermissions.ManageGroupInvites, "Manage group invites", "See the invites the group has sent on VRChat, and cancel one.", "Administration"),
        Describe(ModbotPermissions.ManageGroupGallery, "Manage the group gallery", "Remove an image from one of the group's galleries on VRChat.", "Administration"),
        Describe(ModbotPermissions.ManageCalendar, "Manage calendar", "Create, change and cancel events, and the calendar feed link.", "Administration"),
        Describe(ModbotPermissions.RunGiveaways, "Run giveaways", "Create, change, open, close, draw and cancel giveaways.", "Administration"),
        Describe(ModbotPermissions.ManageLists, "Manage lists", "Make, change and delete saved lists. Seeing them needs See members and See profiles.", "Administration"),
        Describe(ModbotPermissions.ManageUsers, "Manage users", "Add people, invite them, disable them and change their roles.", "Administration"),
        Describe(ModbotPermissions.ManageRoles, "Manage roles", "Create roles and decide what each one allows.", "Administration"),
        Describe(ModbotPermissions.ManageSettings, "Change settings", "VRChat account, group, proxy, retention, evidence storage, AI and integrations.", "Administration"),
        Describe(ModbotPermissions.ManageApiKeys, "Manage API keys and webhooks", "Create and revoke API keys, and set up webhooks.", "Administration"),
        Describe(ModbotPermissions.UseVRChatProxy, "Use the VRChat proxy", "Send requests to VRChat's API through Modbot as the service account.", "Administration"),
        Describe(ModbotPermissions.ManageAutoInvites, "Set up auto-invites", "Decide who Modbot invites to the group on its own, and switch it on or off.", "Administration"),
        Describe(ModbotPermissions.ImportOldData, "Import old data", "Upload another platform's records and write them into the log as history.", "Administration"),
        Describe(ModbotPermissions.ManageDiscordSync, "Manage role and ban sync", "Pair group roles with Discord roles, choose which side decides, and switch ban sync on.", "Administration"),
        Describe(ModbotPermissions.RunDiscordSync, "Run role and ban sync", "Copy the roles and bans that are already different between the two platforms.", "Administration"),
        Describe(ModbotPermissions.Administrator, "Administrator", "Everything, including things added in future versions.", "Administration"),
    ];

    /// <summary>
    /// Names a permission used to go by, still accepted on the way in. A role or an API key is
    /// stored as bits, so renaming a member changes nothing on disk; what it would break is a
    /// script or an integration that sends the old word, and that is what this keeps working.
    /// </summary>
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.Ordinal)
    {
        ["ViewLiveRooms"] = nameof(ModbotPermissions.ViewLiveInstances),
    };

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
            var match = All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal))
                ?? (Renamed.TryGetValue(name, out var current) ? All.First(p => p.Name == current) : null);
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
