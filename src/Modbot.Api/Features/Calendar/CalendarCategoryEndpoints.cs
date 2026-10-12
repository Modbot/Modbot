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
                    [.. categories.Select(c => new CalendarCategoryDetailView(c.Id, c.Name, used.GetValueOrDefault(c.Id)))],
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

                var now = clock.UtcNow;
                var category = new CalendarCategory
                {
                    Id = Guid.CreateVersion7(),
                    Name = name,
                    CreatedByUserId = ModbotAuth.UserIdOf(http.User),
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.CalendarCategories.Add(category);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.CalendarCategoryCreated, category.Id.ToString(), Actor.Of(http), new JsonObject { ["name"] = name }, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(new CalendarCategoryDetailView(category.Id, category.Name, 0));
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

                if (before != name)
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);

                    category.Name = name;
                    category.UpdatedAt = clock.UtcNow;
                    await db.SaveChangesAsync(ct);

                    await facts.RecordAsync(
                        FactType.CalendarCategoryChanged,
                        category.Id.ToString(),
                        Actor.Of(http),
                        new JsonObject { ["name"] = name, ["before"] = before },
                        ct);

                    await transaction.CommitAsync(ct);
                }

                return Results.Ok(new CalendarCategoryDetailView(category.Id, category.Name, await UsedByAsync(db, id, ct)));
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
