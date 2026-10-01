using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// Picking an event's world from its world list by hand: Pick again on a date, and Next game during
/// the event (world lists design §5, §6).
/// </summary>
/// <remarks>
/// Nothing here asks VRChat anything or opens an instance. The people count is the head count the
/// group instance poll already keeps.
/// </remarks>
public static class WorldPickEndpoints
{
    /// <summary>
    /// How long before the event opens a group instance in a picked world may have been opened and
    /// still count as the event's: somebody opening it by hand a little early.
    /// </summary>
    public static readonly TimeSpan OpenedEarly = TimeSpan.FromHours(1);

    public static IEndpointRouteBuilder MapWorldPicks(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/calendar").WithTags("Calendar");

        group.MapPost("/events/{id:guid}/pick-again", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var calendarEvent = await db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);
                if (calendarEvent is null)
                    return Results.NotFound();

                if (calendarEvent.WorldListId is null)
                    return Results.Conflict(new { error = "This event does not pick from a world list." });

                // Once the date is open, its instance may already be open in that world.
                if (calendarEvent.State != CalendarEventStates.Scheduled)
                    return Results.Conflict(new { error = "Only a scheduled date can pick again." });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var result = await new WorldPicker(db, clock).PickAgainAsync(calendarEvent, ModbotAuth.UserIdOf(http.User), ct);

                if (result.Outcome != WorldPickOutcome.Picked)
                    return Results.Conflict(new { error = "There is no other world in the list." });

                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.CalendarWorldPicked,
                    calendarEvent.Id.ToString(),
                    Actor.Of(http),
                    await WorldPicker.FactDataAsync(db, calendarEvent, result, again: true, ct),
                    ct);

                await transaction.CommitAsync(ct);

                var now = clock.UtcNow;
                var views = await CalendarEndpoints.ViewsAsync(db, [calendarEvent], now, now.AddDays(42), ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("PickCalendarWorldAgain")
            .WithSummary("Pick the date's world again")
            .WithDescription(
                "For an event that picks its world from a world list: puts the current date's world back "
                + "and takes the next one in the list's shuffle. Only before the date opens.")
            .Produces<CalendarEventView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapGet("/events/{id:guid}/next-game", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromQuery] Guid? instanceId,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var calendarEvent = await db.CalendarEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);
                if (calendarEvent is null)
                    return Results.NotFound();

                if (Refusal(calendarEvent) is { } refused)
                    return refused;

                return Results.Ok(await ViewAsync(db, http, calendarEvent, instanceId, noneFits: false, clock.UtcNow, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar)
            .WithName("GetNextGame")
            .WithSummary("Get next game")
            .WithDescription(
                "During an open event that picks from a world list: the world picked last as the next "
                + "game, and how many people are in the event's instance now.")
            .Produces<NextGameView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/events/{id:guid}/next-game", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] NextGameRequest? body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                body ??= new NextGameRequest(null, null);

                var calendarEvent = await db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);
                if (calendarEvent is null)
                    return Results.NotFound();

                if (Refusal(calendarEvent) is { } refused)
                    return refused;

                var now = clock.UtcNow;
                var (instance, people, _) = await PeopleAsync(db, calendarEvent, body.InstanceId, now, ct);
                var instead = string.IsNullOrWhiteSpace(body.Instead) ? null : body.Instead.Trim();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var result = await new WorldPicker(db, clock).NextGameAsync(
                    calendarEvent, people, instead, ModbotAuth.UserIdOf(http.User), ct);

                await db.SaveChangesAsync(ct);

                if (result is { Outcome: WorldPickOutcome.Picked })
                {
                    await facts.RecordAsync(
                        FactType.CalendarWorldPicked,
                        calendarEvent.Id.ToString(),
                        Actor.Of(http),
                        await WorldPicker.FactDataAsync(db, calendarEvent, result, again: instead is not null, ct),
                        ct);
                }

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(
                    db, http, calendarEvent, instance, noneFits: result.Outcome == WorldPickOutcome.NoneFits, now, ct));
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("PickNextGame")
            .WithSummary("Pick next game")
            .WithDescription(
                "Picks the next world from the event's world list: the first not played this round whose "
                + "players fit the people in the event's instance now, ignoring players when that count "
                + "is unknown. With instead, that world is put back and the one after it is picked. "
                + "noneFits says nothing fitted; nothing is recorded then. Never opens an instance.")
            .Produces<NextGameView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapGet("/next-game", async (
                HttpContext http,
                [FromQuery] Guid instanceId,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var now = clock.UtcNow;
                var calendarEvent = await EventOfAsync(db, instanceId, now, ct);

                return calendarEvent is null
                    ? Results.NoContent()
                    : Results.Ok(await ViewAsync(db, http, calendarEvent, instanceId, noneFits: false, now, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar)
            .WithName("GetNextGameForInstance")
            .WithSummary("Get next game for an instance")
            .WithDescription(
                "Next game for the open event that picks from a world list and that this instance belongs "
                + "to: the instance Modbot opened for the event's date, or a group instance opened during "
                + "it in a world picked for it. No content when it belongs to none.")
            .Produces<NextGameView>()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>Why an event has no Next game, or null when it has one.</summary>
    private static IResult? Refusal(CalendarEvent calendarEvent)
    {
        if (calendarEvent.WorldListId is null)
            return Results.Conflict(new { error = "This event does not pick from a world list." });

        if (calendarEvent.State != CalendarEventStates.Open || calendarEvent.OccurrenceStartsAt is null)
            return Results.Conflict(new { error = "The event is not open." });

        return null;
    }

    private static async Task<NextGameView> ViewAsync(
        ModbotContext db, HttpContext http, CalendarEvent calendarEvent, Guid? instanceId, bool noneFits, DateTimeOffset now, CancellationToken ct)
    {
        var listId = calendarEvent.WorldListId!.Value;
        var date = calendarEvent.OccurrenceStartsAt!.Value;

        var listName = await db.WorldLists.AsNoTracking()
            .Where(l => l.Id == listId)
            .Select(l => l.Name)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var (instance, people, unsure) = await PeopleAsync(db, calendarEvent, instanceId, now, ct);

        var game = await WorldPicker.StandingFor(db, calendarEvent.Id, date)
            .AsNoTracking()
            .Where(p => p.Kind == WorldPickKinds.Game)
            .FirstOrDefaultAsync(ct);

        NextGameWorldView? shown = null;
        if (game is not null)
        {
            var world = await db.VRChatWorlds.AsNoTracking().FirstOrDefaultAsync(w => w.WorldId == game.WorldId, ct);
            var item = await db.WorldListItems.AsNoTracking()
                .FirstOrDefaultAsync(i => i.ListId == game.ListId && i.WorldId == game.WorldId, ct);

            shown = new NextGameWorldView(
                game.WorldId,
                world?.Name,
                world?.ThumbnailImageUrl ?? world?.ImageUrl,
                item?.MinPlayers,
                item?.MaxPlayers,
                game.PickedAt);
        }

        return new NextGameView(
            calendarEvent.Id,
            calendarEvent.Title,
            listId,
            listName,
            instance,
            people,
            unsure,
            shown,
            noneFits,
            ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ManageCalendar));
    }

    /// <summary>
    /// The event's instance now and how many people are in it (world lists design §6). With an
    /// instance named -- the instance popup's -- that one, when it is one of the event's. Otherwise the
    /// newest open one of the event's.
    /// </summary>
    internal static async Task<(Guid? Instance, int? People, bool Unsure)> PeopleAsync(
        ModbotContext db, CalendarEvent calendarEvent, Guid? instanceId, DateTimeOffset now, CancellationToken ct)
    {
        var instances = await InstancesAsync(db, calendarEvent, now, ct);

        var chosen = instanceId is { } wanted
            ? instances.FirstOrDefault(i => i.Id == wanted)
            : instances.FirstOrDefault();

        chosen ??= instances.FirstOrDefault();

        if (chosen is null)
            return (null, null, false);

        var people = chosen.HeadCount ?? chosen.LastUserCount;
        return (chosen.Id, people, people is not null && chosen.HeadCount is not null && chosen.HeadCountUnsure);
    }

    /// <summary>
    /// The open event, picking from a world list, that an instance belongs to; null when none does.
    /// </summary>
    internal static async Task<CalendarEvent?> EventOfAsync(ModbotContext db, Guid instanceId, DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.DeletedAt == null
                && e.WorldListId != null
                && e.State == CalendarEventStates.Open
                && e.OccurrenceStartsAt != null)
            .ToListAsync(ct);

        foreach (var calendarEvent in open)
        {
            var instances = await InstancesAsync(db, calendarEvent, now, ct);
            if (instances.Any(i => i.Id == instanceId))
                return calendarEvent;
        }

        return null;
    }

    /// <summary>
    /// The event's open instances for its current date, newest first: the one Modbot opened for the
    /// date, and the managed group's instances opened during the date (or the hour before it opened)
    /// in a world picked for it.
    /// </summary>
    private static async Task<List<Core.Data.Entities.VRChatInstance>> InstancesAsync(
        ModbotContext db, CalendarEvent calendarEvent, DateTimeOffset now, CancellationToken ct)
    {
        if (calendarEvent.OccurrenceStartsAt is not { } date)
            return [];

        var occurrence = new CalendarOccurrence(date, date + (calendarEvent.EndsAt - calendarEvent.StartsAt));
        var from = CalendarRepeat.OpensAt(calendarEvent, occurrence) - OpenedEarly;

        var opened = await db.CalendarOpenings.AsNoTracking()
            .Where(o => o.EventId == calendarEvent.Id && o.OccurrenceStartsAt == date && o.InstanceId != null)
            .Select(o => o.InstanceId)
            .FirstOrDefaultAsync(ct);

        var worlds = await WorldPicker.StandingFor(db, calendarEvent.Id, date)
            .Select(p => p.WorldId)
            .Distinct()
            .ToListAsync(ct);

        if (calendarEvent.WorldId is { } current && !worlds.Contains(current))
            worlds.Add(current);

        var groupId = await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.ManagedGroupId).FirstOrDefaultAsync(ct);

        return await db.VRChatInstances.AsNoTracking()
            .Where(i => i.ClosedAt == null
                && (i.Id == opened
                    || (groupId != null && groupId != "" && i.GroupId == groupId
                        && worlds.Contains(i.WorldId)
                        && i.OpenedAt >= from
                        && i.OpenedAt <= now)))
            .OrderByDescending(i => i.OpenedAt)
            .ToListAsync(ct);
    }
}
