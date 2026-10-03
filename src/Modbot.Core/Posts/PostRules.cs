using Modbot.Core.Data.Entities;

namespace Modbot.Core.Posts;

/// <summary>Why a site sends nothing right now, or null when it may send (posts design §3.4).</summary>
public static class PostHolds
{
    /// <summary>Pause all posting is on.</summary>
    public const string Paused = "paused";

    /// <summary>The site's own switch is off.</summary>
    public const string Off = "off";

    /// <summary>The site is not set up: for Discord, no server id or no connected bot.</summary>
    public const string NotSetUp = "notSetUp";
}

/// <summary>How each site stands for posting, read once per pass or per request.</summary>
/// <param name="Paused">Pause all posting.</param>
/// <param name="DiscordOn">The Discord posts switch.</param>
/// <param name="DiscordSetUp">A server id is saved and the bot is connected.</param>
public sealed record PostSites(bool Paused, bool DiscordOn, bool DiscordSetUp)
{
    /// <summary>Why nothing goes to <paramref name="network"/> right now, or null when it may.</summary>
    /// <remarks>Sites not built yet are never set up, so nothing is ever sent there by mistake.</remarks>
    public string? HoldFor(string network)
    {
        if (Paused)
            return PostHolds.Paused;

        return network switch
        {
            PostNetworks.Discord when !DiscordOn => PostHolds.Off,
            PostNetworks.Discord when !DiscordSetUp => PostHolds.NotSetUp,
            PostNetworks.Discord => null,
            _ => PostHolds.NotSetUp,
        };
    }
}

/// <summary>The four lists of the Marketing tab, and the Cancelled view (posts design §2.3).</summary>
public static class PostLists
{
    public const string Scheduled = "scheduled";
    public const string Sent = "sent";
    public const string Drafts = "drafts";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Scheduled, Sent, Drafts, Failed, Cancelled];
}

/// <summary>
/// The shared rules every site's sender follows (posts design §3.4): what is due, what holds it,
/// how late is too late, and how many an hour.
/// </summary>
/// <remarks>
/// Pure, so the senders, the API and the tests ask the same questions and get the same answers.
/// The clock is passed in: nothing here reads it.
/// </remarks>
public static class PostRules
{
    /// <summary>A post due longer ago than this is not sent: it turns Failed, with Post now (decision 5).</summary>
    public static readonly TimeSpan LateLimit = TimeSpan.FromHours(1);

    /// <summary>The most posts one site sends in an hour, to stop a runaway. A real group posts far less.</summary>
    public const int PerSitePerHour = 10;

    /// <summary>How long after an unclear answer Modbot first looks for the post on the site.</summary>
    public static readonly TimeSpan FirstLookAfter = TimeSpan.FromMinutes(1);

    /// <summary>How long to wait before looking again when the look could not be made.</summary>
    public static readonly TimeSpan LookAgainAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long after the looking began (the unclear answer, or a person's Try again) Modbot gives up
    /// looking and calls it Failed, still as one the site may have.
    /// </summary>
    public static readonly TimeSpan StopLookingAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// A destination left Sending longer than this was cut off mid-send (the process stopped): it
    /// is treated as an answer that never came, and looked for.
    /// </summary>
    public static readonly TimeSpan StuckSendingAfter = TimeSpan.FromMinutes(2);

    /// <summary>A destination still looking longer than this is shown on Health.</summary>
    public static readonly TimeSpan LongCheckAfter = TimeSpan.FromMinutes(15);

    public const string NotSentOnTime = "Not sent on time.";
    public const string BeingSent = "This post is being sent.";

    /// <summary>
    /// When a waiting destination became due: the post's time, or later when a person sent it on its
    /// way again (Try again, Post now, an edit), which writes its <see cref="PostDestination.UpdatedAt"/>.
    /// </summary>
    public static DateTimeOffset DueSince(Post post, PostDestination destination)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(destination);

        var sendAt = post.SendAt ?? DateTimeOffset.MaxValue;
        return destination.UpdatedAt > sendAt ? destination.UpdatedAt : sendAt;
    }

    /// <summary>A scheduled post's waiting destination whose time has come.</summary>
    public static bool IsDue(Post post, PostDestination destination, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(destination);

        return post.Status == PostStatuses.Scheduled
            && post.SendAt is { } at
            && at <= now
            && destination.State == PostDestinationStates.Waiting;
    }

    /// <summary>Due, and more than <see cref="LateLimit"/> ago: it is not sent, and turns Failed.</summary>
    public static bool IsLate(Post post, PostDestination destination, DateTimeOffset now) =>
        IsDue(post, destination, now) && now - DueSince(post, destination) > LateLimit;

    /// <summary>Whether one more post may go to a site that sent <paramref name="sentInLastHour"/> in the last hour.</summary>
    public static bool UnderHourlyCap(int sentInLastHour) => sentInLastHour < PerSitePerHour;

    /// <summary>
    /// What the list shows for one destination: its state, or for a waiting one whose site sends
    /// nothing right now, why (<see cref="PostHolds"/>). <c>checking</c> is shown as sending.
    /// </summary>
    public static string Shown(PostDestination destination, PostSites sites)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(sites);

        if (destination.State == PostDestinationStates.Waiting && sites.HoldFor(destination.Network) is { } hold)
            return hold;

        return destination.State == PostDestinationStates.Checking ? PostDestinationStates.Sending : destination.State;
    }

    /// <summary>
    /// The lists a post appears in (§2.3). Failed is not exclusive: a post that went to one site and
    /// failed on another is in Failed and in whatever else it is.
    /// </summary>
    public static IReadOnlyList<string> ListsOf(Post post)
    {
        ArgumentNullException.ThrowIfNull(post);

        if (post.Status == PostStatuses.Draft)
            return [PostLists.Drafts];

        if (post.Status == PostStatuses.Cancelled)
            return [PostLists.Cancelled];

        var lists = new List<string>(2);
        var states = post.Destinations.Select(d => d.State).ToList();

        if (states.Any(s => s is PostDestinationStates.Waiting or PostDestinationStates.Sending or PostDestinationStates.Checking))
            lists.Add(PostLists.Scheduled);

        if (states.Any(s => s == PostDestinationStates.Failed))
            lists.Add(PostLists.Failed);

        if (states.Count > 0
            && states.All(s => s is PostDestinationStates.Posted or PostDestinationStates.Removed or PostDestinationStates.Skipped)
            && states.Any(s => s is PostDestinationStates.Posted or PostDestinationStates.Removed))
        {
            lists.Add(PostLists.Sent);
        }

        return lists;
    }

    /// <summary>
    /// Why the whole post cannot be changed now, or null when it can (§3.4, §4.5). A post on its way
    /// is not edited under the sender; one that has gone out is edited on each site instead.
    /// </summary>
    public static string? CannotEdit(Post post)
    {
        ArgumentNullException.ThrowIfNull(post);

        if (post.Status == PostStatuses.Cancelled)
            return "This post was cancelled.";

        if (post.Destinations.Any(d => PostDestinationStates.IsUnderWay(d.State)))
            return BeingSent;

        if (post.Destinations.Any(d => d.State is PostDestinationStates.Posted or PostDestinationStates.Removed))
            return "This post has gone out. Edit it on each site.";

        if (post.Destinations.Any(d => d.State == PostDestinationStates.Failed && d.MayBeSent))
            return "The site may already have this post. Press Try again first.";

        return null;
    }

    /// <summary>Why the post cannot be cancelled now, or null when it can.</summary>
    public static string? CannotCancel(Post post)
    {
        ArgumentNullException.ThrowIfNull(post);

        if (post.Status == PostStatuses.Cancelled)
            return "This post was cancelled.";

        if (post.Status == PostStatuses.Draft)
            return "A draft is deleted, not cancelled.";

        if (post.Destinations.Any(d => PostDestinationStates.IsUnderWay(d.State)))
            return BeingSent;

        if (!post.Destinations.Any(d => d.State == PostDestinationStates.Waiting
                || (d.State == PostDestinationStates.Failed && !d.MayBeSent)))
        {
            return "Nothing is left to cancel.";
        }

        return null;
    }

    /// <summary>
    /// How long after the attempt a message the attempt made can carry as its time. The look has
    /// read the whole window only once it has seen a message later than this, or the channel's end:
    /// until then, "not found" is not an answer, and nothing that would let it be sent again is done.
    /// </summary>
    public static readonly TimeSpan LookWindowAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A waiting destination the site turned away without making anything (a rate limit, a channel
    /// that could not be looked up) is tried again after this, so it does not hold up the posts
    /// behind it every pass.
    /// </summary>
    public static readonly TimeSpan NotSentRetryAfter = TimeSpan.FromMinutes(2);

    private const long DiscordEpoch = 1420070400000L;

    /// <summary>
    /// The smallest Discord message id that could have been made at <paramref name="at"/>: the
    /// look reads the channel after this (Discord's ids carry their time).
    /// </summary>
    public static string DiscordIdAt(DateTimeOffset at)
    {
        var ms = Math.Max(0, at.ToUnixTimeMilliseconds() - DiscordEpoch);
        return ((ulong)ms << 22).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>When a Discord message id was made, or null for one that is not a Discord id.</summary>
    public static DateTimeOffset? DiscordTimeOf(string? id) =>
        ulong.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(value >> 22) + DiscordEpoch)
            : null;
}
