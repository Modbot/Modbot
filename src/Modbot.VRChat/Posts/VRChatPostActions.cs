using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.VRChat.GroupPage;

namespace Modbot.VRChat.Posts;

/// <summary>
/// <see cref="IVRChatPostActions"/> through the gate: the Marketing tab's Edit and Delete on a post
/// already in the VRChat group (posts design §3.6, §4.5).
/// </summary>
/// <remarks>
/// <para>
/// The VRChat page's own calls (<see cref="GroupPosts"/>), at interactive priority because a person
/// is waiting on the answer, on <c>groups.posts.write</c>'s shared budget. One request each, never
/// sent again: a rate limit is said, and the person tries again later (foundation §4.3.1).
/// </para>
/// <para>
/// An edit sends the whole post again -- who sees it, the roles, and the picture id it went with --
/// because VRChat replaces the whole post and removes a picture an edit leaves out; it never
/// notifies the members again. A delete VRChat answers 404 for was already gone, and counts as done.
/// </para>
/// </remarks>
public sealed class VRChatPostActions(GroupPosts posts) : IVRChatPostActions
{
    public const string RateLimited = "VRChat is not taking posts right now. Try again in a few minutes.";
    public const string Gone = "That post is gone from VRChat.";

    private readonly GroupPosts _posts = posts ?? throw new ArgumentNullException(nameof(posts));

    public async Task<PostSiteOutcome> EditAsync(
        string groupId,
        string postId,
        string title,
        string text,
        string visibility,
        IReadOnlyList<string> roleIds,
        string? imageId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);

        var request = PostVRChatSender.EditRequest(
            title,
            text,
            new VRChatPostOptions(visibility, roleIds, Notify: false, ImageId: imageId));

        var result = await _posts.UpdateAsync(groupId, postId, request, ct).ConfigureAwait(false);

        if (result.Success)
            return PostSiteOutcome.Ok;

        if (result.StatusCode == 404)
            return PostSiteOutcome.Failed(Gone, gone: true);

        return Refused(result, "UpdateGroupPost", groupId);
    }

    public async Task<PostSiteOutcome> DeleteAsync(string groupId, string postId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);

        var result = await _posts.DeleteAsync(groupId, postId, ct).ConfigureAwait(false);

        // Deleted there already, by somebody in VRChat: nothing failed.
        if (result.Success || result.StatusCode == 404)
            return PostSiteOutcome.Ok;

        return Refused(result, "DeleteGroupPost", groupId);
    }

    private static PostSiteOutcome Refused<T>(VRChatResult<T> result, string operation, string groupId)
    {
        if (result.IsRateLimited || result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting)
            return PostSiteOutcome.Failed(RateLimited);

        if (result.Kind == VRChatFailureKind.NotConfigured)
            return new PostSiteOutcome(false, "VRChat is not set up.", BotOffline: true);

        // A 403 because Modbot's VRChat account lacks the permission: named, so the person knows
        // what to give it.
        if (VRChatGroupPermissions.Refusal(result.StatusCode, result.Kind, operation, groupId, result.RawResponse, settings: null) is { Permission: not null } missing)
            return PostSiteOutcome.Failed(VRChatGroupPermissions.Sentence(missing));

        var said = (result.Kind == VRChatFailureKind.WafBlocked ? null : VRChatRefusal.MessageOf(result.RawResponse))
            ?? result.ErrorMessage
            ?? $"VRChat answered {result.StatusCode}.";

        return PostSiteOutcome.Failed(said);
    }
}
