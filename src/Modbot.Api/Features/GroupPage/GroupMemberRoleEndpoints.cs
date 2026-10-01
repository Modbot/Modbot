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
using Modbot.VRChat;
using Modbot.VRChat.Moderation;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.GroupPage;

/// <summary>
/// Giving a member one of the group's roles on VRChat, and taking one away (API conventions
/// design §8): what a group's own bot needs to promote a member without a person opening VRChat.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One request each, never retried, and nothing recorded unless VRChat accepted.</strong>
/// The same VRChat calls role sync makes, through <see cref="GroupRoles"/>, so the same endpoint
/// class and budget (<c>moderation.write</c>, foundation §4.3.4); at interactive priority, because
/// somebody is waiting on the answer. A refusal is VRChat's own words, with the group permission
/// Modbot's VRChat account lacks when that is why.
/// </para>
/// <para>
/// The fact is about the member, so it shows on their history, and names the Modbot account that
/// asked; VRChat's own audit entry for the same change can only name Modbot's VRChat account. The
/// member list shows the new roles after its next sweep.
/// </para>
/// <para>
/// Modbot will not change its own VRChat account's roles, for the reason it will not ban itself:
/// taking its management role away takes away the access everything else depends on.
/// </para>
/// </remarks>
public static class GroupMemberRoleEndpoints
{
    public static IEndpointRouteBuilder MapGroupMemberRoles(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/group/members").WithTags("Group page").RequireAuthorization();

        group.MapPut("/{userId}/roles/{roleId}", (
                string userId,
                string roleId,
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupRoles? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
                ChangeAsync(giving: true, userId, roleId, http, db, clock, vrchat, facts, partitions, ct))
            .RequiresFlag(ModbotPermissions.ManageGroupRoles)
            .WithName("GiveGroupMemberRole")
            .WithSummary("Give a member a group role")
            .WithDescription(
                "Give the member named in the address one of the group's roles on VRChat. Answers "
                + "with the role ids VRChat says they now have. One request, never retried; a refusal "
                + "answers with VRChat's own message, and a 403 for a permission Modbot's VRChat "
                + "account lacks names it in `missingGroupPermission`. Once VRChat accepts, the audit "
                + "log records who gave it, on the member's history.")
            .Produces<GroupMemberRoles>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete("/{userId}/roles/{roleId}", (
                string userId,
                string roleId,
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupRoles? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
                ChangeAsync(giving: false, userId, roleId, http, db, clock, vrchat, facts, partitions, ct))
            .RequiresFlag(ModbotPermissions.ManageGroupRoles)
            .WithName("TakeGroupMemberRole")
            .WithSummary("Take a group role from a member")
            .WithDescription(
                "Take one of the group's roles away from the member named in the address, on VRChat. "
                + "Answers with the role ids VRChat says they still have. One request, never retried. "
                + "Once VRChat accepts, the audit log records who took it, on the member's history.")
            .Produces<GroupMemberRoles>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> ChangeAsync(
        bool giving,
        string userId,
        string roleId,
        HttpContext http,
        ModbotContext db,
        IModbotClock clock,
        GroupRoles? vrchat,
        IFactWriter? facts,
        EventPartitionMaintainer? partitions,
        CancellationToken ct)
    {
        if (ModbotAuth.UserIdOf(http.User) is not { } actor)
            return Results.Forbid();

        if (vrchat is null || facts is null || partitions is null)
            return GroupPageAnswers.NotSetUp();

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
            return GroupPageAnswers.NoGroup();

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(roleId))
            return GroupPageAnswers.Invalid("Say which member and which role.");

        // Compared as text: the id is opaque (foundation §3.1.1).
        if (settings.VRChatSessionUserId is { Length: > 0 } self && string.Equals(self, userId, StringComparison.Ordinal))
            return GroupPageAnswers.Invalid("That is the account Modbot signs in as. Modbot will not change its own roles.");

        var operation = giving ? "AddGroupMemberRole" : "RemoveGroupMemberRole";

        var answer = giving
            ? await vrchat.GiveAsync(groupId, userId, roleId, ct, VRChatCallPriority.Interactive)
            : await vrchat.TakeAsync(groupId, userId, roleId, ct, VRChatCallPriority.Interactive);

        if (!answer.Success)
        {
            // Not a member, or no such role: the request named something VRChat does not have.
            return answer.StatusCode == StatusCodes.Status404NotFound
                ? Problems.Of(StatusCodes.Status404NotFound, GroupPageAnswers.Said(answer), Problems.NotFound)
                : GroupPageAnswers.Refused(answer, operation, groupId, settings);
        }

        var roleName = GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot)?.Roles
            .FirstOrDefault(r => string.Equals(r.Id, roleId, StringComparison.Ordinal))?.Name;

        await GroupPageAnswers.WriteFactAsync(
            facts, partitions, giving ? FactType.GroupRoleGiven : FactType.GroupRoleTaken, groupId, actor, clock.UtcNow,
            new JsonObject
            {
                ["groupId"] = groupId,
                ["roleId"] = roleId,
                ["roleName"] = roleName,
            },
            ct,
            subjectId: userId);

        return Results.Ok(new GroupMemberRoles(userId, answer.Value ?? []));
    }
}

/// <summary>A member's roles in the group, as VRChat answered a change to them.</summary>
/// <param name="UserId">The member's VRChat id. Opaque.</param>
/// <param name="RoleIds">The ids of the roles VRChat says they have now.</param>
public sealed record GroupMemberRoles(string UserId, IReadOnlyList<string> RoleIds);
