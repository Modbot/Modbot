using System.Text.Json;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat;

/// <summary>
/// Which VRChat group permission each group action Modbot sends needs, so that a refusal can say
/// what to grant instead of "Forbidden".
/// </summary>
/// <remarks>
/// <para>
/// Keyed by the operation name each call already gives its <see cref="VRChatEndpoint"/>, so the
/// map and the call cannot name the action differently.
/// </para>
/// <para>
/// Sources, checked 2026-09-27. The SDK documents only one: <c>KickGroupMember</c> needs "Remove
/// Group Members". The rest come from the descriptions in VRChat's own role editor, as the VRChat
/// wiki's Groups page quotes them: "Manage Group Invites" covers creating and cancelling invites
/// and accepting, declining and blocking join requests; "Manage Group Bans" covers banning,
/// unbanning and the ban list; "Assign Group Roles" covers giving and taking roles; "Manage Group
/// Calendar" covers creating, changing and publishing calendar entries; "Manage Group Data",
/// "Manage Group Announcement", "Manage Group Roles" and "Manage Group Galleries" cover the group's
/// profile, its posts, its roles and its galleries. The id behind each label
/// is from the role editor's own list (the web app's <c>vrchatPermissions.ts</c>).
/// </para>
/// <para>
/// An operation left out is one no source settles, such as reading the join request list or the
/// group's instances. A refusal of one of those says "a group permission" rather than a guess.
/// </para>
/// </remarks>
public static class VRChatGroupPermissions
{
    public const string Every = "*";
    public const string ManageInvites = "group-invites-manage";
    public const string RemoveMembers = "group-members-remove";
    public const string ManageBans = "group-bans-manage";
    public const string AssignRoles = "group-roles-assign";
    public const string ManageCalendar = "group-calendar-manage";
    public const string ViewAuditLog = "group-audit-view";
    public const string ViewAllMembers = "group-members-viewall";
    public const string ManageData = "group-data-manage";
    public const string ManageAnnouncement = "group-announcement-manage";
    public const string ManageRoles = "group-roles-manage";
    public const string ManageGalleries = "group-galleries-manage";

    private static readonly Dictionary<string, string> Needed = new(StringComparer.Ordinal)
    {
        ["RespondGroupJoinRequest"] = ManageInvites,
        ["CreateGroupInvite"] = ManageInvites,
        ["KickGroupMember"] = RemoveMembers,
        ["BanGroupMember"] = ManageBans,
        ["UnbanGroupMember"] = ManageBans,
        ["GetGroupBans"] = ManageBans,
        ["AddGroupMemberRole"] = AssignRoles,
        ["RemoveGroupMemberRole"] = AssignRoles,
        ["CreateGroupCalendarEvent"] = ManageCalendar,
        ["UpdateGroupCalendarEvent"] = ManageCalendar,
        ["DeleteGroupCalendarEvent"] = ManageCalendar,

        // The VRChat page's own writes. "Manage Group Data" covers the group's name, description,
        // rules, languages, links and who can join; "Manage Group Announcement" its posts; "Manage
        // Group Roles" creating, changing and deleting roles; "Manage Group Galleries" removing a
        // gallery image; and reading or cancelling the invites the group sent is "Manage Group
        // Invites", like sending one. Reading the roles and a gallery is left out: members can.
        ["UpdateGroup"] = ManageData,
        ["AddGroupPost"] = ManageAnnouncement,
        ["UpdateGroupPost"] = ManageAnnouncement,
        ["DeleteGroupPost"] = ManageAnnouncement,
        ["CreateGroupRole"] = ManageRoles,
        ["UpdateGroupRole"] = ManageRoles,
        ["DeleteGroupRole"] = ManageRoles,
        ["GetGroupInvites"] = ManageInvites,
        ["DeleteGroupInvite"] = ManageInvites,
        ["DeleteGroupGalleryImage"] = ManageGalleries,
    };

    /// <summary>
    /// The permissions every Modbot uses, for the Health page to say which the account lacks: the
    /// member list, the audit log, bans, kicks and join requests.
    /// </summary>
    /// <remarks>
    /// Not role assignment, which nothing sends yet, and not the calendar, which only a group that
    /// publishes events to VRChat needs; a refused calendar write says so on its own.
    /// </remarks>
    public static readonly IReadOnlyList<string> Used =
    [
        ViewAllMembers, ViewAuditLog, ManageBans, RemoveMembers, ManageInvites,
    ];

    /// <summary>
    /// VRChat's own words for the permissions above, as its role editor shows them. For text Modbot
    /// stores, such as an invite's problem; the web app has the full list.
    /// </summary>
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [ManageInvites] = "Manage Group Invites",
        [RemoveMembers] = "Remove Group Members",
        [ManageBans] = "Manage Group Bans",
        [AssignRoles] = "Assign Group Roles",
        [ManageCalendar] = "Manage Group Calendar",
        [ViewAuditLog] = "View Audit log",
        [ViewAllMembers] = "View All Members",
        [ManageData] = "Manage Group Data",
        [ManageAnnouncement] = "Manage Group Announcement",
        [ManageRoles] = "Manage Group Roles",
        [ManageGalleries] = "Manage Group Galleries",
    };

    /// <summary>
    /// VRChat's id for a permission from the SDK's enum name for it (<c>group_bans_manage</c> →
    /// <c>group-bans-manage</c>, <c>group_all</c> → <c>*</c>), read from the SDK's own mapping. A name
    /// the SDK does not map is given back as it is.
    /// </summary>
    /// <remarks>
    /// The group snapshot records permissions by the SDK's names; everything a person sees, and
    /// everything sent back to VRChat, uses VRChat's.
    /// </remarks>
    public static string IdOf(string sdkName)
    {
        ArgumentNullException.ThrowIfNull(sdkName);

        var field = typeof(global::VRChat.API.Model.GroupPermissions).GetField(sdkName);
        var member = field?.GetCustomAttributes(typeof(System.Runtime.Serialization.EnumMemberAttribute), false)
            .OfType<System.Runtime.Serialization.EnumMemberAttribute>()
            .FirstOrDefault();

        return member?.Value ?? sdkName;
    }

    /// <summary>The permission an operation needs, or null when no source settles it.</summary>
    public static string? NeededFor(string? operation) =>
        operation is not null && Needed.TryGetValue(operation, out var permission) ? permission : null;

    /// <summary>VRChat's label for a permission, or the id itself when this list does not have it.</summary>
    public static string Label(string permission) =>
        Labels.TryGetValue(permission, out var label) ? label : permission;

    /// <summary>The page in VRChat where a group's roles, and so their permissions, are changed.</summary>
    public static string RolesPage(string groupId) =>
        $"https://vrchat.com/home/group/{Uri.EscapeDataString(groupId)}/settings/roles";

    /// <summary>
    /// Whether the account holds a permission, as last read: true, false, or null when Modbot has
    /// not read the account's permissions yet.
    /// </summary>
    public static bool? Holds(IReadOnlyCollection<string>? held, string permission) =>
        held is null ? null : held.Contains(Every, StringComparer.Ordinal) || held.Contains(permission, StringComparer.Ordinal);

    /// <summary>
    /// The permissions Modbot uses that the account lacks, or null when they have not been read.
    /// </summary>
    public static IReadOnlyList<string>? MissingOf(IReadOnlyCollection<string>? held) =>
        held is null ? null : Used.Where(p => Holds(held, p) == false).ToList();

    /// <summary>
    /// A refusal for a missing group permission, or null when this answer is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a 403, and never one Cloudflare sent: that one is about where Modbot runs, not what
    /// the account may do (<see cref="WafBlock"/>).
    /// </para>
    /// <para>
    /// When the account's permissions have been read and it does hold the one the action needs,
    /// the 403 was for something else (a member ranked above the account, say), so this answers
    /// null and VRChat's own words are what is shown.
    /// </para>
    /// </remarks>
    /// <param name="operation">The operation name the call gave its endpoint.</param>
    /// <param name="body">VRChat's answer, for its own message.</param>
    /// <param name="settings">For the account's roles and permissions as last read. Null when not at hand.</param>
    public static MissingGroupPermission? Refusal(
        int statusCode,
        VRChatFailureKind kind,
        string? operation,
        string groupId,
        string? body,
        Settings? settings)
    {
        if (statusCode != 403 || kind == VRChatFailureKind.WafBlocked || string.IsNullOrEmpty(groupId))
            return null;

        var permission = NeededFor(operation);

        if (permission is not null && Holds(settings?.VRChatAccountPermissions, permission) == true)
            return null;

        return new MissingGroupPermission(
            permission,
            groupId,
            RoleNames(settings),
            VRChatRefusal.MessageOf(body));
    }

    /// <summary>
    /// The account's roles by name, as last read. Null when they have not been read; a role the
    /// stored role list does not name is left out rather than shown as an id.
    /// </summary>
    public static IReadOnlyList<string>? RoleNames(Settings? settings)
    {
        if (settings?.VRChatAccountRoleIds is not { } ids)
            return null;

        var roles = GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot)?.Roles ?? [];

        return ids
            .Select(id => roles.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal))?.Name)
            .OfType<string>()
            .Where(name => name.Length > 0)
            .ToList();
    }

    /// <summary>A sentence for text Modbot stores, such as an invite's problem.</summary>
    public static string Sentence(MissingGroupPermission missing)
    {
        ArgumentNullException.ThrowIfNull(missing);

        var what = missing.Permission is { } permission ? Label(permission) : "a group permission";
        return $"Modbot's VRChat account needs {what} in this group.";
    }

    /// <summary>
    /// The account's role ids and permissions from a group read's <c>myMember</c>, or nulls when
    /// the answer has none.
    /// </summary>
    /// <remarks>
    /// Read from VRChat's own JSON rather than the SDK's model, because the SDK's permission list
    /// is an enum that does not know every permission VRChat has (it has no
    /// <c>group-calendar-manage</c>), and a permission it cannot name is exactly the kind that
    /// matters here.
    /// </remarks>
    public static (List<string>? RoleIds, List<string>? Permissions) AccountFrom(string? groupJson)
    {
        if (string.IsNullOrWhiteSpace(groupJson))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(groupJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("myMember", out var me)
                || me.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (Strings(me, "roleIds"), Strings(me, "permissions"));
        }
        catch (JsonException)
        {
            return (null, null);
        }

        static List<string>? Strings(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToList()
                : null;
    }
}

/// <summary>
/// VRChat refused a group action because Modbot's own VRChat account lacks a group permission.
/// </summary>
/// <param name="Permission">
/// VRChat's id for the permission the action needs (<c>group-invites-manage</c>). Null when no
/// source says which one it is.
/// </param>
/// <param name="GroupId">The group, for the link to its roles page.</param>
/// <param name="Roles">
/// The group roles the account holds, by name, as last read. Null when Modbot has not read them.
/// </param>
/// <param name="Said">VRChat's own message, when it gave one.</param>
public sealed record MissingGroupPermission(
    string? Permission,
    string GroupId,
    IReadOnlyList<string>? Roles,
    string? Said);
