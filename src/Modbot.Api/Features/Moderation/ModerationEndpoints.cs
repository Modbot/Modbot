using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Features.Cases;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.VRChat.Moderation;
using Modbot.VRChat.Users;

namespace Modbot.Api.Features.Moderation;

/// <summary>
/// Kick, ban and unban a person in the managed group (M4 §2, §4).
/// </summary>
/// <remarks>
/// <para>
/// One flag each: <see cref="ModbotPermissions.Kick"/>, <see cref="ModbotPermissions.Ban"/> and
/// <see cref="ModbotPermissions.Unban"/>. They were reserved when the bitfield was written and are
/// real from here on; unbanning is separate from banning on purpose, because letting somebody back
/// in is a different decision from keeping them out.
/// </para>
/// <para>
/// The person's id travels in the body, never in the path: VRChat ids are opaque and a legacy one
/// can contain anything, so a route constraint on it would be a format check (foundation §3.1.1).
/// Every parameter is explicitly attributed, for the reason the other features give — an
/// unattributed concrete type is bound as the body and throws while the routes are mapped.
/// </para>
/// <para>
/// These are the only endpoints in Modbot that change something in VRChat, so what they answer
/// with is load-bearing: <c>done</c> is true only when VRChat accepted, and a refusal carries what
/// VRChat said rather than a sentence Modbot made up.
/// </para>
/// </remarks>
public static class ModerationEndpoints
{
    public static IEndpointRouteBuilder MapModerationActions(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/moderation").WithTags("Moderation").RequireAuthorization();

        Map(group, ModerationActionService.Kick, ModbotPermissions.Kick)
            .WithName("KickPerson")
            .WithSummary("Kick a person")
            .WithDescription(
                "Only somebody who is in the group can be kicked out of it; VRChat answers a kick "
                + "of anybody else with \"they are not in the group\". Nothing is recorded as done "
                + "unless VRChat accepted it. A reason is optional unless the group has asked for "
                + "one. The key makes one confirmation act once: send the same key again and you "
                + "get the first answer back, not a second kick.");

        Map(group, ModerationActionService.Ban, ModbotPermissions.Ban)
            .WithName("BanPerson")
            .WithSummary("Ban a person")
            .WithDescription(
                "Works on anybody, member or not: VRChat's group ban takes a user id, so somebody "
                + "who has never joined can be kept out before they arrive. A person Modbot has "
                + "never seen is recorded as a person by the ban, and their profile is fetched "
                + "afterwards. A reason is required, and the ban's case file is written from it "
                + "and the note. Nothing is recorded as done unless VRChat accepted it. The key "
                + "makes one confirmation act once.");

        Map(group, ModerationActionService.Unban, ModbotPermissions.Unban)
            .WithName("UnbanPerson")
            .WithSummary("Unban a person")
            .WithDescription(
                "There has to be a ban to lift; VRChat answers an unban of anybody who is not "
                + "banned with \"they are not banned\". Nothing is recorded as done unless VRChat "
                + "accepted it. A reason is optional unless the group has asked for one. The key "
                + "makes one confirmation act once.");

        return app;
    }

    /// <summary>
    /// The three actions differ only in their word and their flag, so they are mapped from one
    /// place: three copies of this handler would be three chances for one of them to drift.
    /// </summary>
    private static RouteHandlerBuilder Map(
        IEndpointRouteBuilder group, string action, ModbotPermissions flag)
        => group.MapPost($"/{action}", async (
                HttpContext http,
                [FromBody] ModerationActionRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupModeration? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                [FromServices] VRChatUserProfiles? profiles,
                [FromServices] EvidenceOptions? evidenceOptions,
                [FromServices] IEvidenceStore? store,
                [FromServices] EvidenceStoreMonitor? monitor,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                // An action with no author is not an action: every fact this writes names who
                // pressed the button, and VRChat's own log cannot (spec 5.9.1).
                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                // A host that mapped the API without the gate or the fact log can still read
                // everything; it just cannot act, and says so rather than failing mid-request.
                if (vrchat is null || facts is null || partitions is null)
                {
                    return Results.Problem(
                        "This deployment is not set up to act in VRChat.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var caller = new Caller(actor, http.User.Identity?.Name ?? string.Empty, ModbotAuth.PermissionsOf(http.User));
                var cases = new CaseFileService(db, clock, facts, partitions, profiles, evidenceOptions, store, monitor);
                var service = new ModerationActionService(db, clock, vrchat, facts, partitions, cases, profiles: profiles);

                try
                {
                    return Results.Ok(await service.RunAsync(action, body, caller, ct));
                }
                catch (ModerationRefused refused)
                {
                    return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
                }
            })
            .RequiresFlag(flag)
            .Produces<ModerationActionResult>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);
}
