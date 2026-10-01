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

/// <summary>One of the group's roles, as VRChat has it.</summary>
/// <param name="Id">VRChat's id for the role. Opaque.</param>
/// <param name="Permissions">
/// VRChat's ids for what the role may do (<c>group-bans-manage</c>, or <c>*</c> for every permission),
/// as VRChat wrote them, including any this build has no label for.
/// </param>
/// <param name="Order">Where VRChat lists the role. Lower is higher up.</param>
/// <param name="IsDefault">The role every member has. VRChat does not let it be deleted.</param>
/// <param name="IsManagementRole">Whether VRChat counts the role as one that manages the group.</param>
/// <param name="RequiresTwoFactor">Whether holding the role needs two-factor sign-in on VRChat.</param>
/// <param name="HeldByModbot">Whether Modbot's own VRChat account has the role, as last read.</param>
public sealed record GroupRoleRow(
    string Id,
    string? Name,
    string? Description,
    IReadOnlyList<string> Permissions,
    int Order,
    bool IsDefault,
    bool IsManagementRole,
    bool IsSelfAssignable,
    bool RequiresTwoFactor,
    bool HeldByModbot);

/// <summary>The group's roles, read from VRChat when asked for, in VRChat's order.</summary>
public sealed record GroupRoleList(IReadOnlyList<GroupRoleRow> Roles, DateTimeOffset ReadAt);

/// <summary>A new role, or a change to one.</summary>
/// <param name="Id">The role being changed. Null for a new one.</param>
/// <param name="Name">The role's name. Required for a new role; null to leave it on a change.</param>
/// <param name="Description">The role's description. Null to leave it; an empty string clears it.</param>
/// <param name="Permissions">
/// Every permission the role should have, as VRChat's ids. The whole list: VRChat replaces it.
/// Null to leave them on a change.
/// </param>
public sealed record GroupRoleBody(
    string? Id,
    string? Name,
    string? Description,
    IReadOnlyList<string>? Permissions);

/// <summary>A role to delete.</summary>
/// <param name="Name">The role's name as the page showed it, kept in the audit log with the deletion.</param>
public sealed record GroupRoleDelete(string Id, string? Name = null);

/// <summary>A role VRChat accepted, as it now stands.</summary>
public sealed record GroupRoleSaved(GroupRoleRow Role);

/// <summary>Somebody the group has invited who has not answered yet.</summary>
/// <param name="UserId">Their VRChat id. Opaque.</param>
/// <param name="DisplayName">Their name, from VRChat's answer or else from what Modbot already knows.</param>
/// <param name="InvitedAt">When VRChat says the invite was made, when it says.</param>
public sealed record GroupInviteRow(string UserId, string? DisplayName, DateTimeOffset? InvitedAt);

/// <summary>A page of the invites the group has sent, read from VRChat when asked for.</summary>
/// <param name="HasMore">True when the page came back full, because VRChat sends no total.</param>
public sealed record GroupInviteList(
    IReadOnlyList<GroupInviteRow> Invites,
    int Page,
    int PageSize,
    bool HasMore,
    DateTimeOffset ReadAt);

/// <summary>An invite to cancel.</summary>
/// <param name="UserId">Who was invited.</param>
/// <param name="DisplayName">Their name as the page showed it, kept in the audit log.</param>
public sealed record GroupInviteCancel(string UserId, string? DisplayName = null);

/// <summary>An invite to send.</summary>
/// <param name="UserId">Who to invite: their VRChat id, taken as sent (foundation §3.1.1).</param>
/// <param name="DisplayName">Their name, when the caller knows it, kept in the audit log.</param>
public sealed record GroupInviteSend(string UserId, string? DisplayName = null);

/// <summary>One of the group's galleries, as the group poll last found it.</summary>
public sealed record GroupGalleryChoice(string Id, string? Name, string? Description, bool MembersOnly);

/// <summary>One image in a gallery.</summary>
/// <param name="Id">VRChat's id for the gallery entry. Opaque.</param>
/// <param name="Approved">False while it waits for somebody to approve it.</param>
/// <param name="SubmittedByName">From the profiles Modbot has already read; null otherwise.</param>
public sealed record GroupGalleryImageRow(
    string Id,
    string? ImageUrl,
    bool Approved,
    string? SubmittedById,
    string? SubmittedByName,
    DateTimeOffset? CreatedAt);

/// <summary>
/// The group's galleries, and one page of the chosen one's images, read from VRChat when asked for.
/// </summary>
/// <param name="Galleries">Every gallery, from the group poll. No request is made for these.</param>
/// <param name="GalleryId">The gallery whose images these are; null when the group has none.</param>
/// <param name="HasMore">True when the page came back full, because VRChat sends no total.</param>
public sealed record GroupGalleryPage(
    IReadOnlyList<GroupGalleryChoice> Galleries,
    string? GalleryId,
    IReadOnlyList<GroupGalleryImageRow> Images,
    int Page,
    int PageSize,
    bool HasMore,
    DateTimeOffset ReadAt);

/// <summary>An image to remove from a gallery.</summary>
/// <param name="SubmittedById">Who submitted it, as the page showed it, kept in the audit log.</param>
public sealed record GroupGalleryImageRemove(string GalleryId, string ImageId, string? SubmittedById = null);
