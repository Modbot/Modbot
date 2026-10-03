namespace Modbot.Api.Features.Posts;

/// <summary>A post's Discord destination, as the composer sends it. Present means Discord is ticked.</summary>
/// <param name="ChannelId">The channel it goes to. One channel per post.</param>
/// <param name="RoleId">One role to mention on the first line and ping on the first send, or null. Never @everyone.</param>
/// <param name="Publish">Publish to the channel's followers once it is in. Announcement channels only.</param>
/// <param name="Text">Discord's own text, or null for the post's.</param>
public sealed record PostDiscordRequest(string? ChannelId, string? RoleId = null, bool Publish = false, string? Text = null);

/// <summary>A post's VRChat destination, as the composer sends it. Present means VRChat is ticked.</summary>
/// <param name="Visibility"><c>group</c> (the group's members, the default) or <c>public</c> (everyone).</param>
/// <param name="RoleIds">With <c>group</c>, only these roles see it; empty means every member. Opaque ids.</param>
/// <param name="Notify">VRChat tells the members when it goes. Unticked to start.</param>
/// <param name="Title">VRChat's own title, or null for the post's. VRChat needs one or the other.</param>
/// <param name="Text">VRChat's own text, or null for the post's.</param>
/// <param name="ImageId">
/// The post's picture as uploaded with <c>POST /api/posts/vrchat-picture</c>, or null for text only.
/// Kept only while VRChat picture uploads are on.
/// </param>
public sealed record PostVRChatRequest(
    string? Visibility = null,
    IReadOnlyList<string>? RoleIds = null,
    bool Notify = false,
    string? Title = null,
    string? Text = null,
    string? ImageId = null);

/// <summary>A new post, or the whole of a post being changed before it goes.</summary>
/// <param name="Title">Shown in bold on Discord's first line. Null or empty for none.</param>
/// <param name="Text">The words.</param>
/// <param name="PictureId">A picture kept with <c>POST /api/posts/picture</c>, or null.</param>
/// <param name="When">
/// <c>now</c> (sent as soon as it is saved) or <c>later</c> (at <paramref name="SendAt"/>). Not used for a draft.
/// </param>
/// <param name="SendAt">The local time it goes, <c>yyyy-MM-ddTHH:mm</c>, in <paramref name="TimeZone"/>. For <c>later</c>.</param>
/// <param name="TimeZone">An IANA zone, such as <c>Europe/London</c>. The browser's own unless picked.</param>
/// <param name="Draft">Saved as a draft: nothing is sent and it has no time.</param>
/// <param name="EventId">A calendar event this post is about, or null.</param>
/// <param name="Discord">Discord's section when Discord is ticked; null when it is not.</param>
/// <param name="VRChat">VRChat's section when VRChat is ticked; null when it is not.</param>
/// <param name="Version">On a change: the version it was read at. A post changed since, or being sent, is refused.</param>
public sealed record PostRequest(
    string? Title,
    string? Text,
    Guid? PictureId = null,
    string? When = null,
    string? SendAt = null,
    string? TimeZone = null,
    bool Draft = false,
    Guid? EventId = null,
    PostDiscordRequest? Discord = null,
    int? Version = null,
    PostVRChatRequest? VRChat = null);

/// <summary>New words for a post already on a site (posts design §4.5).</summary>
/// <param name="Title">The title, or null for none.</param>
/// <param name="Text">The text.</param>
public sealed record PostEditRequest(string? Title, string? Text);

/// <summary>A VRChat destination's own choices.</summary>
/// <param name="Visibility"><c>group</c> or <c>public</c>.</param>
/// <param name="RoleIds">The roles it is for; empty is every member.</param>
/// <param name="RoleNames">Those roles by name, as the last group read found them; an unknown one by its id.</param>
/// <param name="Notify">VRChat tells the members when it goes.</param>
/// <param name="ImageId">The picture's VRChat file id, or null for text only. Once it went out, what VRChat was sent.</param>
/// <param name="PictureId">The post's picture the file id was uploaded from.</param>
public sealed record VRChatDestinationView(
    string Visibility,
    IReadOnlyList<string> RoleIds,
    IReadOnlyList<string> RoleNames,
    bool Notify,
    string? ImageId,
    Guid? PictureId);

/// <summary>One site a post goes to, and how it went there.</summary>
/// <param name="Network"><c>discord</c> or <c>vrchat</c>.</param>
/// <param name="Target">Where on the site: the Discord channel id, or the VRChat group id.</param>
/// <param name="TargetName">The channel's name as Modbot last saw it, or null.</param>
/// <param name="RoleId">The Discord role mentioned, or null.</param>
/// <param name="RoleName">That role's name as Modbot last saw it, or null.</param>
/// <param name="Publish">Publish to followers was ticked.</param>
/// <param name="State">
/// <c>waiting</c>, <c>sending</c>, <c>checking</c>, <c>posted</c>, <c>failed</c>, <c>removed</c> or <c>skipped</c>.
/// </param>
/// <param name="Shown">
/// What the list shows: the state, <c>sending</c> for checking too, or for a waiting one whose site
/// sends nothing now, <c>paused</c>, <c>off</c> or <c>notSetUp</c>.
/// </param>
/// <param name="Link">The post's address on the site, once it is there.</param>
/// <param name="Error">The site's words for what went wrong last, or "Not published" words on a posted one.</param>
/// <param name="NotPublished">Posted, publish to followers was asked for, and it did not go through.</param>
/// <param name="MayBeSent">The site gave no clear answer, so it may have the post: Try again looks first.</param>
/// <param name="TitleOverride">The site's own title, or null for the post's.</param>
/// <param name="TextOverride">The site's own text, or null for the post's.</param>
/// <param name="SentText">Exactly what went out.</param>
/// <param name="VRChat">A VRChat destination's own choices; null for another site.</param>
/// <param name="MissingPermission">The VRChat group permission Modbot's account was refused for lacking, when VRChat said so.</param>
public sealed record PostDestinationView(
    Guid Id,
    string Network,
    string Target,
    string? TargetName,
    string? RoleId,
    string? RoleName,
    bool Publish,
    string State,
    string Shown,
    string? Link,
    string? ExternalId,
    string? Error,
    DateTimeOffset? ErrorAt,
    bool NotPublished,
    bool MayBeSent,
    string? TitleOverride,
    string? TextOverride,
    string? SentText,
    DateTimeOffset? SentAt,
    DateTimeOffset? PostedAt,
    DateTimeOffset? PublishedAt,
    VRChatDestinationView? VRChat = null,
    string? MissingPermission = null);

/// <summary>One post, with where it goes.</summary>
/// <param name="Status"><c>draft</c>, <c>scheduled</c> or <c>cancelled</c>.</param>
/// <param name="Lists">The Marketing lists it appears in: <c>scheduled</c>, <c>sent</c>, <c>drafts</c>, <c>failed</c>, or <c>cancelled</c>.</param>
/// <param name="SendAt">When it goes or went. Null for a draft with no time.</param>
/// <param name="SendAtLocal">The same time in <paramref name="TimeZone"/>, <c>yyyy-MM-ddTHH:mm</c>, for editing.</param>
/// <param name="TimeZone">The zone the time was picked in.</param>
/// <param name="EventTitle">The linked calendar event's title, or null.</param>
/// <param name="Kind">For a post the calendar made: <c>announced</c>, <c>reminder</c>, <c>live</c> or <c>cancelled</c>.</param>
/// <param name="Version">Send it back with a change.</param>
/// <param name="CreatedBy">The username of whoever wrote it, or null.</param>
public sealed record PostView(
    Guid Id,
    string? Title,
    string Text,
    Guid? PictureId,
    string Status,
    IReadOnlyList<string> Lists,
    DateTimeOffset? SendAt,
    string? SendAtLocal,
    string TimeZone,
    Guid? EventId,
    string? EventTitle,
    string? Kind,
    int Version,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CancelledAt,
    IReadOnlyList<PostDestinationView> Destinations);

/// <summary>How many posts each list holds.</summary>
public sealed record PostCounts(int Scheduled, int Sent, int Drafts, int Failed, int Cancelled);

/// <summary>Whether each site can take posts now.</summary>
/// <param name="Paused">Pause all posting is on.</param>
/// <param name="DiscordOn">The Discord posts switch.</param>
/// <param name="DiscordSetUp">A Discord server id is saved and the bot is connected.</param>
/// <param name="VRChatOn">The VRChat posts switch.</param>
/// <param name="VRChatSetUp">A VRChat group is chosen and a VRChat account is saved.</param>
/// <param name="VRChatPictures">VRChat picture uploads are on, so a VRChat post can carry the picture.</param>
public sealed record PostSitesView(bool Paused, bool DiscordOn, bool DiscordSetUp, bool VRChatOn, bool VRChatSetUp, bool VRChatPictures);

/// <summary>A role of the VRChat group, for choosing who a VRChat post is for.</summary>
public sealed record PostRoleChoice(string Id, string Name);

/// <summary>One page of one list on the Marketing tab.</summary>
/// <param name="List">The list asked for.</param>
/// <param name="CanManage">The caller holds Manage posts.</param>
/// <param name="Now">The server's clock.</param>
/// <param name="VRChatRoles">The VRChat group's roles as the last group read found them, in VRChat's order.</param>
public sealed record PostList(
    string List,
    IReadOnlyList<PostView> Posts,
    PostCounts Counts,
    int Page,
    int PageSize,
    int Total,
    bool CanManage,
    PostSitesView Sites,
    DateTimeOffset Now,
    IReadOnlyList<PostRoleChoice> VRChatRoles);

/// <summary>What the Discord message will be, built by the code that sends it.</summary>
/// <param name="Content">The whole message, exactly as it is sent.</param>
/// <param name="Title">The title in bold on the first line after the mention, or null.</param>
/// <param name="Text">The text under it.</param>
/// <param name="RoleName">The role mentioned, or null.</param>
/// <param name="RoleColour">That role's colour, 0xRRGGBB, or 0 for none.</param>
/// <param name="ChannelName">The channel it goes to, or null when it is not known.</param>
/// <param name="PictureUrl">Modbot's own address for the picture, or null.</param>
/// <param name="Length">How long the message is, the way Discord's limit counts it.</param>
/// <param name="Limit">Discord's limit: 2000.</param>
/// <param name="Publish">It will be published to the channel's followers.</param>
public sealed record DiscordPostPreview(
    string Content,
    string? Title,
    string Text,
    string? RoleName,
    int RoleColour,
    string? ChannelName,
    string? PictureUrl,
    int Length,
    int Limit,
    bool Publish);

/// <summary>What the VRChat group post will be, built by the code that sends it.</summary>
/// <param name="Title">The title it goes with, or null when there is none yet.</param>
/// <param name="Text">The text.</param>
/// <param name="Visibility"><c>group</c> or <c>public</c>.</param>
/// <param name="RoleNames">The roles it is for, by name; empty is every member.</param>
/// <param name="Notify">VRChat tells the members.</param>
/// <param name="PictureUrl">Modbot's own address for the picture when VRChat is sent it, or null for text only.</param>
/// <param name="Length">How long the text is. VRChat documents no limit.</param>
public sealed record VRChatPostPreview(
    string? Title,
    string Text,
    string Visibility,
    IReadOnlyList<string> RoleNames,
    bool Notify,
    string? PictureUrl,
    int Length);

/// <summary>What each ticked site would be sent. Null for a site not ticked.</summary>
/// <param name="Problems">What would stop it being scheduled, one sentence each. Empty when none.</param>
public sealed record PostPreview(DiscordPostPreview? Discord, IReadOnlyList<string> Problems, VRChatPostPreview? VRChat = null);

/// <summary>A picture kept for a post.</summary>
public sealed record PostPictureView(Guid PictureId);

/// <summary>A post's picture to upload to VRChat.</summary>
/// <param name="PictureId">The picture kept with <c>POST /api/posts/picture</c>.</param>
public sealed record PostVRChatPictureRequest(Guid? PictureId);

/// <summary>A post's picture as VRChat has it.</summary>
/// <param name="ImageId">VRChat's file id: send it as the VRChat section's <c>imageId</c>.</param>
/// <param name="PictureId">The picture it was uploaded from.</param>
public sealed record PostVRChatPictureView(string ImageId, Guid PictureId);

/// <summary>A link to a picture somewhere else, to fetch for the crop box.</summary>
public sealed record PostPictureLinkRequest(string? Url);

/// <summary>The posts switches in Settings (posts design §4.6).</summary>
/// <param name="Paused">Pause all posting.</param>
/// <param name="Discord">Discord posts.</param>
/// <param name="VRChat">VRChat posts.</param>
public sealed record PostSettingsView(bool Paused, bool Discord, bool VRChat);

/// <summary>A change to the posts switches. A switch left null stays as it is.</summary>
public sealed record PostSettingsRequest(bool? Paused = null, bool? Discord = null, bool? VRChat = null);

/// <summary>One destination worth a look on Health.</summary>
/// <param name="Problem"><c>failed</c>, or <c>checking</c> for one still looked for after 15 minutes.</param>
/// <param name="MissingPermission">The VRChat group permission Modbot's account lacks, when VRChat refused for it.</param>
public sealed record PostHealthProblem(
    Guid PostId, string Title, string Network, string Problem, string? Error, DateTimeOffset? At, string? MissingPermission = null);

/// <summary>A site posts are waiting for that sends nothing now.</summary>
/// <param name="Hold"><c>off</c> or <c>notSetUp</c>.</param>
/// <param name="Waiting">How many scheduled destinations wait on it.</param>
public sealed record PostHealthHold(string Network, string Hold, int Waiting);

/// <summary>The Posts card on Health (posts design §5).</summary>
/// <param name="Paused">Pause all posting is on.</param>
/// <param name="Problems">Failures in the last 7 days, and posts looked for longer than 15 minutes, newest first.</param>
/// <param name="Holds">Sites scheduled posts wait on that are switched off or not set up.</param>
public sealed record PostsHealth(bool Paused, IReadOnlyList<PostHealthProblem> Problems, IReadOnlyList<PostHealthHold> Holds);
