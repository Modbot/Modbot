using Modbot.Core.Discord;

namespace Modbot.Core.Posts;

/// <summary>What <c>/post new</c> asks to send: the words from the form, and the channel picked.</summary>
public sealed record PostDraft(string? Title, string Text, string ChannelId);

/// <summary>
/// What a post would say, found out before anyone is asked to confirm it.
/// </summary>
/// <param name="Problems">Everything wrong, one sentence each. Empty means it may be sent.</param>
/// <param name="Content">
/// The Discord message exactly as the sender builds it (<see cref="PostTexts.Discord(string?, string, string?)"/>),
/// so the preview is what goes out. Null when the words are not fit to show.
/// </param>
/// <param name="ChannelName">The channel's name, without the #, when Modbot has it.</param>
public sealed record PostPreviewAnswer(IReadOnlyList<string> Problems, string? Content, string? ChannelName)
{
    public bool Ready => Problems.Count == 0 && Content is not null;
}

/// <summary>How <c>Post now</c> went.</summary>
/// <param name="Created">The post is saved and waiting for the Discord sender to take it.</param>
/// <param name="Repeat">This confirmation had saved it already; nothing new was saved.</param>
/// <param name="Problems">Why it was not saved, one sentence each.</param>
public sealed record PostNowAnswer(bool Created, bool Repeat, IReadOnlyList<string> Problems, Guid? PostId = null);

/// <summary>One post in <c>/post list</c>.</summary>
/// <param name="Where">The sites it goes to, as words: "Discord #announcements, VRChat".</param>
/// <param name="Error">For a failed post: what the site said.</param>
public sealed record PostLine(Guid Id, string Headline, DateTimeOffset? SendAt, string Where, string? Error);

/// <summary>The posts <c>/post list</c> shows.</summary>
/// <param name="Waiting">The next posts waiting to go out, soonest first.</param>
/// <param name="WaitingMore">How many more are waiting than are listed.</param>
/// <param name="Failed">Posts that failed on a site, newest change first.</param>
/// <param name="FailedMore">How many more failed than are listed.</param>
public sealed record PostListAnswer(
    IReadOnlyList<PostLine> Waiting, int WaitingMore, IReadOnlyList<PostLine> Failed, int FailedMore);

/// <summary>
/// What a staff member may do with posts from Discord, through the same code the Marketing tab
/// uses (Discord commands design §3.7 and §4, step 8).
/// </summary>
/// <remarks>
/// <para>
/// In Core so the bot can ask without depending on the API, whose post checks and rows do the work.
/// Only the Discord site is offered here.
/// </para>
/// <para>
/// <strong>Nothing here sends.</strong> <see cref="PostNowAsync"/> saves a post that is due now, as
/// the web app's Post now does, and the Discord sender's loop sends it: claimed first, one try, never
/// sent again by itself. A command that sent straight to Discord would be a second way to send, with
/// none of that.
/// </para>
/// <para>
/// The caller has already resolved who is acting and checked their permission. The implementation
/// checks it again: Manage posts to preview or save, See posts to list.
/// </para>
/// </remarks>
public interface IPostActions
{
    /// <summary>Checks a post exactly as saving it would, and says what it would be sent as.</summary>
    Task<PostPreviewAnswer> PreviewAsync(PostDraft draft, StaffMember by, CancellationToken ct = default);

    /// <summary>
    /// Saves a post that goes to Discord now. <paramref name="key"/> makes one confirmation save one
    /// post: the same key again answers <see cref="PostNowAnswer.Repeat"/> and saves nothing.
    /// </summary>
    Task<PostNowAnswer> PostNowAsync(string key, PostDraft draft, StaffMember by, CancellationToken ct = default);

    /// <summary>The next waiting posts and the failed ones, with their times.</summary>
    Task<PostListAnswer> ListAsync(int most, StaffMember by, CancellationToken ct = default);
}
