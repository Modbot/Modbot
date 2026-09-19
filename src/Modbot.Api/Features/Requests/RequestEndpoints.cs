using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Features.Cases;
using Modbot.Api.Features.Moderation;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Names;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.VRChat;
using Modbot.VRChat.Moderation;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Users;
using VRChatGroupMember = VRChat.API.Model.GroupMember;

namespace Modbot.Api.Features.Requests;

/// <summary>
/// The people waiting to be let into the group, and the two answers to one of them (join requests
/// design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The list is read from VRChat, not from Modbot's own tables.</strong> VRChat has a real
/// endpoint for it, so the screen shows the queue as it stands rather than a reconstruction from
/// the <c>vrchat.group.request.*</c> facts the audit-log sync writes — which would hold only the
/// requests Modbot happened to be running for, and would leave out everybody who asked before this
/// deployment existed. Nothing is stored, and nothing polls it: one VRChat request per page a
/// moderator asks for (join requests design §3).
/// </para>
/// <para>
/// Each row is then filled in from what Modbot already knows about that person — the profile the
/// sync has fetched, and whether they have ever been banned or have left before. None of that
/// costs another VRChat request, and it is the whole reason a moderator would work the queue here
/// rather than in VRChat.
/// </para>
/// <para>
/// Answering goes through <see cref="ModerationActionService"/>, the same path a kick or a ban
/// takes: one confirmation acts once, nothing is recorded unless VRChat accepted, and a refusal is
/// recorded as a refusal. The id travels in the body rather than the path, because VRChat ids are
/// opaque and a route constraint would be the format check foundation §3.1.1 forbids.
/// </para>
/// </remarks>
public static class RequestEndpoints
{
    public const int DefaultPageSize = 50;

    public static IEndpointRouteBuilder MapJoinRequests(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/requests").WithTags("Requests").RequireAuthorization();

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupJoinRequests? vrchat,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                CancellationToken ct) =>
            {
                if (vrchat is null)
                {
                    return Results.Problem(
                        "This deployment is not set up to read join requests.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
                {
                    return Results.Problem(
                        "No VRChat group is set up yet, so there is no queue to read.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                var number = Math.Max(1, page ?? 1);
                var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, GroupJoinRequests.MaxPageSize);

                var answer = await vrchat.ListAsync(groupId, size, (number - 1) * size, ct);

                if (!answer.Success)
                {
                    // Never a made-up list and never an empty one: an empty queue and a queue
                    // Modbot could not read look identical on a screen, and only one of them means
                    // there is nothing to do.
                    return Results.Json(
                        new { error = answer.ErrorMessage ?? "VRChat did not answer." },
                        statusCode: answer.IsRateLimited
                            ? StatusCodes.Status429TooManyRequests
                            : StatusCodes.Status502BadGateway);
                }

                var waiting = answer.Value ?? [];

                return Results.Ok(new JoinRequestList(
                    await RowsAsync(db, groupId, waiting, ct),
                    number,
                    size,
                    waiting.Count >= size,
                    clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ViewJoinRequests)
            .WithName("GetJoinRequests")
            .WithSummary("List join requests")
            .WithDescription(
                "Read from VRChat when you ask, not from a stored copy, so it is the queue as it "
                + "stands. `page` and `pageSize` page it; `hasMore` is true when the page came back "
                + "full, because VRChat sends no total for this list. Each row carries what Modbot "
                + "already knows about the person: their profile, whether the group has banned them "
                + "before, and whether they have been a member before.")
            .Produces<JoinRequestList>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        Answer(group, ModerationActionService.Approve)
            .WithName("ApproveJoinRequest")
            .WithSummary("Approve join request")
            .WithDescription(
                "Let somebody into the group. "
                + "Nothing is recorded as done unless VRChat accepted it. The key makes one "
                + "confirmation act once. `gone` is true when the request had already been "
                + "answered in VRChat: nothing failed, the row was out of date.");

        Answer(group, ModerationActionService.Reject)
            .WithName("RejectJoinRequest")
            .WithSummary("Reject join request")
            .WithDescription(
                "The person may ask again; Modbot never blocks them from asking, which is what Ban "
                + "is for. Nothing is recorded as done unless VRChat accepted it, and `gone` is "
                + "true when the request had already been answered in VRChat.");

        return app;
    }

    /// <summary>
    /// Approve and reject differ by one word, so they are mapped from one place for the reason the
    /// three moderation actions are.
    /// </summary>
    private static RouteHandlerBuilder Answer(IEndpointRouteBuilder group, string action)
        => group.MapPost($"/{action}", async (
                HttpContext http,
                [FromBody] JoinRequestAnswerRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupModeration? moderation,
                [FromServices] GroupJoinRequests? requests,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                [FromServices] VRChatUserProfiles? profiles,
                [FromServices] EvidenceOptions? evidenceOptions,
                [FromServices] IEvidenceStore? store,
                [FromServices] EvidenceStoreMonitor? monitor,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                // An answer with no author is not an answer: the fact names who decided, and
                // VRChat's own log cannot (foundation §5.9.1).
                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                if (moderation is null || requests is null || facts is null || partitions is null)
                {
                    return Results.Problem(
                        "This deployment is not set up to act in VRChat.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var caller = new Caller(actor, http.User.Identity?.Name ?? string.Empty, ModbotAuth.PermissionsOf(http.User));
                var cases = new CaseFileService(db, clock, facts, partitions, profiles, evidenceOptions, store, monitor);
                var service = new ModerationActionService(db, clock, moderation, facts, partitions, cases, requests: requests);

                var ask = new ModerationActionRequest(body.UserId, body.Key, body.ReasonIds, body.Note);

                try
                {
                    return Results.Ok(await service.RunAsync(action, ask, caller, ct));
                }
                catch (ModerationRefused refused)
                {
                    return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
                }
            })
            .RequiresFlag(ModbotPermissions.AnswerJoinRequests)
            .Produces<ModerationActionResult>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// VRChat's queue, with everything Modbot already knows about each person written beside it.
    /// </summary>
    /// <remarks>
    /// Three reads of Modbot's own tables for the whole page, never one per row. A moderator
    /// deciding whether to let somebody in is deciding it mostly on the ban history, so the ban
    /// read is not optional dressing.
    /// </remarks>
    private static async Task<IReadOnlyList<JoinRequestRow>> RowsAsync(
        ModbotContext db, string groupId, IReadOnlyList<VRChatGroupMember> waiting, CancellationToken ct)
    {
        var ids = waiting
            .Select(m => m.UserId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return [];

        var profiles = await db.VRChatUsers.AsNoTracking()
            .Where(u => ids.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, ct);

        var bans = await db.GroupBans.AsNoTracking()
            .Where(b => b.GroupId == groupId && ids.Contains(b.UserId))
            .ToDictionaryAsync(b => b.UserId, ct);

        var members = await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId && ids.Contains(m.UserId))
            .ToDictionaryAsync(m => m.UserId, ct);

        var rows = new List<JoinRequestRow>(waiting.Count);

        foreach (var request in waiting)
        {
            var id = request.UserId ?? string.Empty;

            profiles.TryGetValue(id, out var profile);
            bans.TryGetValue(id, out var ban);
            members.TryGetValue(id, out var member);

            // VRChat sends the name with the request; the stored profile is the fallback, for a
            // legacy account whose request carries no user object at all.
            var name = Blank(request.User?.DisplayName) ?? profile?.DisplayName;

            rows.Add(new JoinRequestRow(
                id,
                name,
                PlainName.Of(name),
                ProfilePictures.Best(profile),
                profile?.TrustRank,
                profile?.Is18PlusVerified ?? false,
                // Read through the audit-log sync's reader, so a VRChat timestamp means the same
                // thing everywhere in Modbot whatever Newtonsoft made of its Kind.
                request.CreatedAt is { } asked ? AuditLogEntryMapper.ReadTimestamp(asked) : null,
                Banned: ban is { LiftedAt: null },
                BannedBefore: ban is { LiftedAt: not null },
                WasMember: member is { LeftAt: not null },
                member?.LeftAt,
                Known: profile is not null || ban is not null || member is not null));
        }

        return rows;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
