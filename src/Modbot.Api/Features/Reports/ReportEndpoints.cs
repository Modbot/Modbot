using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Reports;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Reports;

/// <summary>
/// What members told the mods with <c>/report</c> or Report to mods: the list, the count for the nav
/// badge, and closing one with a note (Discord commands design §3.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two permissions.</strong> See reports reads and counts; Handle reports closes. Closing
/// needs both, because a person who cannot read a report cannot have a reason to close it.
/// </para>
/// <para>
/// <strong>A report about a staff account is shown only to people with Review tickets</strong> (M4
/// design §8.3): the people being reviewed should not be the people closing the reviews. Everything
/// here goes through <see cref="MemberReportAccess.VisibleAsync"/>, so the list, the count and the
/// close agree, and a report the caller may not see is answered as if it did not exist.
/// </para>
/// <para>
/// Everyone who may see reports sees who reported (decision 2). Nothing else does.
/// </para>
/// </remarks>
public static class ReportEndpoints
{
    /// <summary>Most reports one list returns.</summary>
    public const int MostListed = 500;

    public static IEndpointRouteBuilder MapReports(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/reports").WithTags("Reports").RequireAuthorization();

        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] string? state,
                CancellationToken ct) =>
            {
                var wanted = (state ?? MemberReportStates.Open).ToLowerInvariant();
                if (wanted is not (MemberReportStates.Open or MemberReportStates.Closed))
                    return Results.BadRequest(new { error = "state must be open or closed." });

                var now = clock.UtcNow;
                var visible = await MemberReportAccess.VisibleAsync(db, ModbotAuth.PermissionsOf(http.User), now, ct);

                // Open ones oldest first: the one that has waited longest is the one to answer.
                // Closed ones newest first, because the recent decision is the one people check.
                var rows = wanted == MemberReportStates.Closed
                    ? await visible.Where(r => r.State == MemberReportStates.Closed)
                        .OrderByDescending(r => r.ClosedAt).ThenBy(r => r.Id).Take(MostListed).ToListAsync(ct)
                    : await visible.Where(r => r.State == MemberReportStates.Open)
                        .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Take(MostListed).ToListAsync(ct);

                var openCount = await visible.CountAsync(r => r.State == MemberReportStates.Open, ct);

                return Results.Ok(new MemberReportList(rows.Select(View).ToList(), openCount, now));
            })
            .RequiresFlag(ModbotPermissions.ViewReports)
            .WithName("ListReports")
            .WithSummary("List member reports")
            .WithDescription(
                "What members told the mods with /report or Report to mods: open by default, or closed. "
                + "Each report names who reported, who it is about, what was written and the message "
                + "quoted. A report about a staff account is left out unless the caller holds Review "
                + "tickets. Returns at most 500: the oldest open ones, or the most recently closed.")
            .Produces<MemberReportList>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/open-count", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var visible = await MemberReportAccess.VisibleAsync(db, ModbotAuth.PermissionsOf(http.User), clock.UtcNow, ct);
                return Results.Ok(new OpenReportCount(await visible.CountAsync(r => r.State == MemberReportStates.Open, ct)));
            })
            .RequiresFlag(ModbotPermissions.ViewReports)
            .WithName("CountOpenReports")
            .WithSummary("Count open member reports")
            .WithDescription("How many reports the caller may see are waiting -- the number on the nav badge.")
            .Produces<OpenReportCount>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/{id:guid}/close", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] CloseReportRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] MemberReports reports,
                CancellationToken ct) =>
            {
                if (ModbotAuth.UserIdOf(http.User) is not { } userId)
                    return Results.Forbid();

                // A report the caller may not see is not found, the same as one that is not there.
                var visible = await MemberReportAccess.VisibleAsync(db, ModbotAuth.PermissionsOf(http.User), clock.UtcNow, ct);
                if (!await visible.AnyAsync(r => r.Id == id, ct))
                    return Results.NotFound(new { error = "No such report." });

                var report = await db.MemberReports.FirstOrDefaultAsync(r => r.Id == id, ct);
                var result = await reports.CloseAsync(report, body?.Note, userId, http.User.Identity?.Name ?? string.Empty, ct);

                return result switch
                {
                    MemberReportCloseResult.Closed => Results.Ok(View(report!)),
                    MemberReportCloseResult.NoNote => Results.BadRequest(new { error = "A note is required: say what was done." }),
                    MemberReportCloseResult.NoteTooLong => Results.BadRequest(new { error = $"The note is too long (at most {MemberReport.MaxCloseNoteLength} characters)." }),
                    MemberReportCloseResult.AlreadyClosed => Results.Conflict(new { error = "This report is already closed." }),
                    _ => Results.NotFound(new { error = "No such report." }),
                };
            })
            .RequiresFlag(ModbotPermissions.ViewReports | ModbotPermissions.HandleReports)
            .WithName("CloseReport")
            .WithSummary("Close a member report")
            .WithDescription(
                "The note is required and is kept with the report. Closing is recorded as a fact against "
                + "your account that names the report and not the note or the reporter.")
            .Produces<MemberReportView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    internal static MemberReportView View(MemberReport r)
        => new(
            r.Id,
            r.State,
            r.CreatedAt,
            new ReportPerson(r.ReporterDiscordId, r.ReporterName),
            new ReportPerson(r.ReportedDiscordId, r.ReportedName, r.ReportedVRChatUserId),
            r.Text,
            r.MessageUrl is null && r.MessageText is null
                ? null
                : new ReportMessageView(
                    r.MessageChannelId,
                    r.MessageChannelName,
                    r.MessageSentAt,
                    r.MessageText,
                    r.MessageAttachments ?? [],
                    r.MessageUrl),
            r.TextRemovedAt,
            r.ClosedAt,
            r.ClosedByUsername,
            r.CloseNote);
}
