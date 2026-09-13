using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Analytics;

/// <summary>
/// Which group roles count as moderation roles.
/// </summary>
/// <remarks>
/// <para>
/// A role is a moderation role when it carries a permission that acts on other people: kicking
/// them out of an instance, removing them from the group, or banning them. Permissions that
/// manage the group's own things — posts, galleries, the calendar — are administration, and a
/// person who only holds those is not who a coverage gap is about.
/// </para>
/// <para>
/// The names are VRChat's own, as the group-info sync stores them. They are compared loosely on
/// the separator because VRChat has written the same permission both as
/// <c>group_instance_moderate</c> and <c>group-instance-moderate</c> in different places
/// (audit-log research §6).
/// </para>
/// </remarks>
public static class ModerationRoles
{
    public static readonly IReadOnlyList<string> Permissions =
    [
        "group_instance_moderate",
        "group_members_remove",
        "group_bans_manage",
    ];

    public static bool IsModerationRole(GroupRoleSnapshot role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return role.Permissions.Any(p => Permissions.Contains(Normalise(p), StringComparer.Ordinal));
    }

    private static string Normalise(string permission) => permission.Replace('-', '_').ToLowerInvariant();
}
