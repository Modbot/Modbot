using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.Cases;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Watches;

/// <summary>
/// Watching a person: list, start, stop, follow up (watching a person design §3).
/// </summary>
/// <remarks>
/// Ids travel in the query string or the body, never in a path, for the reason notes give: a legacy
/// VRChat id can contain anything, and a route constraint on one would be a format check
/// (foundation §3.1.1). Only the watch's own id, Modbot's own, is in a path.
/// </remarks>
public static class WatchEndpoints
{
    public static IEndpointRouteBuilder MapWatches(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/watches").WithTags("Watches").RequireAuthorization();

        group.MapGet("/", async (
                HttpContext http,
                [FromQuery] bool? due,
                [FromServices] WatchService watches,
                CancellationToken ct) =>
            {
                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                return Results.Ok(await watches.ListAsync(due == true, caller, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewAuditLog)
            .WithName("ListWatches")
            .WithSummary("List watched people")
            .WithDescription(
                "Every watch that stands, the soonest follow-up first, then the newest. With "
                + "`due=true`, only those whose follow-up day has come. At most 500.")
            .Produces<WatchList>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/person", async (
                HttpContext http,
                [FromQuery] string? vrchat,
                [FromQuery] string? discord,
                [FromServices] WatchService watches,
                CancellationToken ct) =>
            {
                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                return await Answer(() => watches.ForPersonAsync(vrchat, discord, caller, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewAuditLog)
            .WithName("ListPersonWatches")
            .WithSummary("List a person's watches")
            .WithDescription(
                "The watches on a person's VRChat account, Discord account, or both: the one that "
                + "stands first, then the ones that ended, newest first. At most 20.")
            .Produces<PersonWatchList>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] StartWatchRequest body,
                [FromServices] WatchService watches,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                return await Answer(() => watches.StartAsync(body, caller, ct));
            })
            .RequiresFlag(ModbotPermissions.WriteNotes)
            .WithName("StartWatch")
            .WithSummary("Watch a person")
            .WithDescription(
                "Starts watching one account. A watched person is Flagged wherever Flagged is shown, "
                + "and their arrival in one of the group's instances raises a notification. The "
                + $"reason is required, at most {PersonWatch.MaxReasonLength} characters. `endsAt` "
                + "stops it on its own; `followUpAt` raises a reminder to whoever set it and lists it "
                + "on Now. One watch per account at a time. Nothing is changed on VRChat or Discord.")
            .Produces<WatchView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id:guid}/stop", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] WatchService watches,
                CancellationToken ct) =>
            {
                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                return await Answer(() => watches.StopAsync(id, caller, ct));
            })
            .WithName("StopWatch")
            .WithSummary("Stop watching a person")
            .WithDescription(
                "Open to whoever started the watch, and to anyone who may write notes. The watch "
                + "stays in the person's history.")
            .Produces<WatchView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id:guid}/followed-up", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] WatchService watches,
                CancellationToken ct) =>
            {
                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                return await Answer(() => watches.FollowedUpAsync(id, caller, ct));
            })
            .WithName("FollowedUpOnWatch")
            .WithSummary("Mark a follow-up done")
            .WithDescription(
                "Clears the watch's follow-up day; the watch itself carries on. Open to whoever "
                + "started the watch, and to anyone who may write notes.")
            .Produces<WatchView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> Answer<T>(Func<Task<T>> run)
    {
        try
        {
            return Results.Ok(await run());
        }
        catch (WatchRefused refused)
        {
            return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
        }
    }

    private static Caller? CallerOf(HttpContext http)
        => ModbotAuth.UserIdOf(http.User) is { } id
            ? new Caller(id, http.User.Identity?.Name ?? string.Empty, ModbotAuth.PermissionsOf(http.User))
            : null;
}
