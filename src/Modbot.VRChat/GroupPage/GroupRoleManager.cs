using VRChat.API.Model;

namespace Modbot.VRChat.GroupPage;

/// <summary>
/// The managed group's roles on VRChat, as the Settings → Roles tab edits them: the list, a new
/// role, a change to one and deleting one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every request here is one a person asked for.</strong> The list is read when the Roles
/// tab opens and when Refresh is pressed; a write is one Save or Delete. Nothing polls.
/// </para>
/// <para>
/// Reads are on <see cref="VRChatEndpointClass.GroupsRoles"/> and the three writes share
/// <see cref="VRChatEndpointClass.GroupsRolesWrite"/>. Both are unmeasured and deliberately slow.
/// A 429 on either is a cold stop of that class only, and is never retried (spec 4.3.1).
/// </para>
/// <para>
/// Giving a member a role and taking one away are not here: role sync does those, on its own
/// class (<see cref="Moderation.GroupRoles"/>).
/// </para>
/// </remarks>
public sealed class GroupRoleManager(IVRChatGate gate)
{
    private readonly IVRChatGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>Every role the group has, in whatever order VRChat sends them.</summary>
    public Task<VRChatResult<List<GroupRole>>> ListAsync(string groupId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsRoles, groupId, "GetGroupRoles"),
            (client, token) => client.Groups.GetGroupRolesWithHttpInfoAsync(groupId, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }

    /// <summary>Creates a role. VRChat answers with the role as it stored it.</summary>
    public Task<VRChatResult<GroupRole>> CreateAsync(string groupId, GroupRoleChange role, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(role);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsRolesWrite, groupId, "CreateGroupRole"),
            (client, token) => client.Groups.CreateGroupRoleWithHttpInfoAsync(groupId, role.ForCreate(), cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }

    /// <summary>
    /// Changes a role's name, description and permissions. VRChat answers with every role the group
    /// now has.
    /// </summary>
    public Task<VRChatResult<List<GroupRole>>> UpdateAsync(
        string groupId, string roleId, GroupRoleChange role, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentNullException.ThrowIfNull(role);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsRolesWrite, groupId, "UpdateGroupRole"),
            (client, token) => client.Groups.UpdateGroupRoleWithHttpInfoAsync(groupId, roleId, role, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }

    /// <summary>Deletes a role. VRChat answers with the roles that are left.</summary>
    public Task<VRChatResult<List<GroupRole>>> DeleteAsync(string groupId, string roleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsRolesWrite, groupId, "DeleteGroupRole"),
            (client, token) => client.Groups.DeleteGroupRoleWithHttpInfoAsync(groupId, roleId, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }
}
