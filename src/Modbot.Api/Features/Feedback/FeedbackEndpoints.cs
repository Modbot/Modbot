using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Feedback;

/// <summary>
/// The Feedback page: what staff sent with the Feedback button, for the admins to read and mark.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Admins only, and nothing leaves this server.</strong> Listing, opening and changing the
/// status all need <see cref="ModbotPermissions.ReviewFeedback"/>. Sending is not here: it is the
/// companion's endpoint, which needs <see cref="ModbotPermissions.SendFeedback"/> (feedback design §5).
/// </para>
/// <para>
/// A status change is recorded as a fact naming the status and never the title or the details, so the
/// log holds no copy of the words.
/// </para>
/// </remarks>
public static class FeedbackEndpoints
{
    public static IEndpointRouteBuilder MapFeedback(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/feedback").WithTags("Feedback");

        group.MapGet("/", async (
                [FromQuery] string? status,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var wanted = status?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(wanted) && !FeedbackStatuses.All.Contains(wanted))
                    return Results.BadRequest(new { error = "The status must be new, reviewed, done or dropped." });

                var query = db.Feedback.AsNoTracking();
                if (!string.IsNullOrEmpty(wanted))
                    query = query.Where(f => f.Status == wanted);

                var items = await query
                    .OrderByDescending(f => f.CreatedAt)
                    .Select(f => new FeedbackSummary(f.Id, f.CreatedAt, f.SenderName, f.Kind, f.Title, f.Status))
                    .ToListAsync(ct);

                return Results.Ok(new FeedbackListView(items));
            })
            .RequiresFlag(ModbotPermissions.ReviewFeedback)
            .WithName("ListFeedback")
            .WithSummary("List feedback")
            .WithDescription("Every feedback item, newest first. status limits the list to new, reviewed, done or dropped.")
            .Produces<FeedbackListView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var item = await db.Feedback.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);

                return item is null ? Results.NotFound() : Results.Ok(View(item));
            })
            .RequiresFlag(ModbotPermissions.ReviewFeedback)
            .WithName("GetFeedback")
            .WithSummary("Get feedback")
            .WithDescription("One feedback item, in full.")
            .Produces<FeedbackView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}/status", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] FeedbackStatusRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var status = body.Status?.Trim().ToLowerInvariant();
                if (status is null || !FeedbackStatuses.All.Contains(status))
                    return Results.BadRequest(new { error = "The status must be new, reviewed, done or dropped." });

                var item = await db.Feedback.FirstOrDefaultAsync(f => f.Id == id, ct);
                if (item is null)
                    return Results.NotFound();

                if (item.Status == status)
                    return Results.Ok(View(item));

                var actor = Actor.Of(http);

                item.Status = status;
                item.StatusChangedAt = clock.UtcNow;
                item.StatusChangedBy = actor?.Username;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.FeedbackStatusChanged, item.Id.ToString(), actor,
                    new JsonObject { ["status"] = status }, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(View(item));
            })
            .RequiresFlag(ModbotPermissions.ReviewFeedback)
            .WithName("SetFeedbackStatus")
            .WithSummary("Set feedback status")
            .WithDescription("Mark an item new, reviewed, done or dropped. A refusal (400) says what is wrong in error.")
            .Produces<FeedbackView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static FeedbackView View(FeedbackItem item) => new(
        item.Id, item.CreatedAt, item.SenderUserId, item.SenderName, item.Kind, item.Title, item.Details,
        item.Status, item.StatusChangedAt, item.StatusChangedBy);
}
