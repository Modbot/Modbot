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
using Modbot.VRChat.GroupPage;
using Modbot.VRChat.Invites;
using Modbot.VRChat.Sync;
using SentInvite = VRChat.API.Model.GroupMember;

namespace Modbot.Api.Features.GroupPage;

/// <summary>
/// The invites the group has sent on VRChat, from the VRChat page's Invites tab: the list, and
/// cancelling one; and sending one, for a group's own tools (API conventions design §8).
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
/// <para>
/// <strong>Sending one goes through <see cref="GroupInvites"/>,</strong> the same code auto-invites
/// use, so the deployment's one invite every thirty seconds holds whoever asks, and the person is
/// remembered as invited, so auto-invites wait their usual time before asking them again. The
/// row it writes is the auto-invites' own, so it also counts in their "Invites sent". The endpoint
/// class is <c>groups.invites</c> (foundation §4.3.4), which that pace was set for.
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

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] GroupInviteSend body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupInvites? invites,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                if (invites is null || facts is null || partitions is null)
                    return GroupPageAnswers.NotSetUp();

                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
                    return GroupPageAnswers.NoGroup();

                if (string.IsNullOrWhiteSpace(body.UserId))
                    return GroupPageAnswers.Invalid("Say who to invite.");

                // Compared as text: the id is opaque (foundation §3.1.1).
                if (settings.VRChatSessionUserId is { Length: > 0 } self && string.Equals(self, body.UserId, StringComparison.Ordinal))
                    return GroupPageAnswers.Invalid("That is the account Modbot signs in as.");

                // The deployment's one-invite-every-thirty-seconds, shared with auto-invites
                // (auto-invites design §5). Said with how long is left, never waited out here.
                var wait = await invites.WaitBeforeNextAsync(ct);
                if (wait > TimeSpan.Zero)
                    return TooSoon(http, wait);

                var sent = await invites.SendAsync(groupId, body.UserId, instanceId: null, ct, VRChatCallPriority.Interactive);

                switch (sent.Outcome)
                {
                    case InviteOutcome.TooSoon:
                        return TooSoon(http, await invites.WaitBeforeNextAsync(ct));

                    case InviteOutcome.Refused:
                        return Problems.Of(
                            StatusCodes.Status502BadGateway,
                            sent.Problem ?? "VRChat did not send the invite.",
                            Problems.VRChatRefused);
                }

                await GroupPageAnswers.WriteFactAsync(
                    facts, partitions, FactType.GroupInviteSent, groupId, actor, clock.UtcNow,
                    new JsonObject
                    {
                        ["groupId"] = groupId,
                        ["displayName"] = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim(),
                    },
                    ct,
                    subjectId: body.UserId);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageGroupInvites)
            .WithName("SendGroupInvite")
            .WithSummary("Invite to the group")
            .WithDescription(
                "Invite one person to the group on VRChat. `userId` names them; `displayName` is "
                + "kept in the audit log. At most one invite goes out every thirty seconds across "
                + "the deployment, auto-invites included: sooner answers 429 with `Retry-After`. "
                + "One request, never retried; a refusal answers with what VRChat said. Once VRChat "
                + "accepts, the audit log records who sent it, on the invited person's history. It "
                + "counts as an invite Modbot sent, so auto-invites wait as long before inviting "
                + "the same person as they would after one of their own.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
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

    /// <summary>An invite asked for too soon after the last one, with how long is left.</summary>
    private static IResult TooSoon(HttpContext http, TimeSpan wait)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return Problems.Of(
            StatusCodes.Status429TooManyRequests,
            $"One invite goes out every thirty seconds. Try again in {seconds} second{(seconds == 1 ? "" : "s")}.",
            Problems.TooManyRequests);
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
