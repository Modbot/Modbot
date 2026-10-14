using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// The categories admins make in Modbot for events, on top of the fixed list VRChat gives an event.
/// An event carries any number of them; Discord's posts and event details show them.
/// </summary>
/// <remarks>
/// Under the calendar's permissions: See calendar to look, Manage calendar to change. Nothing here
/// asks VRChat or Discord anything: the places an event goes pick up a new name on their next pass.
/// </remarks>
public static class CalendarCategoryEndpoints
{
    public static IEndpointRouteBuilder MapCalendarCategories(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/calendar/categories").WithTags("Calendar");

        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var categories = await db.CalendarCategories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

                var used = await db.CalendarEvents.AsNoTracking()
                    .Where(e => e.DeletedAt == null)
                    .SelectMany(e => e.Categories)
                    .GroupBy(c => c.Id)
                    .Select(g => new { Id = g.Key, Events = g.Count() })
                    .ToDictionaryAsync(g => g.Id, g => g.Events, ct);

                return Results.Ok(new CalendarCategoriesView(
                    [.. categories.Select(c => DetailOf(c, used.GetValueOrDefault(c.Id)))],
                    ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ManageCalendar)));
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar)
            .WithName("ListCalendarCategories")
            .WithSummary("List event categories")
            .WithDescription("Every category made in Modbot by name, with how many events carry it. VRChat's own categories are not in it.")
            .Produces<CalendarCategoriesView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] CalendarCategoryRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var name = body.Name?.Trim() ?? string.Empty;
                if (await NameProblemAsync(db, name, except: null, ct) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var tag = TagFor(name, body.BlueskyTag, keep: null, out var tagProblem);
                if (tagProblem is not null)
                    return Results.BadRequest(new { error = tagProblem });

                var now = clock.UtcNow;
                var category = new CalendarCategory
                {
                    Id = Guid.CreateVersion7(),
                    Name = name,
                    BlueskyTag = tag,
                    CreatedByUserId = ModbotAuth.UserIdOf(http.User),
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.CalendarCategories.Add(category);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.CalendarCategoryCreated,
                    category.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject { ["name"] = name, ["blueskyTag"] = BlueskyTags.ForCategory(name, tag) },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(DetailOf(category, 0));
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("CreateCalendarCategory")
            .WithSummary("Add event category")
            .WithDescription("Make a category, such as Karaoke, that events can carry. Names are up to 40 characters and not repeated, whatever the letter case.")
            .Produces<CalendarCategoryDetailView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] CalendarCategoryRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var category = await db.CalendarCategories.FirstOrDefaultAsync(c => c.Id == id, ct);
                if (category is null)
                    return Results.NotFound();

                var name = body.Name?.Trim() ?? string.Empty;
                if (await NameProblemAsync(db, name, except: id, ct) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var before = category.Name;
                var tagBefore = BlueskyTags.ForCategory(before, category.BlueskyTag);

                // Kept as it is when none was sent. A tag that is the name's own stays "made from the name".
                var tag = TagFor(name, body.BlueskyTag, keep: category.BlueskyTag, out var tagProblem);
                if (tagProblem is not null)
                    return Results.BadRequest(new { error = tagProblem });

                if (before != name || tag != category.BlueskyTag)
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);

                    category.Name = name;
                    category.BlueskyTag = tag;
                    category.UpdatedAt = clock.UtcNow;
                    await db.SaveChangesAsync(ct);

                    var tagAfter = BlueskyTags.ForCategory(name, tag);

                    var change = new JsonObject { ["name"] = name, ["before"] = before };
                    if (tagAfter != tagBefore)
                    {
                        change["blueskyTag"] = tagAfter;
                        change["blueskyTagBefore"] = tagBefore;
                    }

                    await facts.RecordAsync(FactType.CalendarCategoryChanged, category.Id.ToString(), Actor.Of(http), change, ct);

                    await transaction.CommitAsync(ct);
                }

                return Results.Ok(DetailOf(category, await UsedByAsync(db, id, ct)));
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("UpdateCalendarCategory")
            .WithSummary("Rename event category")
            .WithDescription("Rename a category. Every event that carries it shows the new name; Discord's posts follow on their next update.")
            .Produces<CalendarCategoryDetailView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                var category = await db.CalendarCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
                if (category is null)
                    return Results.NotFound();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var usedBy = await UsedByAsync(db, id, ct);

                // The database lets go of the category on every event that carried it.
                await db.CalendarCategories.Where(c => c.Id == id).ExecuteDeleteAsync(ct);

                await facts.RecordAsync(
                    FactType.CalendarCategoryDeleted,
                    category.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject { ["name"] = category.Name, ["events"] = usedBy },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("DeleteCalendarCategory")
            .WithSummary("Delete event category")
            .WithDescription("Delete a category. Events that carried it keep everything else, and Discord's posts drop its name on their next update.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static CalendarCategoryDetailView DetailOf(CalendarCategory category, int usedBy) =>
        new(category.Id, category.Name, usedBy, BlueskyTags.ForCategory(category.Name, category.BlueskyTag), category.BlueskyTag is not null);

    /// <summary>
    /// The tag to keep for a category named <paramref name="name"/> when <paramref name="typed"/> was
    /// sent: null keeps <paramref name="keep"/>; the tag the name makes is kept as null, so a rename
    /// carries it along; empty is none.
    /// </summary>
    internal static string? TagFor(string name, string? typed, string? keep, out string? problem)
    {
        problem = null;

        if (typed is null)
            return keep;

        var tag = BlueskyTags.Clean(typed);

        if (BlueskyTags.IsTooLong(tag))
        {
            problem = BlueskyTags.TooLong;
            return keep;
        }

        return tag == BlueskyTags.FromName(name) ? null : tag;
    }

    /// <summary>What is wrong with a category's name, or null. <paramref name="except"/> is the category being renamed.</summary>
    private static async Task<string?> NameProblemAsync(ModbotContext db, string name, Guid? except, CancellationToken ct)
    {
        if (name.Length == 0)
            return "Give the category a name.";

        if (name.Length > CalendarCategory.MaxNameLength)
            return $"The name is longer than {CalendarCategory.MaxNameLength} characters.";

        var lower = name.ToLowerInvariant();

        return await db.CalendarCategories.AnyAsync(c => c.Id != except && c.Name.ToLower() == lower, ct)
            ? "There is a category with that name already."
            : null;
    }

    private static Task<int> UsedByAsync(ModbotContext db, Guid id, CancellationToken ct) =>
        db.CalendarEvents.CountAsync(e => e.DeletedAt == null && e.Categories.Any(c => c.Id == id), ct);
}
