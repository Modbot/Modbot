using VRChat.API.Model;

namespace Modbot.VRChat.GroupPage;

/// <summary>
/// The managed group's posts on VRChat: the list, a new post, a change to one and deleting one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every request here is one a person asked for.</strong> The list is read when the Posts
/// tab opens, when a page is turned and when Refresh is pressed; a write is one Post, Save or
/// Delete. Nothing is stored and nothing polls, so there is no sweep to budget.
/// </para>
/// <para>
/// Reads are on <see cref="VRChatEndpointClass.GroupsPosts"/> and the three writes share
/// <see cref="VRChatEndpointClass.GroupsPostsWrite"/>. Both are unmeasured and deliberately slow.
/// A 429 on either is a cold stop of that class only, and is never retried (spec 4.3.1).
/// </para>
/// <para>
/// VRChat names a post's id <c>notificationId</c> in its paths. It is an opaque string like every
/// other VRChat id and is passed through untouched (spec 3.1.1).
/// </para>
/// </remarks>
public sealed class GroupPosts(IVRChatGate gate)
{
    private readonly IVRChatGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>The most VRChat returns in one page of posts.</summary>
    public const int MaxPageSize = 100;

    /// <summary>One page of the group's posts, newest first as VRChat orders them.</summary>
    /// <param name="n">How many to return. Clamped to between one and <see cref="MaxPageSize"/>.</param>
    /// <param name="offset">How many to skip. VRChat pages this list by offset.</param>
    public Task<VRChatResult<GroupPostsResponse>> ListAsync(string groupId, int n, int offset, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var size = Math.Clamp(n, 1, MaxPageSize);
        var from = Math.Max(0, offset);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsPosts, groupId, "GetGroupPosts"),
            // `publicOnly` left unset: Modbot's account is in the group, and the page shows what
            // the group's own members see, members-only posts included.
            (client, token) => client.Groups.GetGroupPostsWithHttpInfoAsync(
                groupId, size, from, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }

    /// <summary>Posts in the group. VRChat answers with the post as it stored it.</summary>
    public Task<VRChatResult<GroupPost>> CreateAsync(string groupId, CreateGroupPostRequest post, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(post);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsPostsWrite, groupId, "AddGroupPost"),
            (client, token) => client.Groups.AddGroupPostWithHttpInfoAsync(groupId, post, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }

    /// <summary>
    /// Replaces a post's title, text, audience and picture. VRChat takes the whole post, not the
    /// fields that changed, so a picture that should stay has to be sent again.
    /// </summary>
    public Task<VRChatResult<GroupPost>> UpdateAsync(
        string groupId, string postId, CreateGroupPostRequest post, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        ArgumentNullException.ThrowIfNull(post);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsPostsWrite, groupId, "UpdateGroupPost"),
            (client, token) => client.Groups.UpdateGroupPostWithHttpInfoAsync(
                groupId, postId, post, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }

    /// <summary>Deletes a post.</summary>
    public Task<VRChatResult<Success>> DeleteAsync(string groupId, string postId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsPostsWrite, groupId, "DeleteGroupPost"),
            (client, token) => client.Groups.DeleteGroupPostWithHttpInfoAsync(groupId, postId, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }
}
