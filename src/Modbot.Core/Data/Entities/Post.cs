namespace Modbot.Core.Data.Entities;

/// <summary>Where a <see cref="Post"/> is in its life (posts design §2.2).</summary>
/// <remarks>
/// Sent, Failed and Sending are not here: they are worked out from the destinations (§2.3), so a
/// post that went to one site and failed on another can be both.
/// </remarks>
public static class PostStatuses
{
    /// <summary>Saved, with no time and nothing waiting to go.</summary>
    public const string Draft = "draft";

    /// <summary>Has a time. Its destinations go out at that time, or have already.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Called off before it went. Destinations that had not gone are skipped.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>The sites a post can go to (posts design §3). Each is a <see cref="PostDestination.Network"/>.</summary>
/// <remarks>
/// Discord came first and VRChat second; Bluesky follows as its own step, and others (Mastodon, a
/// web address) later. Each is a new value, a sender and a composer section.
/// </remarks>
public static class PostNetworks
{
    public const string Discord = "discord";
    public const string VRChat = "vrchat";
    public const string Bluesky = "bluesky";

    public static readonly IReadOnlyList<string> All = [Discord, VRChat, Bluesky];
}

/// <summary>Where one destination of a post is (posts design §2.2).</summary>
public static class PostDestinationStates
{
    /// <summary>Not sent yet: before its time, paused, switched off or not set up.</summary>
    public const string Waiting = "waiting";

    /// <summary>Claimed and on its way: <see cref="PostDestination.SentAt"/> was written before the call.</summary>
    public const string Sending = "sending";

    /// <summary>The site gave no clear answer. Modbot looks for the post before anything else happens.</summary>
    public const string Checking = "checking";

    /// <summary>On the site, with its id.</summary>
    public const string Posted = "posted";

    /// <summary>Not on the site, with the reason. Nothing sends it again until a person says so.</summary>
    public const string Failed = "failed";

    /// <summary>Was on the site and has been deleted there.</summary>
    public const string Removed = "removed";

    /// <summary>Never went: the post was cancelled first, or the site was unticked.</summary>
    public const string Skipped = "skipped";

    /// <summary>Finished, one way or another: nothing more happens to it by itself.</summary>
    public static bool IsDone(string state) => state is Posted or Removed or Skipped or Failed;

    /// <summary>Somewhere between being claimed and knowing how it went.</summary>
    public static bool IsUnderWay(string state) => state is Sending or Checking;
}

/// <summary>The posts the calendar makes for an event (posts design §2.4). Null on a post a person wrote.</summary>
/// <remarks>Not made by anything yet: the calendar moves onto posts in a later step.</remarks>
public static class PostKinds
{
    public const string Announced = "announced";
    public const string Reminder = "reminder";
    public const string Live = "live";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// One post Modbot sends to one or more sites at a time: written on the Marketing tab, or made by
/// the calendar for an event. The table is <c>post</c> (posts design §2.2).
/// </summary>
/// <remarks>
/// <para>
/// The words live here once. Each site the post goes to is a <see cref="PostDestination"/> with its
/// own state, its own id on that site, and the exact text it sent, so a post can be Posted on one
/// site and Failed on another.
/// </para>
/// <para>
/// <strong><see cref="Version"/> is the concurrency token.</strong> Every write to a post or one of
/// its destinations raises it, the sender's claim included, so an edit saved while the post is
/// being sent fails its own check rather than changing words that are already on their way.
/// </para>
/// </remarks>
public class Post
{
    /// <summary>The longest title kept. VRChat needs one; Discord shows it in bold above the text.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>The longest text kept: room for every site's own limit and some.</summary>
    public const int MaxTextLength = 8000;

    public Guid Id { get; set; }

    /// <summary>Shown in bold on Discord's first line. Null for none.</summary>
    public string? Title { get; set; }

    /// <summary>The words, before any destination's own text.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>A picture kept in <c>calendar_cover_picture</c>, which holds every picture Modbot keeps for posting.</summary>
    public Guid? PictureId { get; set; }

    /// <summary>One of <see cref="PostStatuses"/>.</summary>
    public string Status { get; set; } = PostStatuses.Draft;

    /// <summary>When it goes. "Now" is saved as the time it was saved. Null only for a draft.</summary>
    public DateTimeOffset? SendAt { get; set; }

    /// <summary>The zone the time was picked in, used to show it and to edit it.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>The calendar event this post is about, for event posts and a post linked to an event.</summary>
    public Guid? EventId { get; set; }

    /// <summary>One of <see cref="PostKinds"/> for a post the calendar made; null for one a person wrote.</summary>
    public string? Kind { get; set; }

    /// <summary>The planned start of the date a calendar post is about, for its reminder, live and one-date cancel posts.</summary>
    public DateTimeOffset? DateStartsAt { get; set; }

    /// <summary>Raised on every write; the concurrency token.</summary>
    public int Version { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public List<PostDestination> Destinations { get; set; } = [];
}

/// <summary>
/// One site a <see cref="Post"/> goes to, and how it went there. The table is
/// <c>post_destination</c>, one row per site per post (posts design §2.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>At most once.</strong> <see cref="SentAt"/> and <see cref="SentText"/> are written, with
/// <see cref="PostDestinationStates.Sending"/>, before the site is asked. An answer that is not
/// clear leaves the row <see cref="PostDestinationStates.Checking"/> and Modbot looks on the site
/// for what it sent; it never sends again by itself (posts design §3.5).
/// </para>
/// </remarks>
public class PostDestination
{
    public Guid Id { get; set; }

    public Guid PostId { get; set; }

    /// <summary>One of <see cref="PostNetworks"/>.</summary>
    public string Network { get; set; } = PostNetworks.Discord;

    /// <summary>
    /// Where on the site: the Discord channel id, the VRChat group id, or the Bluesky account's DID.
    /// Kept so a later edit or delete goes to the same place after the settings change. Opaque,
    /// never checked for shape.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// The site's own choices, as JSON: <see cref="DiscordPostOptions"/>, <see cref="VRChatPostOptions"/>
    /// or <see cref="BlueskyPostOptions"/>.
    /// </summary>
    public string Options { get; set; } = "{}";

    /// <summary>
    /// The site's own copy of the post's picture, kept in <c>calendar_cover_picture</c> beside it:
    /// Bluesky's card picture, a small JPEG the browser made (posts design §4.2c). Null for none.
    /// A real column rather than a word in <see cref="Options"/>, so the hourly picture sweep sees
    /// that a post still uses it.
    /// </summary>
    public Guid? SitePictureId { get; set; }

    /// <summary>This site's own title, or null for the post's.</summary>
    public string? TitleOverride { get; set; }

    /// <summary>This site's own text, or null for the post's.</summary>
    public string? TextOverride { get; set; }

    /// <summary>One of <see cref="PostDestinationStates"/>.</summary>
    public string State { get; set; } = PostDestinationStates.Waiting;

    /// <summary>An id Modbot makes once and the site keeps (Bluesky's record key; later, Discord's nonce).</summary>
    public string? ClientKey { get; set; }

    /// <summary>
    /// The post's id on the site: the Discord message id, VRChat's <c>not_…</c> id, or Bluesky's
    /// <c>at://</c> address. Opaque.
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>The post's https address on the site.</summary>
    public string? Link { get; set; }

    /// <summary>The title as it went out, for the look and the audit log.</summary>
    public string? SentTitle { get; set; }

    /// <summary>Exactly what went out: for Discord, the whole message text the look compares.</summary>
    public string? SentText { get; set; }

    /// <summary>The last attempt, written before sending.</summary>
    public DateTimeOffset? SentAt { get; set; }

    public DateTimeOffset? PostedAt { get; set; }

    /// <summary>When a Discord post was published to the channel's followers.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>The site's own words for the last thing that went wrong.</summary>
    public string? Error { get; set; }

    public DateTimeOffset? ErrorAt { get; set; }

    /// <summary>The permission the site said Modbot lacks, when it said so.</summary>
    public string? MissingPermission { get; set; }

    /// <summary>The destination this one answers, for a Bluesky reply under an earlier post.</summary>
    public Guid? ReplyToId { get; set; }

    /// <summary>When Modbot deletes it by itself, for the calendar's cancel line.</summary>
    public DateTimeOffset? RemoveAt { get; set; }

    /// <summary>
    /// When Modbot next looks on the site for a post that got no clear answer; or, for a waiting one
    /// the site turned away without making anything, when it may be tried again.
    /// </summary>
    public DateTimeOffset? CheckAt { get; set; }

    /// <summary>
    /// The last attempt got no clear answer, so the post may be on the site. Sending again then
    /// waits for a look, and the whole post cannot be edited until that is settled.
    /// </summary>
    public bool MayBeSent { get; set; }

    /// <summary>A person pressed Try again: when the look finds nothing, send.</summary>
    public bool SendIfMissing { get; set; }

    /// <summary>
    /// For a waiting row, when it started waiting (the late rule counts from it); for a checking
    /// row, when the looking began (Modbot gives up looking an hour after).
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A Discord destination's own choices (posts design §3.5), stored as JSON in <see cref="PostDestination.Options"/>.</summary>
/// <param name="RoleId">One role mentioned on the first line and pinged on the first send. Never @everyone.</param>
/// <param name="Publish">Publish to the channel's followers after it is in. Announcement channels only.</param>
public sealed record DiscordPostOptions(string? RoleId = null, bool Publish = false);

/// <summary>Who sees a VRChat group post (posts design §3.6), as <see cref="VRChatPostOptions.Visibility"/> keeps it.</summary>
public static class VRChatPostVisibilities
{
    /// <summary>The group's members, or only the roles picked. VRChat's <c>group</c>.</summary>
    public const string Group = "group";

    /// <summary>Everyone, on vrchat.com. VRChat's <c>public</c>.</summary>
    public const string Everyone = "public";

    public static bool IsKnown(string? value) => value is Group or Everyone;
}

/// <summary>A VRChat destination's own choices (posts design §3.6), stored as JSON in <see cref="PostDestination.Options"/>.</summary>
/// <param name="Visibility"><see cref="VRChatPostVisibilities.Group"/> (the default) or <see cref="VRChatPostVisibilities.Everyone"/>.</param>
/// <param name="RoleIds">Only these roles see it; empty is every member. Only with Group. Opaque ids.</param>
/// <param name="Notify">VRChat tells the members, on the first send only. Unticked to start.</param>
/// <param name="ImageId">
/// The picture's VRChat file id, uploaded while VRChat picture uploads are on (decision 14). Once
/// the post went out, the id VRChat was sent, or null when it went as text only: an edit sends it
/// again, because VRChat removes a picture an edit leaves out.
/// </param>
/// <param name="PictureId">The post's picture <see cref="ImageId"/> was uploaded from, so a picture changed since is not sent.</param>
public sealed record VRChatPostOptions(
    string Visibility = VRChatPostVisibilities.Group,
    IReadOnlyList<string>? RoleIds = null,
    bool Notify = false,
    string? ImageId = null,
    Guid? PictureId = null);

/// <summary>A Bluesky destination's own choices (posts design §4.2c), stored as JSON in <see cref="PostDestination.Options"/>.</summary>
/// <param name="PictureId">
/// The post's picture the card picture (<see cref="PostDestination.SitePictureId"/>) was made from,
/// so a picture changed since is not sent.
/// </param>
/// <param name="Cid">The post's content id on Bluesky, once it went out, as Bluesky answered or as read back.</param>
/// <param name="KeyTaken">
/// Bluesky answered a put with <c>InvalidSwap</c> (something is at the record key) and a read-back
/// then found nothing. Bluesky contradicted itself, so the post may be there: it is never sent again,
/// by the loop or by Try again, only read back and taken if found.
/// </param>
public sealed record BlueskyPostOptions(Guid? PictureId = null, string? Cid = null, bool KeyTaken = false);
