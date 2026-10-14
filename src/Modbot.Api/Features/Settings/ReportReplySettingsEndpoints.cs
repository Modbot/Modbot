using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <summary>A ready-made message for the person who made a report.</summary>
/// <param name="Name">What staff pick it by in the close form.</param>
/// <param name="Text">What the reporter is sent.</param>
public sealed record ReportReplySettingView(string Name, string Text);

/// <summary>The server's ready-made messages for reporters, in the order the close form lists them.</summary>
public sealed record ReportReplySettingsResponse(IReadOnlyList<ReportReplySettingView> Replies);

/// <param name="Replies">The whole list. Replaces what is stored; an empty list removes them all.</param>
public sealed record ReportReplySettingsUpdate(IReadOnlyList<ReportReplySettingView> Replies);

/// <summary>
/// Settings → Discord → Report replies: the messages staff can pick from when they close a report and
/// tell the reporter. Staff can also write their own words in the close form.
/// </summary>
public static class ReportReplySettingsEndpoints
{
    public static IEndpointRouteBuilder MapReportReplySettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/report-replies").WithTags("Settings");

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(View((await db.GetSettingsAsync(ct)).MemberReportReplies)))
            .WithName("GetReportReplySettings")
            .WithSummary("Get the ready-made messages for reporters")
            .WithDescription("The messages staff can send the reporter when they close a report.")
            .Produces<ReportReplySettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] ReportReplySettingsUpdate body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var replies = (body.Replies ?? [])
                    .Select(r => new ReportReply((r.Name ?? string.Empty).Trim(), (r.Text ?? string.Empty).Trim()))
                    .ToList();

                if (ReportReplies.Problem(replies) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var settings = await db.GetSettingsAsync(ct);
                var stored = ReportReplies.Write(replies);

                var change = new SettingsChange("reportReplies")
                    .Field("replies", ReportReplies.Write(ReportReplies.Read(settings.MemberReportReplies)), stored);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.MemberReportReplies = stored;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(stored));
            })
            .WithName("SetReportReplySettings")
            .WithSummary("Update the ready-made messages for reporters")
            .WithDescription("Replace the list. At most 10 messages, each with a name (40 characters) and text (1,000 characters), and no two with the same name.")
            .Produces<ReportReplySettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static ReportReplySettingsResponse View(string stored)
        => new([.. ReportReplies.Read(stored).Select(r => new ReportReplySettingView(r.Name, r.Text))]);
}
