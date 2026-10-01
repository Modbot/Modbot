using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Conventions;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat.GroupPage;
using Modbot.VRChat.Sync;
using VRChat.API.Model;

namespace Modbot.Api.Features.GroupPage;

/// <summary>
/// The group's posts on VRChat, from the VRChat page's Posts tab: the list, a new post, a change to
/// one, and deleting one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read from VRChat when asked, never stored and never polled.</strong> One request when
/// the tab opens, one per page, one per Refresh. The author's name and the roles offered come from
/// what Modbot already has, so filling the page in costs nothing more.
/// </para>
/// <para>
/// <strong>A write is one request, and nothing is recorded unless VRChat accepted it.</strong> Then
/// one fact names who posted, changed or deleted, beside VRChat's own audit entry, which can only
/// name Modbot's account. The post's id travels in the body, as a join request's does, or in the
/// address on <c>PUT</c> and <c>DELETE /{id}</c> (API conventions design §4); the route has no
/// constraint, so its format is checked in neither place (foundation §3.1.1).
/// </para>
/// </remarks>
public static class GroupPostEndpoints
{
    public static IEndpointRouteBuilder MapGroupPosts(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/group/posts").WithTags("Group page").RequireAuthorization();

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupPosts? vrchat,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                CancellationToken ct) =>
            {
                if (vrchat is null)
                    return GroupPageAnswers.NotSetUp();

                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
                    return GroupPageAnswers.NoGroup();

                var number = Math.Max(1, page ?? 1);
                var size = Math.Clamp(pageSize ?? GroupPageRules.PostPageSize, 1, GroupPosts.MaxPageSize);

                var answer = await vrchat.ListAsync(groupId, size, (number - 1) * size, ct);

                // Never an empty list in place of one Modbot could not read: the two look the same
                // on a screen, and only one of them means the group has no posts.
                if (!answer.Success)
                    return GroupPageAnswers.Refused(answer, "GetGroupPosts", groupId, settings);

                var posts = answer.Value?.Posts ?? [];
                var names = await AuthorNamesAsync(db, posts, ct);

                var rows = posts
                    .Where(p => !string.IsNullOrEmpty(p.Id))
                    .Select(p => RowOf(p, names))
                    // Newest first, whatever order VRChat sent the page in.
                    .OrderByDescending(p => p.CreatedAt ?? DateTimeOffset.MinValue)
                    .ToList();

                return Results.Ok(new GroupPostList(
                    rows,
                    Math.Max(answer.Value?.Total ?? 0, rows.Count),
                    number,
                    size,
                    RolesOf(settings),
                    clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetGroupPosts")
            .WithSummary("List group posts")
            .WithDescription(
                "The group's posts, read from VRChat when you ask, newest first. `page` and "
                + "`pageSize` page it (at most 100 a page); `total` is VRChat's count. One request to "
                + "VRChat per call, never retried. Authors are named from the profiles Modbot already "
                + "has; `roles` are the group's roles as the last group read found them, for choosing "
                + "who a new post is for.")
            .Produces<GroupPostList>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/", (
                HttpContext http,
                [FromBody] GroupPostBody body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupPosts? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
                new Writes(db, clock, vrchat, facts, partitions).SaveAsync(http, body, creating: true, ct))
            .RequiresFlag(ModbotPermissions.ManageGroupPosts)
            .WithName("CreateGroupPost")
            .WithSummary("Create group post")
            .WithDescription(
                "Post in the group on VRChat. `title` and `text` are required. `visibility` is "
                + "`group` (members only, the default) or `public`. `roleIds` limits it to those "
                + "roles; empty means every member. `notify` has VRChat tell the members. One "
                + "request, never retried; a refusal answers with VRChat's own message. Once VRChat "
                + "accepts, the audit log records who posted.")
            .Produces<GroupPostSaved>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPut("/", (
                HttpContext http,
                [FromBody] GroupPostBody body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupPosts? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
                new Writes(db, clock, vrchat, facts, partitions).SaveAsync(http, body, creating: false, ct))
            .RequiresFlag(ModbotPermissions.ManageGroupPosts)
            .WithName("UpdateGroupPost")
            .WithMetadata(new ReplacedBy("PUT /api/group/posts/{id}"))
            .WithSummary("Update group post")
            .WithDescription(
                "Change a post: `id` names it, and the title, text, `visibility` and `roleIds` "
                + "replace what it had. VRChat replaces the whole post, so send `imageId` back to "
                + "keep its picture. Members are not notified again. One request, never retried. "
                + "Once VRChat accepts, the audit log records who changed it.")
            .Produces<GroupPostSaved>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/delete", (
                HttpContext http,
                [FromBody] GroupPostDelete body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupPosts? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
                new Writes(db, clock, vrchat, facts, partitions).DeleteAsync(http, body, ct))
            .RequiresFlag(ModbotPermissions.ManageGroupPosts)
            .WithName("DeleteGroupPost")
            .WithMetadata(new ReplacedBy("DELETE /api/group/posts/{id}"))
            .WithSummary("Delete group post")
            .WithDescription(
                "Delete a post on VRChat. `id` names it; `title` is kept in the audit log with the "
                + "deletion, as the page showed it. One request, never retried. A post VRChat no "
                + "longer has answers 404.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        // The same change and deletion with the post in the address (API conventions design §4).
        group.MapPut("/{id}", async (
                string id,
                HttpContext http,
                [FromBody] GroupPostBody body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupPosts? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.Id is { Length: > 0 } named && !string.Equals(named, id, StringComparison.Ordinal))
                    return GroupPageAnswers.Invalid("The address and the body name different posts.");

                return await new Writes(db, clock, vrchat, facts, partitions)
                    .SaveAsync(http, body with { Id = id }, creating: false, ct);
            })
            .RequiresFlag(ModbotPermissions.ManageGroupPosts)
            .WithName("UpdateGroupPostById")
            .WithSummary("Update group post")
            .WithDescription(
                "Change the post named in the address: the title, text, `visibility` and `roleIds` "
                + "replace what it had. VRChat replaces the whole post, so send `imageId` back to "
                + "keep its picture. Members are not notified again. One request, never retried. "
                + "Once VRChat accepts, the audit log records who changed it.")
            .Produces<GroupPostSaved>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete("/{id}", (
                string id,
                [FromQuery] string? title,
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupPosts? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
                new Writes(db, clock, vrchat, facts, partitions).DeleteAsync(http, new GroupPostDelete(id, title), ct))
            .RequiresFlag(ModbotPermissions.ManageGroupPosts)
            .WithName("DeleteGroupPostById")
            .WithSummary("Delete group post")
            .WithDescription(
                "Delete the post named in the address on VRChat. `title`, when given, is kept in the "
                + "audit log with the deletion, as the page showed it. One request, never retried. A "
                + "post VRChat no longer has answers 404.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>
    /// The three writes, with what they share in one place. The VRChat and fact-log services are
    /// optional so that a host without them refuses to act rather than half-acting.
    /// </summary>
    private sealed class Writes(
        ModbotContext db,
        IModbotClock clock,
        GroupPosts? vrchat = null,
        IFactWriter? facts = null,
        EventPartitionMaintainer? partitions = null)
    {
        public async Task<IResult> SaveAsync(HttpContext http, GroupPostBody body, bool creating, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(body);

            if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                return Results.Forbid();

            if (vrchat is null || facts is null || partitions is null)
                return GroupPageAnswers.NotSetUp();

            var (groupId, settings) = await ManagedGroupAsync(ct);
            if (groupId is null)
                return GroupPageAnswers.NoGroup();

            var (post, problem) = GroupPageRules.TidyPost(body);
            if (post is null)
                return GroupPageAnswers.Invalid(problem!);

            if (!creating && post.Id is null)
                return GroupPageAnswers.Invalid("Say which post to change.");

            var request = new CreateGroupPostRequest(
                // Only ever a picture the post already has: see GroupPostBody.ImageId.
                imageId: creating ? null! : post.ImageId!,
                roleIds: post.RoleIds is { Count: > 0 } roles ? [.. roles] : null!,
                // Changing a post does not ping every member a second time.
                sendNotification: creating && post.Notify,
                text: post.Text!,
                title: post.Title!,
                visibility: post.Visibility == "public" ? GroupPostVisibility.Public : GroupPostVisibility.Group);

            var answer = creating
                ? await vrchat.CreateAsync(groupId, request, ct)
                : await vrchat.UpdateAsync(groupId, post.Id!, request, ct);

            if (!answer.Success)
                return GroupPageAnswers.Refused(answer, creating ? "AddGroupPost" : "UpdateGroupPost", groupId, settings);

            var saved = answer.Value;
            var postId = saved?.Id ?? post.Id ?? string.Empty;

            var data = new JsonObject
            {
                ["postId"] = postId,
                ["title"] = saved?.Title ?? post.Title,
                ["text"] = saved?.Text ?? post.Text,
                ["visibility"] = saved is null ? post.Visibility : GroupPageRules.VisibilityWord(saved.Visibility),
                ["roleIds"] = new JsonArray([.. (saved?.RoleIds ?? [.. post.RoleIds!]).Select(r => (JsonNode?)JsonValue.Create(r))]),
            };

            if (creating)
                data["notified"] = post.Notify;

            var now = clock.UtcNow;

            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                await GroupPageAnswers.WriteFactAsync(
                    facts, partitions, creating ? FactType.GroupPostPosted : FactType.GroupPostChanged,
                    groupId, actor, now, data, ct);

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }

            var row = saved is not null
                ? RowOf(saved, await AuthorNamesAsync(db, [saved], ct))
                : new GroupPostRow(
                    postId, post.Title, post.Text, null, null, null, post.ImageId, post.Visibility!,
                    post.RoleIds ?? [], now, now);

            return Results.Ok(new GroupPostSaved(row));
        }

        public async Task<IResult> DeleteAsync(HttpContext http, GroupPostDelete body, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(body);

            if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                return Results.Forbid();

            if (vrchat is null || facts is null || partitions is null)
                return GroupPageAnswers.NotSetUp();

            var (groupId, settings) = await ManagedGroupAsync(ct);
            if (groupId is null)
                return GroupPageAnswers.NoGroup();

            if (string.IsNullOrWhiteSpace(body.Id))
                return GroupPageAnswers.Invalid("Say which post to delete.");

            var answer = await vrchat.DeleteAsync(groupId, body.Id, ct);

            if (!answer.Success)
            {
                // Somebody deleted it in VRChat first: the row was out of date, nothing failed.
                return answer.StatusCode == StatusCodes.Status404NotFound
                    ? Results.Json(
                        new { error = "That post is no longer there. Somebody may have deleted it in VRChat." },
                        statusCode: StatusCodes.Status404NotFound)
                    : GroupPageAnswers.Refused(answer, "DeleteGroupPost", groupId, settings);
            }

            var now = clock.UtcNow;

            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                await GroupPageAnswers.WriteFactAsync(
                    facts, partitions, FactType.GroupPostRemoved, groupId, actor, now,
                    new JsonObject
                    {
                        ["postId"] = body.Id,
                        ["title"] = string.IsNullOrWhiteSpace(body.Title) ? null : body.Title.Trim(),
                    },
                    ct);

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }

            return Results.NoContent();
        }

        /// <summary>The managed group, with the settings a refusal names the account's roles from.</summary>
        private async Task<(string? GroupId, Core.Data.Entities.Settings? Settings)> ManagedGroupAsync(CancellationToken ct)
        {
            var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
            return (settings?.ManagedGroupId is { Length: > 0 } id ? id : null, settings);
        }
    }

    /// <summary>
    /// Display names for the posts' authors, from the profiles Modbot has already read. One read of
    /// Modbot's own table for the page, never a request to VRChat per author.
    /// </summary>
    private static async Task<Dictionary<string, string>> AuthorNamesAsync(
        ModbotContext db, IReadOnlyList<GroupPost> posts, CancellationToken ct)
    {
        var ids = posts
            .Select(p => p.AuthorId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        return await db.VRChatUsers.AsNoTracking()
            .Where(u => ids.Contains(u.UserId) && u.DisplayName != null)
            .ToDictionaryAsync(u => u.UserId, u => u.DisplayName!, StringComparer.Ordinal, ct);
    }

    private static GroupPostRow RowOf(GroupPost post, IReadOnlyDictionary<string, string> names)
        => new(
            post.Id,
            post.Title,
            post.Text,
            post.AuthorId,
            post.AuthorId is { } author && names.TryGetValue(author, out var name) ? name : null,
            string.IsNullOrWhiteSpace(post.ImageUrl) ? null : post.ImageUrl,
            string.IsNullOrWhiteSpace(post.ImageId) ? null : post.ImageId,
            GroupPageRules.VisibilityWord(post.Visibility),
            post.RoleIds ?? [],
            Instant(post.CreatedAt),
            Instant(post.UpdatedAt));

    /// <summary>A VRChat timestamp, or null for the empty value the SDK leaves when there was none.</summary>
    private static DateTimeOffset? Instant(DateTime value)
        => value == default ? null : AuditLogEntryMapper.ReadTimestamp(value);

    /// <summary>The group's roles as the last group read found them, in VRChat's order.</summary>
    private static IReadOnlyList<GroupRoleChoice> RolesOf(Core.Data.Entities.Settings settings)
        => (GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot)?.Roles ?? [])
            .OrderBy(r => r.Order)
            .Select(r => new GroupRoleChoice(r.Id, string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name))
            .ToList();
}
