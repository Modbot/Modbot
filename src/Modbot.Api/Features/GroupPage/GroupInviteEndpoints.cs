using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat.GroupPage;
using Modbot.VRChat.Sync;
using SentInvite = VRChat.API.Model.GroupMember;

namespace Modbot.Api.Features.GroupPage;

/// <summary>
/// The invites the group has sent on VRChat, from the VRChat page's Invites tab: the list, and
/// cancelling one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read from VRChat when asked, never stored and never polled.</strong> One request when
/// the tab opens, one per page, one per Refresh. Names VRChat leaves out come from the profiles
/// Modbot already has, so filling the page in costs nothing more.
/// </para>
/// <para>
/// <strong>A cancel is one request, and nothing is recorded unless VRChat accepted it.</strong>
/// The fact is about the person who was invited, so it shows on their history. Their id travels
/// in the body (foundation §3.1.1).
/// </para>
/// </remarks>
public static class GroupInviteEndpoints
{
    public static IEndpointRouteBuilder MapGroupInvites(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/group/invites").WithTags("Group page").RequireAuthorization();

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupSentInvites? vrchat,
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
                var size = Math.Clamp(pageSize ?? GroupPageRules.InvitePageSize, 1, GroupSentInvites.MaxPageSize);

                var answer = await vrchat.ListAsync(groupId, size, (number - 1) * size, ct);

                if (!answer.Success)
                    return GroupPageAnswers.Refused(answer, "GetGroupInvites", groupId, settings);

                var invited = (answer.Value ?? [])
                    .Where(m => !string.IsNullOrEmpty(m.UserId))
                    .ToList();

                var names = await KnownNamesAsync(db, invited, ct);

                return Results.Ok(new GroupInviteList(
                    invited
                        .Select(m => new GroupInviteRow(
                            m.UserId,
                            NameOf(m, names),
                            m.CreatedAt is { } at && at != default ? AuditLogEntryMapper.ReadTimestamp(at) : null))
                        .OrderByDescending(r => r.InvitedAt ?? DateTimeOffset.MinValue)
                        .ToList(),
                    number,
                    size,
                    invited.Count >= size,
                    clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ManageGroupInvites)
            .WithName("GetGroupInvites")
            .WithSummary("List sent group invites")
            .WithDescription(
                "The people the group has invited on VRChat who have not answered yet, read from "
                + "VRChat when you ask, newest first. `page` and `pageSize` page it (at most 100 a "
                + "page); `hasMore` is true when the page came back full, because VRChat sends no "
                + "total. One request to VRChat per call, never retried.")
            .Produces<GroupInviteList>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/cancel", async (
                HttpContext http,
                [FromBody] GroupInviteCancel body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupSentInvites? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                if (vrchat is null || facts is null || partitions is null)
                    return GroupPageAnswers.NotSetUp();

                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
                    return GroupPageAnswers.NoGroup();

                if (string.IsNullOrWhiteSpace(body.UserId))
                    return GroupPageAnswers.Invalid("Say whose invite to cancel.");

                var answer = await vrchat.CancelAsync(groupId, body.UserId, ct);

                if (!answer.Success)
                {
                    // They answered it, or somebody cancelled it in VRChat first.
                    return answer.StatusCode == StatusCodes.Status404NotFound
                        ? Results.Json(
                            new { error = "That invite is no longer there. They may have answered it, or somebody cancelled it in VRChat." },
                            statusCode: StatusCodes.Status404NotFound)
                        : GroupPageAnswers.Refused(answer, "DeleteGroupInvite", groupId, settings);
                }

                var now = clock.UtcNow;

                await using (var transaction = await db.Database.BeginTransactionAsync(ct))
                {
                    await GroupPageAnswers.WriteFactAsync(
                        facts, partitions, FactType.GroupInviteCancelled, groupId, actor, now,
                        new JsonObject
                        {
                            ["groupId"] = groupId,
                            ["displayName"] = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim(),
                        },
                        ct,
                        subjectId: body.UserId);

                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageGroupInvites)
            .WithName("CancelGroupInvite")
            .WithSummary("Cancel group invite")
            .WithDescription(
                "Take back the group's invite to one person on VRChat. `userId` names them; "
                + "`displayName` is kept in the audit log, as the page showed it. One request, never "
                + "retried. An invite VRChat no longer has answers 404. Once VRChat accepts, the "
                + "audit log records who cancelled it, on the invited person's history.")
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

    /// <summary>The name VRChat sent with the invite, or the one Modbot already knows.</summary>
    private static string? NameOf(SentInvite member, IReadOnlyDictionary<string, string> names)
        => !string.IsNullOrWhiteSpace(member.User?.DisplayName)
            ? member.User.DisplayName
            : names.TryGetValue(member.UserId, out var name) ? name : null;

    /// <summary>
    /// Display names from the profiles Modbot has already read, for the rows VRChat sent without
    /// one. One read of Modbot's own table, never a request to VRChat per person.
    /// </summary>
    private static async Task<Dictionary<string, string>> KnownNamesAsync(
        ModbotContext db, IReadOnlyList<SentInvite> invited, CancellationToken ct)
    {
        var ids = invited
            .Where(m => string.IsNullOrWhiteSpace(m.User?.DisplayName))
            .Select(m => m.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        return await db.VRChatUsers.AsNoTracking()
            .Where(u => ids.Contains(u.UserId) && u.DisplayName != null)
            .ToDictionaryAsync(u => u.UserId, u => u.DisplayName!, StringComparer.Ordinal, ct);
    }
}
