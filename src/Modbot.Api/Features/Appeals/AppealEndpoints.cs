using System.Globalization;
using System.Text;
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

namespace Modbot.Api.Features.Appeals;

/// <summary>
/// Appeals against bans and the yearly report (batch 3, step G): staff only, no public page.
/// </summary>
/// <remarks>
/// Reading, and the report, need <see cref="ModbotPermissions.ViewProfile"/>, as case files do.
/// Logging and deciding an appeal need <see cref="ModbotPermissions.Unban"/>; the service checks
/// that, because it also decides what the reader may press. The person's id travels in the query
/// string or the body, never in the path (foundation §3.1.1). Every parameter is explicitly
/// attributed, for the reason the other features give.
/// </remarks>
public static class AppealEndpoints
{
    public static IEndpointRouteBuilder MapAppeals(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/appeals").WithTags("Appeals").RequireAuthorization();

        group.MapGet("/", async (
                HttpContext http,
                [FromQuery] string? userId,
                [FromQuery] int? year,
                [FromQuery] string? outcome,
                [FromQuery] int? limit,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                return await Answer(() => new AppealService(db, clock)
                    .ListAsync(userId, year, outcome, limit ?? AppealService.DefaultLimit, caller, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("ListAppeals")
            .WithSummary("List appeals")
            .WithDescription(
                "Appeals staff logged, newest received first. `userId` narrows it to one VRChat "
                + "account, `year` to appeals received in that year, `outcome` (pending, upheld or "
                + "turned-down) to those that wait or ended that way. Returns at most `limit` "
                + "(default 50, at most 200); `total` and `waiting` count every match.")
            .Produces<AppealList>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] LogAppealRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                return await Answer(() => new AppealService(db, clock, facts, partitions).LogAsync(body, caller, ct));
            })
            .RequiresFlag(ModbotPermissions.Unban)
            .WithName("LogAppeal")
            .WithSummary("Log an appeal")
            .WithDescription(
                "Writes down that a banned person appealed. `receivedOn` defaults to today and may "
                + "not be a day to come; `caseFileId` links the ban's case file, which must be about "
                + $"the same person; the note is at most {Appeal.MaxNoteLength} characters. The appeal "
                + "is one entry in the moderation log. Nothing is changed on VRChat.")
            .Produces<AppealView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id:guid}/decide", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] DecideAppealRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (CallerOf(http) is not { } caller)
                    return Results.Forbid();

                return await Answer(() => new AppealService(db, clock, facts, partitions).DecideAsync(id, body, caller, ct));
            })
            .RequiresFlag(ModbotPermissions.Unban)
            .WithName("DecideAppeal")
            .WithSummary("Uphold or turn down an appeal")
            .WithDescription(
                "`outcome` is `upheld` or `turned-down`. Records the decision, who made it and when, "
                + "once: an appeal that was decided cannot be decided again. Upholding does not "
                + "unban anybody; lift the ban the usual way.")
            .Produces<AppealView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/report", async (
                [FromQuery] int? year,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
                await Answer(() => new YearlyReportQuery(db, clock).RunAsync(year, ct)))
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("GetYearlyReport")
            .WithSummary("Get the yearly report")
            .WithDescription(
                "One calendar year (UTC days; the current year by default): warnings, instance "
                + "kicks, removals from the group, bans and unbans, summed from the same daily "
                + "totals the Team page uses, then the appeals received in the year and those "
                + "upheld and turned down in it. `countedSince` is the first day Modbot has "
                + "anything counted; a year starting before it is a partial year.")
            .Produces<YearlyReport>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/report.csv", async (
                [FromQuery] int? year,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                try
                {
                    var report = await new YearlyReportQuery(db, clock).RunAsync(year, ct);

                    return Results.File(
                        Encoding.UTF8.GetBytes(YearlyReportQuery.Csv(report)),
                        "text/csv; charset=utf-8",
                        $"yearly-report-{report.Year.ToString(CultureInfo.InvariantCulture)}.csv");
                }
                catch (AppealRefused refused)
                {
                    return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
                }
            })
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("DownloadYearlyReport")
            .WithSummary("Download the yearly report")
            .WithDescription("The yearly report for `year` as a CSV file, one row per number.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> Answer<T>(Func<Task<T>> run)
    {
        try
        {
            return Results.Ok(await run());
        }
        catch (AppealRefused refused)
        {
            return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
        }
    }

    private static Caller? CallerOf(HttpContext http)
        => ModbotAuth.UserIdOf(http.User) is { } id
            ? new Caller(id, http.User.Identity?.Name ?? string.Empty, ModbotAuth.PermissionsOf(http.User))
            : null;
}
