using VRChat.API.Model;

namespace Modbot.VRChat.Moderation;

/// <summary>
/// The three things Modbot asks VRChat to do to a person in the managed group: kick them out, ban
/// them, lift a ban.
/// </summary>
/// <remarks>
/// <para>
/// M4 §4: this is the first place Modbot writes to VRChat rather than reading from it, and the
/// difference that matters is that these can fail. Nothing here retries, nothing here throws, and
/// nothing here records anything — it returns what VRChat said and lets the caller decide what is
/// true. "I thought I banned them" is a safety problem, not a UX blemish (M4 §4.1).
/// </para>
/// <para>
/// Every call goes through <see cref="IVRChatGate"/> at <see cref="VRChatCallPriority.Interactive"/>:
/// a moderator is watching a spinner, so it preempts queued background sync (spec 4.3.3). All three
/// draw on <see cref="VRChatEndpointClass.GroupsModerate"/>, whose rate is a deliberately low guess
/// until somebody measures the real one.
/// </para>
/// <para>
/// A thin wrapper rather than three calls inline at the endpoint, so the endpoint class, the
/// priority and the <c>…WithHttpInfoAsync</c> rule (spec 4.1.1) are decided in one place and cannot
/// drift apart between the three actions.
/// </para>
/// </remarks>
public sealed class GroupModeration(IVRChatGate gate)
{
    private readonly IVRChatGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>Removes a person from the group. VRChat's own word for this is "kick".</summary>
    public Task<VRChatResult<Success>> KickAsync(string groupId, string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsModerate, groupId, "KickGroupMember"),
            (client, token) => client.Groups.KickGroupMemberWithHttpInfoAsync(groupId, userId, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }

    /// <summary>Bans a person from the group. VRChat takes the person in the body, not the path.</summary>
    public Task<VRChatResult<GroupMember>> BanAsync(string groupId, string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var request = new BanGroupMemberRequest(userId);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsModerate, groupId, "BanGroupMember"),
            (client, token) => client.Groups.BanGroupMemberWithHttpInfoAsync(groupId, request, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }

    /// <summary>Lifts a ban.</summary>
    public Task<VRChatResult<GroupMember>> UnbanAsync(string groupId, string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsModerate, groupId, "UnbanGroupMember"),
            (client, token) => client.Groups.UnbanGroupMemberWithHttpInfoAsync(groupId, userId, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }
}
