namespace Modbot.Discord.ModerationLog;

/// <summary>How the channel poster paces itself. Tests shorten these; the defaults suit a deployment.</summary>
public sealed class ModerationLogOptions
{
    /// <summary>How often new facts are looked for while the bot is connected.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to leave a channel after it refused a post before trying it again.</summary>
    public TimeSpan RetryAfterFailure { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Facts read per pass, all types. The cursor moves past the ones not posted.</summary>
    public int FactsPerPass { get; init; } = 200;

    /// <summary>Discord allows ten embeds in one message; a backlog is sent that way.</summary>
    public int EmbedsPerMessage { get; init; } = 10;

    /// <summary>
    /// Messages per pass. With the gap below this is about five seconds of posting, which keeps
    /// one pass shorter than the poll interval and leaves the channel's rate limit alone.
    /// </summary>
    public int MessagesPerPass { get; init; } = 5;

    /// <summary>
    /// Discord allows five messages per five seconds per channel. A second and a bit between
    /// messages stays under that without depending on the library's queue to smooth it.
    /// </summary>
    public TimeSpan GapBetweenMessages { get; init; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// The longest one post of repeats may cover, from the first to the latest. A change that comes
    /// later starts a new post, so the channel still shows roughly when things happened: a group
    /// whose details change every five minutes is one post an hour rather than twelve.
    /// </summary>
    public TimeSpan RepeatWindow { get; init; } = DefaultRepeatWindow;

    /// <summary>One hour (Discord event repeats design §3).</summary>
    public static readonly TimeSpan DefaultRepeatWindow = TimeSpan.FromHours(1);
}
