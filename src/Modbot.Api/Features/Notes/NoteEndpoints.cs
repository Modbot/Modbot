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

namespace Modbot.Api.Features.Notes;

/// <summary>
/// Notes about a person: write one, read them back, take one back (M4 §2, notes design).
/// </summary>
/// <remarks>
/// <para>
/// Reading needs <see cref="ModbotPermissions.ViewAuditLog"/>, because a note <em>is</em> a fact
/// in the moderation log and that flag already governs those rows. Writing needs
/// <see cref="ModbotPermissions.WriteNotes"/>. Taking one back is open to its author as well as to
/// anyone who may write notes, so that check is inside the handler — a route attribute can only
/// say "all of these flags".
/// </para>
/// <para>
/// The person's id travels in the query string or the body, never in the path: VRChat ids are
/// opaque and a legacy one can contain anything, so a route constraint on one would be a format
/// check (foundation §3.1.1). Only the note's own id is in a path, and that is Modbot's own number.
/// Every parameter is explicitly attributed, for the reason the other features give — an
/// unattributed concrete type is bound as the body and throws while the routes are mapped.
/// </para>
/// </remarks>
public static class NoteEndpoints
{
    public static IEndpointRouteBuilder MapNotes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/notes").WithTags("Notes").RequireAuthorization();

        group.MapGet("/", async (
                HttpContext http,
                [FromQuery] string? userId,
                [FromQuery] string? platform,
                [FromQuery] int? limit,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                try
                {
                    return Results.Ok(await new NoteService(db, clock)
                        .ListAsync(userId ?? string.Empty, platform, limit ?? NoteService.DefaultLimit, caller, ct));
                }
                catch (NoteRefused refused)
                {
                    return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
                }
            })
            .RequiresFlag(ModbotPermissions.ViewAuditLog)
            .WithName("ListNotes")
            .WithSummary("One person's notes, newest first")
            .WithDescription(
                "platform is VRChat or Discord and defaults to VRChat. Notes that were taken back "
                + "are listed too and say so; standing counts only the ones that still stand.")
            .Produces<NoteListResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] WriteNoteRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                // A note with no author is not a note: it is one person's words about another, and
                // who wrote it is half of what makes it worth reading (spec 5.9.1).
                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                try
                {
                    return Results.Ok(await new NoteService(db, clock, facts, partitions)
                        .WriteAsync(body, caller, ct));
                }
                catch (NoteRefused refused)
                {
                    return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
                }
            })
            .RequiresFlag(ModbotPermissions.WriteNotes)
            .WithName("WriteNote")
            .WithSummary("Write a note about a person")
            .WithDescription(
                $"The text is stored and shown as text, never as markup, and is at most "
                + $"{NoteService.MaxTextLength} characters. The note is one fact in the moderation "
                + "log, so it shows in the person's history and can be read by anyone who may read "
                + "that log.")
            .Produces<NoteView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id:long}/take-back", async (
                HttpContext http,
                [FromRoute] long id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                try
                {
                    return Results.Ok(await new NoteService(db, clock, facts, partitions)
                        .TakeBackAsync(id, caller, ct));
                }
                catch (NoteRefused refused)
                {
                    return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
                }
            })
            .WithName("TakeBackNote")
            .WithSummary("Take a note back, so it no longer stands")
            .WithDescription(
                "Nothing is deleted. The note stays in the log and a second entry records that it "
                + "was taken back, by whom and when. Open to whoever wrote it, and to anyone who "
                + "may write notes. Taking back a note that is already taken back changes nothing.")
            .Produces<NoteView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static Caller? CallerOf(HttpContext http)
        => ModbotAuth.UserIdOf(http.User) is { } id
            ? new Caller(id, http.User.Identity?.Name ?? string.Empty, ModbotAuth.PermissionsOf(http.User))
            : null;
}
