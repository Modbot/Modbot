using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Calendar;

/// <summary>One date of an event's series (<see cref="EventSeriesDate"/>).</summary>
/// <param name="StartsAt">When the date started.</param>
/// <param name="DiscordInterested">Interested on Discord when it started. Null when it was not read.</param>
/// <param name="VRChatInterested">Interested on VRChat when it started. Null when it was not read.</param>
/// <param name="Came">People seen arriving at the date's instance. Null when Modbot opened none or saw nobody.</param>
/// <param name="MostAtOnce">The most people counted in the instance at once. Null when it never counted.</param>
/// <param name="DiscordLate">The Discord count was read more than ten minutes after the date started.</param>
/// <param name="VRChatLate">The VRChat count was read more than ten minutes after the date started.</param>
public sealed record CalendarSeriesDateView(
    DateTimeOffset StartsAt,
    int? DiscordInterested,
    int? VRChatInterested,
    int? Came,
    int? MostAtOnce,
    bool DiscordLate,
    bool VRChatLate);

/// <param name="EventId">The event: one series is all the dates of one event.</param>
/// <param name="Title">The event's title.</param>
/// <param name="Dates">Earliest first, at most the latest 100.</param>
/// <param name="AverageDiscordInterested">Over the dates that have a Discord count. Null when none does.</param>
/// <param name="AverageVRChatInterested">Over the dates that have a VRChat count. Null when none does.</param>
/// <param name="AverageCame">Over the dates that have a count of who came. Null when none does.</param>
public sealed record CalendarSeriesView(
    Guid EventId,
    string Title,
    IReadOnlyList<CalendarSeriesDateView> Dates,
    decimal? AverageDiscordInterested,
    decimal? AverageVRChatInterested,
    decimal? AverageCame);

/// <summary>
/// How an event's dates did over time: how many were interested on Discord and on VRChat when each
/// started, and how many came. Read from Modbot's own tables; nothing here calls Discord or VRChat.
/// </summary>
public static class CalendarSeriesEndpoints
{
    public static IEndpointRouteBuilder MapCalendarSeries(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/calendar").WithTags("Calendar");

        group.MapGet("/events/{id:guid}/series", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var calendarEvent = await db.CalendarEvents.AsNoTracking()
                    .Where(e => e.Id == id && e.DeletedAt == null)
                    .Select(e => new { e.Id, e.Title })
                    .FirstOrDefaultAsync(ct);

                if (calendarEvent is null)
                    return Results.NotFound();

                var dates = await new EventSeriesHistory(db).ForEventAsync(id, ct: ct);

                return Results.Ok(new CalendarSeriesView(
                    calendarEvent.Id,
                    calendarEvent.Title,
                    [.. dates.Select(d => new CalendarSeriesDateView(
                        d.StartsAt, d.DiscordInterested, d.VRChatInterested, d.Came, d.MostAtOnce, d.DiscordLate, d.VRChatLate))],
                    EventSeriesHistory.Average(dates.Select(d => d.DiscordInterested)),
                    EventSeriesHistory.Average(dates.Select(d => d.VRChatInterested)),
                    EventSeriesHistory.Average(dates.Select(d => d.Came))));
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar | ModbotPermissions.ViewAnalytics)
            .WithName("GetCalendarEventSeries")
            .WithSummary("Get how an event's dates did over time")
            .WithDescription(
                "Every date of one event that has a count or an opened instance, earliest first, at most the "
                + "latest 100: how many people were interested on Discord and on VRChat when the date started, "
                + "how many people a moderator's client saw arrive at its instance, and the most counted at once. "
                + "Each interested count is written once, when the date starts; one read more than ten minutes "
                + "later (Modbot was not running at the start) is marked late, and a date from before Modbot "
                + "kept them has none. `came` counts only people seen where a moderator's client stood, so it "
                + "can be lower than the real turnout, and is null for a date nobody was seen at. The averages "
                + "are over the dates that have the figure.")
            .Produces<CalendarSeriesView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
