namespace Modbot.VRChat.Moderation;

/// <summary>
/// Gives a group member a role, and takes one away.
/// </summary>
/// <remarks>
/// <para>
/// The VRChat half of role sync (M5 §3). Like <see cref="GroupModeration"/> it is a thin wrapper
/// so that the endpoint class, the priority and the <c>…WithHttpInfoAsync</c> rule (spec 4.1.1)
/// are decided once; unlike it, these run at <see cref="VRChatCallPriority.Background"/>, because
/// a sync pass can be hundreds of changes long and nobody is watching a spinner for any one of
/// them. A moderator pressing Ban must never queue behind a role sweep.
/// </para>
/// <para>
/// <strong>The rate limit here is not measured.</strong> Spec 4.2 declared
/// <see cref="VRChatEndpointClass.ModerationWrite"/> for role changes and the budget was set
/// deliberately low — about one request every three seconds — before anything used it. That
/// number is a guess that is meant to be too low, not a finding, and spec 4.3.4's standing
/// question about these two endpoints is still open. The sync's own cap on how many changes one
/// pass makes is the second brake, so a first run against a large group spreads over passes
/// rather than emptying the bucket in one go.
/// </para>
/// </remarks>
public sealed class GroupRoles(IVRChatGate gate)
{
    private readonly IVRChatGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>Gives a member one of the group's roles.</summary>
    public Task<VRChatResult<List<string>>> GiveAsync(
        string groupId, string userId, string roleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.ModerationWrite, groupId, "AddGroupMemberRole"),
            (client, token) => client.Groups.AddGroupMemberRoleWithHttpInfoAsync(groupId, userId, roleId, cancellationToken: token),
            VRChatCallPriority.Background,
            ct);
    }

    /// <summary>Takes one of the group's roles away from a member.</summary>
    public Task<VRChatResult<List<string>>> TakeAsync(
        string groupId, string userId, string roleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.ModerationWrite, groupId, "RemoveGroupMemberRole"),
            (client, token) => client.Groups.RemoveGroupMemberRoleWithHttpInfoAsync(groupId, userId, roleId, cancellationToken: token),
            VRChatCallPriority.Background,
            ct);
    }
}
