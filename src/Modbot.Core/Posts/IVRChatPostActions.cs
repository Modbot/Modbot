namespace Modbot.Core.Posts;

/// <summary>
/// What a person does to a post already in the VRChat group from the Marketing tab (posts design
/// §3.6, §4.5): edit its title and text, or delete it.
/// </summary>
/// <remarks>
/// In Core so the API can ask without knowing how VRChat is reached, as it does through
/// <see cref="IDiscordPostActions"/>. Each is one call through the gate, made at once so the person
/// sees whether it worked, and never sent again by Modbot: a rate limit is said, not waited out.
/// The API writes the row and the fact.
/// </remarks>
public interface IVRChatPostActions
{
    /// <summary>
    /// Replaces the post's title and text. VRChat takes the whole post, so who sees it, the roles and
    /// the picture (<paramref name="imageId"/>) are sent again as they were; members are not
    /// notified again.
    /// </summary>
    Task<PostSiteOutcome> EditAsync(
        string groupId,
        string postId,
        string title,
        string text,
        string visibility,
        IReadOnlyList<string> roleIds,
        string? imageId,
        CancellationToken ct = default);

    /// <summary>Deletes the post. One VRChat answers 404 for is already gone, and counts as done.</summary>
    Task<PostSiteOutcome> DeleteAsync(string groupId, string postId, CancellationToken ct = default);
}

/// <summary>A process with no VRChat side. Every action answers that VRChat is not set up.</summary>
public sealed class NoVRChatPostActions : IVRChatPostActions
{
    public const string NotSetUp = "This deployment is not set up to act in VRChat.";

    public Task<PostSiteOutcome> EditAsync(
        string groupId,
        string postId,
        string title,
        string text,
        string visibility,
        IReadOnlyList<string> roleIds,
        string? imageId,
        CancellationToken ct = default)
        => Task.FromResult(new PostSiteOutcome(false, NotSetUp, BotOffline: true));

    public Task<PostSiteOutcome> DeleteAsync(string groupId, string postId, CancellationToken ct = default)
        => Task.FromResult(new PostSiteOutcome(false, NotSetUp, BotOffline: true));
}
