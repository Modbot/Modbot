using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Posts;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Net;
using Modbot.Core.Posts;
using Modbot.Core.Profiles;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Spotlight;

/// <summary>A member staff can pick for the spotlight.</summary>
public sealed record SpotlightMemberView(string Id, string Name);

/// <summary>A spotlight draft waiting for staff to pick its member.</summary>
/// <param name="Id">The week's spotlight.</param>
/// <param name="Week">The Monday of its week, <c>yyyy-MM-dd</c>.</param>
/// <param name="PostId">The Marketing draft.</param>
public sealed record SpotlightPendingView(Guid Id, string Week, Guid PostId);

/// <param name="Pending">The newest draft with no member yet, or null when none waits.</param>
/// <param name="Members">The members who opted in and are still in the server, by name. Empty when nothing waits.</param>
public sealed record SpotlightView(SpotlightPendingView? Pending, IReadOnlyList<SpotlightMemberView> Members);

/// <param name="DiscordUserId">The member picked. Only a member who opted in is taken.</param>
public sealed record SpotlightPickRequest(string? DiscordUserId);

/// <summary>
/// The spotlight on the Marketing page (spotlight design §3): a draft made in staff mode waits for
/// staff to pick its member from the members who opted in. Picking fills the draft with their card;
/// the note is written and the post sent from Marketing as for any post.
/// </summary>
public static class SpotlightEndpoints
{
    public const string NotFound = "That spotlight does not exist.";
    public const string AlreadyPicked = "A member is already picked.";
    public const string NotOptedIn = "That member has not opted in.";

    public static IEndpointRouteBuilder MapSpotlight(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/spotlight").WithTags("Posts");

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var pending = await (
                        from w in db.SpotlightWeeks.AsNoTracking()
                        join p in db.Posts.AsNoTracking() on w.PostId equals (Guid?)p.Id
                        where w.DiscordUserId == null && p.Kind == PostKinds.Spotlight && p.Status == PostStatuses.Draft
                        orderby w.Week descending
                        select new { w.Id, w.Week, PostId = p.Id })
                    .FirstOrDefaultAsync(ct);

                if (pending is null)
                    return Results.Ok(new SpotlightView(null, []));

                var members = await MemberSpotlight.OptedInAsync(db, ct);

                return Results.Ok(new SpotlightView(
                    new SpotlightPendingView(pending.Id, pending.Week.ToString("yyyy-MM-dd"), pending.PostId),
                    [.. members.Select(m => new SpotlightMemberView(m.UserId, m.Name))]));
            })
            .RequiresFlag(ModbotPermissions.ViewPosts)
            .WithName("GetSpotlight")
            .WithSummary("Get the spotlight waiting for a member")
            .WithDescription(
                "The newest spotlight draft that has no member yet (staff mode), with the members who "
                + "opted in to pick from. `pending` is null when none waits.")
            .Produces<SpotlightView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/{id:guid}/member", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] SpotlightPickRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var week = await db.SpotlightWeeks.FirstOrDefaultAsync(w => w.Id == id, ct);
                if (week is null || week.PostId is not { } postId)
                    return Results.NotFound(new { error = NotFound });

                if (week.DiscordUserId is not null)
                    return Conflict(AlreadyPicked);

                var post = await db.Posts.Include(p => p.Destinations).FirstOrDefaultAsync(p => p.Id == postId, ct);
                if (post is null || post.Kind != PostKinds.Spotlight)
                    return Results.NotFound(new { error = NotFound });

                if (PostRules.CannotEdit(post) is { } cannot)
                    return Conflict(cannot);

                var member = (await MemberSpotlight.OptedInAsync(db, ct))
                    .FirstOrDefault(m => string.Equals(m.UserId, body.DiscordUserId, StringComparison.Ordinal));

                if (member is null)
                    return Results.BadRequest(new { error = NotOptedIn });

                var now = clock.UtcNow;
                var before = PostRequests.Describe(post);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                await SpotlightPosts.FillAsync(db, post, member, PictureLinks.Shared, now, ct);
                week.DiscordUserId = member.UserId;
                week.PickedAt = now;

                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Conflict(PostEndpoints.ChangedElsewhere);
                }

                var changed = PostRequests.Changed(before, PostRequests.Describe(post));
                if (changed.Count > 0)
                {
                    await facts.RecordAsync(
                        FactType.PostChanged, post.Id.ToString(), Actor.Of(http), new JsonObject { ["title"] = post.Title, ["changed"] = changed }, ct);
                }

                await transaction.CommitAsync(ct);

                return Results.Ok(new SpotlightPendingView(week.Id, week.Week.ToString("yyyy-MM-dd"), post.Id));
            })
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("PickSpotlightMember")
            .WithSummary("Pick the spotlight's member")
            .WithDescription(
                "Picks the member for a spotlight draft that has none (staff mode) and fills the draft "
                + "with their card: their name, their avatar and the answers they opened to Members, above any text already there. "
                + "Only a member who opted in and is still in the server is taken. The note is written "
                + "and the post sent from Marketing as for any post. Refused (409) once a member is "
                + "picked, and once the draft has gone out or is being sent.")
            .Produces<SpotlightPendingView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static IResult Conflict(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict);
}
