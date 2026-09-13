using VRChat.API.Model;

namespace Modbot.Api.Features.Onboarding.SelectGroup;

/// <param name="Id">The group id, an opaque string — never parsed or validated (spec 3.1.1).</param>
/// <param name="Name">The group's display name.</param>
/// <param name="MemberCount">
/// As VRChat reports it. Never compared against a hardcoded ceiling: capacity is data, and groups
/// hold per-group exemptions that change without notice (spec 3.1).
/// </param>
/// <param name="IconUrl">For the avatar in the picker, or null.</param>
/// <param name="ShortCode">The <c>NAME.1234</c> form, so two similarly named groups are separable.</param>
/// <param name="Permissions">
/// The moderation permissions this account holds here, as VRChat's own strings. Shown rather than
/// reduced to a yes/no, because an operator whose group is missing from the list needs to be told
/// which permission to grant, not merely that something is absent.
/// </param>
/// <param name="MissingPermissions">
/// The ones Modbot uses that this account does not hold. Empty means full coverage.
/// </param>
public sealed record GroupCandidate(
    string Id,
    string Name,
    int MemberCount,
    string? IconUrl,
    string? ShortCode,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> MissingPermissions);

/// <param name="Groups">Qualifying groups, most populous first.</param>
/// <param name="TotalGroups">
/// How many groups the account belongs to in total. The difference between this and the length of
/// <paramref name="Groups"/> is the number filtered out for lacking permissions, which is what
/// turns "no groups found" into an explanation.
/// </param>
/// <param name="RequiredPermissions">
/// What a group needs for Modbot to be useful there, so the "none qualify" message can name them.
/// </param>
public sealed record GroupCandidatesResponse(
    IReadOnlyList<GroupCandidate> Groups,
    int TotalGroups,
    IReadOnlyList<string> RequiredPermissions);

/// <summary>
/// Which VRChat group permissions make a group worth offering.
/// </summary>
/// <remarks>
/// <para>
/// Spec 7.1 step 4 says to list groups "where the authenticated account holds moderator
/// permissions", and to explain the requirement rather than show an empty list. That requires
/// naming the permissions, which is what this does.
/// </para>
/// <para>
/// A group qualifies on holding <em>any</em> of these, not all of them. Communities delegate
/// narrowly — a bot account that can read the audit log and nothing else is a real and reasonable
/// configuration, and refusing to manage it would be Modbot deciding how someone else's group is
/// run. What the operator gets instead is the group in the list with its gaps named, so they can
/// see before choosing that bans will not sync until somebody grants one more permission.
/// </para>
/// </remarks>
public static class ModeratorPermissions
{
    /// <summary>VRChat's wildcard: the account can do everything in the group.</summary>
    public const string All = "*";

    /// <summary>The permissions Modbot's own features are built on, in spec order of use.</summary>
    public static IReadOnlyList<GroupPermissions> Required { get; } =
    [
        GroupPermissions.group_members_viewall,
        GroupPermissions.group_audit_view,
        GroupPermissions.group_bans_manage,
        GroupPermissions.group_members_remove,
    ];

    /// <summary>Why each one matters, so the wizard can say so instead of printing a slug.</summary>
    public static IReadOnlyDictionary<string, string> Explanations { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Name(GroupPermissions.group_members_viewall)] =
                "See the full member list — without it Modbot cannot sync members at all.",
            [Name(GroupPermissions.group_audit_view)] =
                "Read the group audit log, which is Modbot's authoritative record of what happened.",
            [Name(GroupPermissions.group_bans_manage)] =
                "Read and apply bans.",
            [Name(GroupPermissions.group_members_remove)] =
                "Kick members.",
        };

    /// <summary>VRChat's wire name for a permission, not the C# identifier.</summary>
    public static string Name(GroupPermissions permission) => permission switch
    {
        GroupPermissions.group_all => All,
        GroupPermissions.group_members_viewall => "group-members-viewall",
        GroupPermissions.group_audit_view => "group-audit-view",
        GroupPermissions.group_bans_manage => "group-bans-manage",
        GroupPermissions.group_members_remove => "group-members-remove",
        GroupPermissions.group_members_manage => "group-members-manage",
        GroupPermissions.group_roles_assign => "group-roles-assign",
        GroupPermissions.group_roles_manage => "group-roles-manage",
        GroupPermissions.group_instance_moderate => "group-instance-moderate",
        GroupPermissions.group_invites_manage => "group-invites-manage",
        _ => permission.ToString().Replace('_', '-'),
    };

    /// <summary>The required names, for the response.</summary>
    public static IReadOnlyList<string> RequiredNames { get; } = [.. Required.Select(Name)];

    /// <summary>Whether this account can do anything useful in a group with these permissions.</summary>
    public static bool Qualifies(IEnumerable<GroupPermissions> held)
    {
        ArgumentNullException.ThrowIfNull(held);

        var set = held.ToHashSet();

        return set.Contains(GroupPermissions.group_all) || Required.Any(set.Contains);
    }

    /// <summary>The required permissions this account does not hold. Empty when it holds the wildcard.</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<GroupPermissions> held)
    {
        ArgumentNullException.ThrowIfNull(held);

        var set = held.ToHashSet();

        return set.Contains(GroupPermissions.group_all)
            ? []
            : [.. Required.Where(p => !set.Contains(p)).Select(Name)];
    }
}
