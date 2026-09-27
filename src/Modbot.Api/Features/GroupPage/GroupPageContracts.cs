namespace Modbot.Api.Features.GroupPage;

/// <summary>
/// A change to the group's profile. A field left null is not being changed.
/// </summary>
/// <param name="Name">The group's name.</param>
/// <param name="Description">What the group is about. An empty string clears it.</param>
/// <param name="Rules">The group's rules. An empty string clears them.</param>
/// <param name="Languages">VRChat's three-letter language codes, such as <c>eng</c>. At most three.</param>
/// <param name="Links">Web addresses, each <c>http</c> or <c>https</c>. At most three.</param>
/// <param name="JoinState">
/// Who can join: <c>open</c> (anyone), <c>request</c> (anyone can ask), <c>invite</c> (invite only)
/// or <c>closed</c> (nobody).
/// </param>
public sealed record GroupProfileEdit(
    string? Name = null,
    string? Description = null,
    string? Rules = null,
    IReadOnlyList<string>? Languages = null,
    IReadOnlyList<string>? Links = null,
    string? JoinState = null);

/// <summary>One of the group's roles, offered when choosing who a post is for.</summary>
public sealed record GroupRoleChoice(string Id, string Name);

/// <summary>One post, as VRChat has it, with its author's name from what Modbot already knows.</summary>
/// <param name="Id">VRChat's id for the post. Opaque.</param>
/// <param name="AuthorName">The author's display name when Modbot has read their profile; null otherwise.</param>
/// <param name="ImageUrl">The post's picture, when it has one.</param>
/// <param name="ImageId">The picture's VRChat file id. Sent back with an edit so the picture stays.</param>
/// <param name="Visibility"><c>group</c> (members only) or <c>public</c>.</param>
/// <param name="RoleIds">The roles the post is for. Empty means every member.</param>
public sealed record GroupPostRow(
    string Id,
    string? Title,
    string? Text,
    string? AuthorId,
    string? AuthorName,
    string? ImageUrl,
    string? ImageId,
    string Visibility,
    IReadOnlyList<string> RoleIds,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>A page of the group's posts, read from VRChat when asked for.</summary>
/// <param name="Total">How many posts VRChat says the group has.</param>
/// <param name="Roles">The group's roles as the last group read found them, for choosing who a post is for.</param>
/// <param name="ReadAt">When VRChat was asked.</param>
public sealed record GroupPostList(
    IReadOnlyList<GroupPostRow> Posts,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<GroupRoleChoice> Roles,
    DateTimeOffset ReadAt);

/// <summary>A new post, or the whole of a post being changed.</summary>
/// <param name="Id">The post being changed. Null for a new one.</param>
/// <param name="Visibility"><c>group</c> (members only) or <c>public</c>.</param>
/// <param name="RoleIds">Only these roles see it. Empty or null means every member.</param>
/// <param name="Notify">Whether VRChat tells the group's members. Only for a new post.</param>
/// <param name="ImageId">
/// The picture's VRChat file id. Only for keeping the picture an existing post already has:
/// VRChat replaces the whole post on an edit, so a picture not sent again is removed.
/// </param>
public sealed record GroupPostBody(
    string? Id,
    string? Title,
    string? Text,
    string? Visibility,
    IReadOnlyList<string>? RoleIds,
    bool Notify = false,
    string? ImageId = null);

/// <summary>A post to delete.</summary>
/// <param name="Title">The post's title as the page showed it, kept in the audit log with the deletion.</param>
public sealed record GroupPostDelete(string Id, string? Title = null);

/// <summary>A post VRChat accepted, as it now stands.</summary>
public sealed record GroupPostSaved(GroupPostRow Post);
