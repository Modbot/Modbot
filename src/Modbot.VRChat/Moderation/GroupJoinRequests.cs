using VRChat.API.Model;

namespace Modbot.VRChat.Moderation;

/// <summary>
/// The people waiting to be let into the managed group, and the two answers a moderator can give
/// one of them (join requests design).
/// </summary>
/// <remarks>
/// <para>
/// VRChat has a real list here — <c>GET /groups/{groupId}/requests</c> — so the Requests screen is
/// not reconstructed from the audit log. That matters: a queue rebuilt from
/// <c>vrchat.group.request.create</c> facts could only ever hold the requests Modbot happened to
/// be watching for, and would quietly leave out everybody who asked before this deployment
/// existed.
/// </para>
/// <para>
/// Both answers are writes against the group, so they can fail, and they fail the way
/// <see cref="GroupModeration"/>'s three do: nothing retries, nothing throws, and the caller is
/// handed what VRChat said. The one failure worth naming is a <strong>404</strong>, which is what
/// VRChat answers when there is no longer a request to answer — somebody accepted it in VRChat, or
/// the person withdrew it — and is a stale row rather than a broken one.
/// </para>
/// <para>
/// Reads are on <see cref="VRChatEndpointClass.GroupsRequests"/> and the two answers on
/// <see cref="VRChatEndpointClass.GroupsRequestsAnswer"/>; both are unmeasured and both are
/// deliberately slow. Everything here runs at <see cref="VRChatCallPriority.Interactive"/>,
/// because nothing calls it except a moderator looking at the screen.
/// </para>
/// </remarks>
public sealed class GroupJoinRequests(IVRChatGate gate)
{
    private readonly IVRChatGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>The largest page VRChat serves for a list like this one.</summary>
    public const int MaxPageSize = 100;

    /// <summary>
    /// One page of the outstanding join requests, newest first as VRChat orders them.
    /// </summary>
    /// <remarks>
    /// One request per page a moderator asks for, and no sweep: the list is small, it changes
    /// whenever anybody answers one in VRChat, and a stored copy would be wrong in exactly the
    /// way that makes a moderator press a button on a row that is no longer there.
    /// </remarks>
    /// <param name="n">How many to return. Clamped to <see cref="MaxPageSize"/>.</param>
    /// <param name="offset">How many to skip. VRChat pages this list by offset, not by cursor.</param>
    public Task<VRChatResult<List<GroupMember>>> ListAsync(
        string groupId, int n, int offset, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var size = Math.Clamp(n, 1, MaxPageSize);
        var from = Math.Max(0, offset);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsRequests, groupId, "GetGroupRequests"),
            // `blocked` is left at VRChat's default, which is the requests that are waiting. The
            // blocked ones are a different list answering a different question, and Modbot has no
            // screen that asks it.
            (client, token) => client.Groups.GetGroupRequestsWithHttpInfoAsync(
                groupId, size, from, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }

    /// <summary>Lets somebody in.</summary>
    public Task<VRChatResult<object>> ApproveAsync(string groupId, string userId, CancellationToken ct = default)
        => AnswerAsync(groupId, userId, GroupJoinRequestAction.Accept, ct);

    /// <summary>
    /// Turns somebody down. Their request goes away; they may ask again.
    /// </summary>
    /// <remarks>
    /// VRChat's body also carries a <c>block</c> flag, which stops the person asking again. Modbot
    /// never sets it: blocking somebody is a heavier decision than declining one request, it is
    /// not undoable from this screen, and a moderator who wants that has Ban.
    /// </remarks>
    public Task<VRChatResult<object>> RejectAsync(string groupId, string userId, CancellationToken ct = default)
        => AnswerAsync(groupId, userId, GroupJoinRequestAction.Reject, ct);

    private Task<VRChatResult<object>> AnswerAsync(
        string groupId, string userId, GroupJoinRequestAction action, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var request = new RespondGroupJoinRequest(action, block: false);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsRequestsAnswer, groupId, "RespondGroupJoinRequest"),
            (client, token) => client.Groups.RespondGroupJoinRequestWithHttpInfoAsync(
                groupId, userId, request, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }
}
