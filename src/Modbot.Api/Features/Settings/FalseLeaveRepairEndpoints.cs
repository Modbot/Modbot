using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Analytics.Repairs;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <summary>
/// The way in to the one-off repair of the false "left the group" facts the member sync wrote on
/// 2026-11-21. No screen: an administrator calls it by hand, and it is meant to be removed once it
/// has run.
/// </summary>
/// <remarks>
/// Administrator only, the same as the purge: it deletes facts. GET counts and changes nothing; POST
/// does the repair and refuses to run a second time.
/// </remarks>
public static class FalseLeaveRepairEndpoints
{
    public static IEndpointRouteBuilder MapFalseLeaveRepair(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/repairs/false-leaves").WithTags("Settings").RequireAuthorization();

        group.MapGet("", async (
                [FromServices] FalseLeaveRepair repair,
                CancellationToken ct) => Results.Ok(await repair.PreviewAsync(ct)))
            .RequiresFlag(ModbotPermissions.Administrator)
            .WithName("PreviewFalseLeaveRepair")
            .WithSummary("Preview the false leave repair")
            .WithDescription(
                "What the repair would remove, counted from the same selection the repair uses. "
                + "Nothing is changed.")
            .Produces<FalseLeavePreview>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("", async (
                [FromServices] FalseLeaveRepair repair,
                CancellationToken ct) =>
            {
                var result = await repair.RunAsync(ct);

                return result.Ran
                    ? Results.Ok(result)
                    : Results.Conflict(new { error = "The repair has already run." });
            })
            .RequiresFlag(ModbotPermissions.Administrator)
            .WithName("RunFalseLeaveRepair")
            .WithSummary("Run the false leave repair")
            .WithDescription(
                "Removes the false leave facts (and the rows that name them), after saving a copy in "
                + "three backup tables, then works the daily totals out again for the days they touched. "
                + "Runs once: the backup tables are the record, and a second call is refused with 409.")
            .Produces<FalseLeaveRepairResult>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }
}
