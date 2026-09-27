using VRChat.API.Model;

namespace Modbot.VRChat.GroupPage;

/// <summary>
/// The invites the managed group has sent and nobody has answered yet: the list, and cancelling one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every request here is one a person asked for.</strong> The list is read when the Invites
/// tab opens, when a page is turned and when Refresh is pressed; a cancel is one press. Nothing
/// polls.
/// </para>
/// <para>
/// The list is on <see cref="VRChatEndpointClass.GroupsInvitesRead"/> and cancelling on
/// <see cref="VRChatEndpointClass.GroupsInvitesCancel"/>, both apart from
/// <see cref="VRChatEndpointClass.GroupsInvites"/>, which paces sending an invite (see
/// <see cref="Invites.GroupInvites"/>). A 429 is a cold stop of its own class, never retried
/// (spec 4.3.1).
/// </para>
/// </remarks>
public sealed class GroupSentInvites(IVRChatGate gate)
{
    private readonly IVRChatGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>The most VRChat returns in one page of invites.</summary>
    public const int MaxPageSize = 100;

    /// <summary>
    /// One page of the people the group has invited. VRChat answers with a member row per person,
    /// and sends no total.
    /// </summary>
    public Task<VRChatResult<List<GroupMember>>> ListAsync(string groupId, int n, int offset, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var size = Math.Clamp(n, 1, MaxPageSize);
        var from = Math.Max(0, offset);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsInvitesRead, groupId, "GetGroupInvites"),
            (client, token) => client.Groups.GetGroupInvitesWithHttpInfoAsync(groupId, size, from, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }

    /// <summary>Takes back the invite sent to one person.</summary>
    /// <param name="userId">Who was invited. Opaque text, never validated (spec 3.1.1).</param>
    public Task<VRChatResult<object>> CancelAsync(string groupId, string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsInvitesCancel, groupId, "DeleteGroupInvite"),
            (client, token) => client.Groups.DeleteGroupInviteWithHttpInfoAsync(groupId, userId, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }
}
