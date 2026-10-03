namespace Modbot.Core.Posts;

/// <summary>
/// What a person does to a post already on Bluesky from the Marketing tab (posts design §4.2c,
/// §4.5): delete it. Bluesky posts cannot be edited, so there is no edit.
/// </summary>
/// <remarks>
/// In Core so the API asks without knowing how Bluesky is reached, as it does through
/// <see cref="IDiscordPostActions"/> and <see cref="IVRChatPostActions"/>. One call, made at once so
/// the person sees whether it worked, and never sent again by Modbot: a rate limit is said, not
/// waited out. The API writes the row and the fact.
/// </remarks>
public interface IBlueskyPostActions
{
    /// <summary>
    /// Deletes the post at <paramref name="recordKey"/> on the account <paramref name="did"/>. One
    /// that is already gone counts as done. Only the account in Settings can delete: a post sent
    /// from an account removed since is said to be out of reach.
    /// </summary>
    Task<PostSiteOutcome> DeleteAsync(string did, string recordKey, CancellationToken ct = default);
}
