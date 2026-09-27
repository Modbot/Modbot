using VRChat.API.Model;

namespace Modbot.VRChat.GroupPage;

/// <summary>
/// Changes the managed group's own page on VRChat: the fields an admin can edit on vrchat.com.
/// </summary>
/// <remarks>
/// <para>
/// One request per Save, and nothing else: no read before the write, no poll after it. VRChat
/// answers <c>PUT /groups/{groupId}</c> with the whole group as it now stands, and that answer is
/// what Modbot stores, so the page shows the new values straight away without asking again.
/// </para>
/// <para>
/// Fails the way every write through the gate fails: nothing retries, nothing throws, and the caller
/// gets VRChat's answer. A 429 cold stops <see cref="VRChatEndpointClass.GroupsEdit"/> and nothing
/// else (spec 4.3.1).
/// </para>
/// </remarks>
public sealed class GroupProfile(IVRChatGate gate)
{
    private readonly IVRChatGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>
    /// Sends the fields that changed. A field left null in <paramref name="request"/> is left out
    /// of the body, and VRChat leaves that field as it is — see <see cref="GroupProfileChange"/>
    /// for why this does not take the SDK's own request type.
    /// </summary>
    public Task<VRChatResult<Group>> UpdateAsync(string groupId, GroupProfileChange request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(request);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsEdit, groupId, "UpdateGroup"),
            (client, token) => client.Groups.UpdateGroupWithHttpInfoAsync(groupId, request, cancellationToken: token),
            // Somebody pressed Save and is waiting, so it goes ahead of anything queued on its own
            // lane. What keeps it below moderation is its backstop, not this (see the class).
            VRChatCallPriority.Interactive,
            ct);
    }
}
