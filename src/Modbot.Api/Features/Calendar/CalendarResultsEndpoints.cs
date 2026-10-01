using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// What each time an event ran did, for the event on the calendar and the calendar's Past events
/// (<see cref="CalendarResults"/>).
/// </summary>
/// <remarks>
/// Read from Modbot's own tables; nothing here calls VRChat. Both need See calendar and See
/// analytics: they are the calendar's own numbers, and the same figures the analytics pages show.
/// Who was seen needs See the audit log as well, as it does in the instance popup.
/// </remarks>
public static class CalendarResultsEndpoints
{
    public static IEndpointRouteBuilder MapCalendarResults(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/calendar").WithTags("Calendar");

        group.MapGet("/events/{id:guid}/results", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromQuery] DateTimeOffset? at,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (at is not { } start)
                    return Results.BadRequest(new { error = "at is required." });

                var calendarEvent = await db.CalendarEvents.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);

                if (calendarEvent is null)
                    return Results.NotFound();

                var canSee = ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ViewAuditLog);
                var view = await new CalendarResults(db).ForOccurrenceAsync(calendarEvent, start, canSee, clock.UtcNow, ct);

                return view is null ? Results.NotFound() : Results.Ok(view);
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar | ModbotPermissions.ViewAnalytics)
            .WithName("GetCalendarEventResults")
            .WithSummary("Get what one time of an event did")
            .WithDescription(
                "One time an event ran, named by its start (`at`, as the calendar gives it): the instance "
                + "it ran in, with the most at once and how long it ran as the instance popup has them; "
                + "how many joined the group and how many asked to, from when the event opened until a "
                + "day after it ended; and the middle value of each of those over the event's last "
                + "earlier times.\n\n"
                + "The instance is the one Modbot opened for that time. When Modbot opened none, it is "
                + "the group's instance in the event's world that was open for most of the event.\n\n"
                + "Who a moderator's client saw there (`occurrence.seen` and `people`) needs ViewAuditLog "
                + "as well, as in the instance popup; without it `canSeeWhoWasThere` is false and both "
                + "are empty. They exist only while a moderator's client was in the instance. 404 when "
                + "the event has not run at that time.")
            .Produces<CalendarResultsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/past", async (
                [FromQuery] int? days,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var span = days ?? CalendarResults.PastDays;
                if (span < 1 || span > CalendarResults.MaxPastDays)
                    return Results.BadRequest(new { error = $"days must be from 1 to {CalendarResults.MaxPastDays}." });

                var now = clock.UtcNow;

                return Results.Ok(await new CalendarResults(db).PastAsync(now.AddDays(-span), now, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar | ModbotPermissions.ViewAnalytics)
            .WithName("ListPastCalendarEvents")
            .WithSummary("List past events")
            .WithDescription(
                "Every time an event ran over the last `days` days (90 unless asked), the most at once "
                + "first, at most 50. Each carries the same figures as one event's results, without who "
                + "was seen.")
            .Produces<CalendarPastView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}
