using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.VRChat.Users;

namespace Modbot.Api.Features.Cases;

/// <summary>
/// Ban case files (spec 5.8.3, ban case files design): the write-up of a ban, with the reasons
/// picked, the moderator's words, the evidence attached and the person's profile at the time.
/// </summary>
/// <remarks>
/// <para>
/// Reading needs <see cref="ModbotPermissions.ViewProfile"/>: a case file is part of a person's
/// history and is shown where their history is. Writing one needs <see cref="ModbotPermissions.Ban"/>.
/// Editing and withdrawing are open to the author as well as to anyone who may ban, so a
/// moderator who has since lost the ban permission can still correct their own write-up; that
/// check is inside the handler, because the route attribute can only say "all of these flags".
/// The evidence list inside a case file needs <see cref="ModbotPermissions.ViewEvidence"/> and is
/// null without it; attaching goes through the evidence upload endpoints with their own flag.
/// </para>
/// <para>
/// Every parameter is explicitly attributed, for the reason the other read endpoints give: an
/// unattributed concrete type on a GET is bound as the body and throws while the route is mapped.
/// The person's id travels in the query string on the list and lookup endpoints, never in the
/// path -- VRChat ids are opaque and a legacy one can contain anything (spec 3.1.1).
/// </para>
/// </remarks>
public static class CaseFileEndpoints
{
    public static IEndpointRouteBuilder MapCaseFiles(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/cases").WithTags("Case files").RequireAuthorization();

        group.MapGet("/", async (
                [FromQuery] string? userId,
                [FromQuery] bool? includeWithdrawn,
                [FromQuery] int? offset,
                [FromQuery] int? limit,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
                Results.Ok(await new CaseFileService(db, clock)
                    .ListAsync(userId?.Trim(), includeWithdrawn ?? false, offset ?? 0, limit ?? 100, ct)))
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("ListCaseFiles")
            .WithSummary("Case files, newest first -- everyone's, or one person's")
            .WithDescription(
                "Withdrawn case files are left out unless includeWithdrawn is true. Nothing is "
                + "ever deleted, so a withdrawn one is still there to read; it just no longer "
                + "counts as the write-up of its ban.")
            .Produces<CaseFileListResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/missing", async (
                [FromQuery] int? days,
                [FromQuery] int? limit,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
                Results.Ok(await new CaseFileService(db, clock)
                    .MissingAsync(days ?? CaseFileService.DefaultMissingDays, limit ?? 100, ct)))
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("ListUnwrittenCaseFiles")
            .WithSummary("Bans in the last N days with no case file, newest first")
            .WithDescription(
                "Read from the recorded ban facts, so it covers the window Modbot's audit-log sync "
                + "covers and nothing before it. A ban is covered when a case file that stands "
                + "names its audit entry, or was written for the same person on or after it. A "
                + "withdrawn case file covers nothing. This is the list the accountability "
                + "\"bans without a report\" signal reads.")
            .Produces<UnwrittenBanListResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/lookup", async (
                [FromQuery(Name = "userId")] string[]? userIds,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
                Results.Ok(await new CaseFileService(db, clock).LookupAsync(userIds ?? [], ct)))
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("LookupCaseFiles")
            .WithSummary("Whether each of these people has a case file -- the badge beside a ban")
            .Produces<IReadOnlyList<CaseFileLookup>>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] EvidenceOptions? evidenceOptions,
                [FromServices] IEvidenceStore? store,
                [FromServices] EvidenceStoreMonitor? monitor,
                CancellationToken ct) =>
            {
                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                var view = await new CaseFileService(db, clock, evidenceOptions: evidenceOptions, store: store, monitor: monitor)
                    .ViewAsync(id, caller, ct);

                return view is null ? Results.NotFound(new { error = "No such case file." }) : Results.Ok(view);
            })
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("GetCaseFile")
            .WithSummary("One case file: reasons, the written reason, the evidence, and the profile at the time")
            .WithDescription(
                "`evidence` is null unless the caller holds ViewEvidence. `snapshot.explanation` "
                + "says when the profile was captured and how old it was then; the snapshot never "
                + "changes afterwards except for the one permitted recapture, which "
                + "`snapshot.canCaptureAgain` offers when a fresher profile has arrived. "
                + "`evidenceDelivery` repeats what the Settings evidence card says about how bytes "
                + "travel, so the attach control can say the same thing.")
            .Produces<CaseFileView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] CreateCaseFileRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                [FromServices] VRChatUserProfiles? profiles,
                [FromServices] EvidenceOptions? evidenceOptions,
                [FromServices] IEvidenceStore? store,
                [FromServices] EvidenceStoreMonitor? monitor,
                CancellationToken ct) =>
            {
                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                var service = new CaseFileService(db, clock, facts, partitions, profiles, evidenceOptions, store, monitor);

                return await Attempt(async () => Results.Ok(await service.CreateAsync(body, caller, ct)));
            })
            .RequiresFlag(ModbotPermissions.Ban)
            .WithName("CreateCaseFile")
            .WithSummary("Write the case file for a ban")
            .WithDescription(
                "Names the person and, optionally, the audit entry the ban came from; otherwise "
                + "the newest recorded ban of that person is used, or the ban list's row. Takes "
                + "the profile snapshot from what Modbot has stored, then asks the profile sync for "
                + "a fresher copy -- never the other way round, because the write-up must not wait "
                + "on VRChat. One case file per ban: a second is refused with 409 naming the "
                + "first. Recorded as a fact against your account.")
            .Produces<CaseFileCreatedResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] UpdateCaseFileRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                [FromServices] EvidenceOptions? evidenceOptions,
                [FromServices] IEvidenceStore? store,
                [FromServices] EvidenceStoreMonitor? monitor,
                CancellationToken ct) =>
            {
                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                var service = new CaseFileService(db, clock, facts, partitions, null, evidenceOptions, store, monitor);

                return await Attempt(async () => Results.Ok(await service.UpdateAsync(id, body, caller, ct)));
            })
            .WithName("UpdateCaseFile")
            .WithSummary("Change the reasons or the written reason")
            .WithDescription(
                "The author, or anyone who may ban. The change is a fact carrying before and "
                + "after, so the edit history reads back from the log alone. The profile snapshot "
                + "and the evidence are not touched by this.")
            .Produces<CaseFileView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id:guid}/withdraw", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] WithdrawCaseFileRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                [FromServices] EvidenceOptions? evidenceOptions,
                [FromServices] IEvidenceStore? store,
                [FromServices] EvidenceStoreMonitor? monitor,
                CancellationToken ct) =>
            {
                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                var service = new CaseFileService(db, clock, facts, partitions, null, evidenceOptions, store, monitor);

                return await Attempt(async () => Results.Ok(await service.WithdrawAsync(id, body, caller, ct)));
            })
            .WithName("WithdrawCaseFile")
            .WithSummary("Mark a case file withdrawn, with a note")
            .WithDescription(
                "Case files are never deleted. A withdrawn one stays readable, stops counting as "
                + "the write-up of its ban, and cannot be edited again -- write a new one instead.")
            .Produces<CaseFileView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id:guid}/capture-again", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                [FromServices] EvidenceOptions? evidenceOptions,
                [FromServices] IEvidenceStore? store,
                [FromServices] EvidenceStoreMonitor? monitor,
                CancellationToken ct) =>
            {
                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                var service = new CaseFileService(db, clock, facts, partitions, null, evidenceOptions, store, monitor);

                return await Attempt(async () => Results.Ok(await service.CaptureAgainAsync(id, caller, ct)));
            })
            .WithName("CaptureCaseFileProfileAgain")
            .WithSummary("Take the profile snapshot again, once, after a fresher profile has arrived")
            .WithDescription(
                "Allowed once per case file, and only when VRChat has answered with a newer "
                + "profile since the snapshot was taken. The snapshot it replaces is kept in full "
                + "in the fact that records this, so the first capture is never lost.")
            .Produces<CaseFileView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>
    /// The signed-in account. Null only on a misconfigured host: the cookie always carries the id.
    /// </summary>
    private static Caller? CallerOf(HttpContext http)
        => ModbotAuth.UserIdOf(http.User) is { } id
            ? new Caller(id, http.User.Identity?.Name ?? string.Empty, ModbotAuth.PermissionsOf(http.User))
            : null;

    /// <summary>A refusal becomes the status it names, with its sentence, never a 500.</summary>
    private static async Task<IResult> Attempt(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (CaseFileRefused refused)
        {
            return refused.Status == StatusCodes.Status403Forbidden
                ? Results.Json(new { error = refused.Message }, statusCode: StatusCodes.Status403Forbidden)
                : Results.Json(
                    new { error = refused.Message, caseId = refused.ExistingCaseId },
                    statusCode: refused.Status);
        }
    }
}
