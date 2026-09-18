using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Reviews;
using Modbot.Api.Auth;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Flags;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Reviews;

/// <summary>
/// The reviews that open when a moderator's pattern looks unusual (spec 5.8.5): the list, the
/// count for the nav badge, and closing one with a note.
/// </summary>
/// <remarks>
/// <para>
/// Everything here needs <see cref="ModbotPermissions.ReviewTickets"/>, which is deliberately not
/// one of the moderation flags: the people being reviewed should not be the people closing the
/// reviews (M4 design §8.3).
/// </para>
/// <para>
/// Every parameter is explicitly attributed, for the reason the other read endpoints give: an
/// unattributed concrete type on a GET is bound as the body and throws while the route is mapped.
/// </para>
/// </remarks>
public static class ReviewEndpoints
{
    public const int MaxNoteLength = 2000;

    public static IEndpointRouteBuilder MapReviews(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/reviews").WithTags("Reviews").RequireAuthorization();

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] string? state,
                CancellationToken ct) =>
            {
                var wanted = (state ?? "open").ToLowerInvariant();
                if (wanted is not ("open" or "closed" or "all"))
                    return Results.BadRequest(new { error = "state must be open, closed or all." });

                var query = db.Reviews.AsNoTracking();
                if (wanted == "open")
                    query = query.Where(r => r.State == ReviewState.Open);
                else if (wanted == "closed")
                    query = query.Where(r => r.State == ReviewState.Closed);

                // Open ones oldest first -- the one that has waited longest is the one to look at;
                // closed ones newest first, because the recent decision is the one people check.
                var rows = wanted == "closed"
                    ? await query.OrderByDescending(r => r.ClosedAt).ThenBy(r => r.Id).Take(500).ToListAsync(ct)
                    : await query.OrderBy(r => r.State).ThenBy(r => r.OpenedAt).ThenBy(r => r.Id).Take(500).ToListAsync(ct);

                var openCount = await db.Reviews.AsNoTracking().CountAsync(r => r.State == ReviewState.Open, ct);
                var lastRun = await db.ReviewRunState.AsNoTracking().Where(s => s.Id == 1).Select(s => s.UpdatedAt).FirstOrDefaultAsync(ct);

                var ids = rows.Select(r => r.ModeratorId)
                    .Concat(rows.Where(r => r.Signal == ReviewSignal.SamePerson).Select(r => r.About))
                    .ToList();
                var names = await PeopleNames.LookupAsync(db, ids, ct);

                return Results.Ok(new ReviewListResponse(
                    rows.Select(r => View(r, names)).ToList(),
                    openCount,
                    lastRun,
                    clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ReviewTickets)
            .WithName("ListReviews")
            .WithSummary("Reviews of a moderator's pattern: open by default, or closed, or all")
            .WithDescription(
                "Detection opens a review when one moderator keeps acting on one person across "
                + "instances where nobody else has, or does far more in a day than the rest of the "
                + "team. Every review carries the numbers it was opened on and the fact ids behind "
                + "them. It is a question for a person, never a finding: closing one records who "
                + "answered and what they said, and is itself a fact.")
            .Produces<ReviewListResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/open-count", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
                Results.Ok(new OpenReviewCount(
                    await db.Reviews.AsNoTracking().CountAsync(r => r.State == ReviewState.Open, ct))))
            .RequiresFlag(ModbotPermissions.ReviewTickets)
            .WithName("CountOpenReviews")
            .WithSummary("How many reviews are waiting -- the number on the nav badge")
            .Produces<OpenReviewCount>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/{id:guid}/close", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] CloseReviewRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] ReviewFacts? facts,
                [FromServices] IFactWriter flagFacts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                var note = body.Note?.Trim() ?? string.Empty;
                if (note.Length == 0)
                    return Results.BadRequest(new { error = "A note is required: say what you concluded." });
                if (note.Length > MaxNoteLength)
                    return Results.BadRequest(new { error = $"The note is too long (at most {MaxNoteLength} characters)." });

                if (facts is null)
                    return Results.Problem("Review records are not available in this process.", statusCode: 503);

                // A close with no author is not a close. The cookie always carries the account id,
                // so this guards against a misconfigured host rather than a user error.
                if (ModbotAuth.UserIdOf(http.User) is not { } userId)
                    return Results.Forbid();

                var review = await db.Reviews.FirstOrDefaultAsync(r => r.Id == id, ct);
                if (review is null)
                    return Results.NotFound(new { error = "No such review." });
                if (review.State == ReviewState.Closed)
                    return Results.Conflict(new { error = "This review is already closed." });

                var outcome = string.IsNullOrWhiteSpace(body.Outcome) ? null : body.Outcome.Trim().ToLowerInvariant();
                var flagReview = review.Signal == ReviewSignal.AiFlag;

                // Only a flag's review asks what you concluded, because only there does the answer
                // change something (AI moderation design §19).
                if (flagReview && !ReviewOutcome.IsOutcome(outcome))
                    return Results.BadRequest(new { error = "Say whether the rule was right or wrong." });
                if (!flagReview && outcome is not null)
                    return Results.BadRequest(new { error = "This kind of review has no right or wrong." });

                var flag = flagReview && Guid.TryParse(review.About, out var flagId)
                    ? await db.ModerationFlags.FirstOrDefaultAsync(f => f.Id == flagId, ct)
                    : null;

                var username = http.User.Identity?.Name ?? string.Empty;
                var now = clock.UtcNow;

                await partitions.EnsureForAsync(now, ct);

                // The row and the fact commit together: a review closed with no record of who
                // closed it is the failure spec 5.8 exists to prevent.
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                review.State = ReviewState.Closed;
                review.ClosedAt = now;
                review.ClosedByUserId = userId;
                review.ClosedByUsername = username;
                review.Note = note;
                review.Outcome = outcome;

                // The flag goes with its review: closing it as wrong is a dismissal, and a
                // dismissal stops that rule and term flagging this person ever again.
                if (flag is { State: ModerationFlagState.Open })
                {
                    if (outcome == ReviewOutcome.Wrong)
                        await FlagDecisions.DismissAsync(flagFacts, flag, userId, username, now, ct);
                    else
                        await FlagDecisions.ConfirmAsync(flagFacts, flag, userId, username, now, ct);
                }

                await db.SaveChangesAsync(ct);

                await facts.ClosedAsync(review, userId, username, ct);
                await transaction.CommitAsync(ct);

                var names = await PeopleNames.LookupAsync(db, [review.ModeratorId, review.About], ct);
                return Results.Ok(View(review, names));
            })
            .RequiresFlag(ModbotPermissions.ReviewTickets)
            .WithName("CloseReview")
            .WithSummary("Close a review with a note saying what you concluded")
            .WithDescription(
                "The note is required and is kept with the review and in the fact log against your "
                + "account. Closing does not judge the moderator either way; it records that a "
                + "person looked. Detection will not reopen a closed review on the same facts -- "
                + "only on new ones after it was closed.")
            .Produces<ReviewView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    internal static async Task<ReviewThresholds> ThresholdsAsync(ModbotContext db, CancellationToken ct)
        => ReviewThresholds.Read(await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ReviewThresholds)
            .FirstOrDefaultAsync(ct));

    private static ReviewView View(Review review, IReadOnlyDictionary<string, string> names)
    {
        var platform = review.ModeratorPlatform.ToString().ToLowerInvariant();

        return new ReviewView(
            review.Id,
            new Person(platform, review.ModeratorId, names.GetValueOrDefault(review.ModeratorId)),
            review.Signal,
            ReviewSignals.Label(review.Signal),
            review.About,
            review.Signal == ReviewSignal.SamePerson
                ? new Person(platform, review.About, names.GetValueOrDefault(review.About))
                : null,
            review.WindowStart,
            review.WindowEnd,
            review.Summary,
            JsonDocument.Parse(review.Evidence).RootElement.Clone(),
            review.State.ToString(),
            review.OpenedAt,
            review.UpdatedAt,
            review.ClosedAt,
            review.ClosedByUsername,
            review.Note,
            review.Outcome);
    }
}
