using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.Analytics.Group;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Api.Features.Analytics.Team;
using Modbot.Api.Features.Analytics.Worlds;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Analytics;

/// <summary>
/// The Analytics section: four pages, one question each (spec 10.1).
/// </summary>
/// <remarks>
/// <para>
/// One endpoint per page rather than one per chart. The charts on a page are read together and
/// share a date window; splitting them would mean several round trips and several chances for
/// the windows to disagree, which is how a page ends up showing two panels that cannot both be
/// true. And one endpoint for all four pages would be the single "metrics" dashboard the spec
/// says not to build.
/// </para>
/// <para>
/// The window is expressed in whole UTC days because the daily totals are, and translating between
/// two day boundaries in one system is spec 5.4's stated bug farm.
/// </para>
/// </remarks>
public static class AnalyticsEndpoints
{
    public const int DefaultDays = 30;
    public const int MaxDays = 1830;

    public static IEndpointRouteBuilder MapAnalytics(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/analytics").WithTags("Analytics").RequireAuthorization();

        // Explicit on every parameter: an unattributed concrete type is bound as the request
        // body, and on a GET that throws during route mapping and takes the whole host's
        // endpoint table with it.

        group.MapGet("/group", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] int? days,
                [FromQuery] bool? all,
                [FromQuery] DateOnly? from,
                [FromQuery] DateOnly? to,
                CancellationToken ct) =>
            {
                var window = await WindowAsync(db, clock, days, all, from, to, ct);
                if (window.Error is not null) return window.Error;

                return Results.Ok(await new GroupAnalyticsQuery(db).RunAsync(window.From, window.To, clock.UtcNow, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetGroupAnalytics")
            .WithSummary("My Group: is the community growing or shrinking, and what changed?")
            .WithDescription(
                "Member count over time, joins and leaves per day, net change, roles, how long "
                + "current members have been members, and whether invites turn into joins. Daily "
                + "series come from modbot_daily_total, which is never aged out; the headcount, "
                + "role changes, tenure and invite follow-up come from the fact log, which a "
                + "retention window can shorten. `coverage` reports both ranges.")
            .Produces<GroupAnalytics>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/team", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] int? days,
                [FromQuery] bool? all,
                [FromQuery] DateOnly? from,
                [FromQuery] DateOnly? to,
                CancellationToken ct) =>
            {
                var window = await WindowAsync(db, clock, days, all, from, to, ct);
                if (window.Error is not null) return window.Error;

                return Results.Ok(await new TeamAnalyticsQuery(db).RunAsync(window.From, window.To, clock.UtcNow, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetTeamAnalytics")
            .WithSummary("My Team: who is doing the moderation work, and when is nobody covering?")
            .WithDescription(
                "Actions per moderator broken down by kind and over time, from daily totals; and "
                + "coverage gaps -- stretches when people were in a group instance and no moderator "
                + "was, computed from the desktop client's presence reports. A moderator is present "
                + "when a paired client is reporting from the instance or when somebody recognised as "
                + "a moderator (holds a role with moderation permissions, or has taken a moderation "
                + "action) is seen there. Moderators without the client are not seen at all.")
            .Produces<TeamAnalytics>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/worlds", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] int? days,
                [FromQuery] bool? all,
                [FromQuery] DateOnly? from,
                [FromQuery] DateOnly? to,
                CancellationToken ct) =>
            {
                var window = await WindowAsync(db, clock, days, all, from, to, ct);
                if (window.Error is not null) return window.Error;

                return Results.Ok(await new WorldsAnalyticsQuery(db).RunAsync(window.From, window.To, clock.UtcNow, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetWorldsAnalytics")
            .WithSummary("Worlds: which of our worlds actually get used?")
            .WithDescription(
                "Time people were seen in each world, distinct visitors and instances opened, plus "
                + "visitors per day per world from daily totals. Time and visitors come from presence "
                + "reports, which exist only while a moderator's desktop client is in the instance.")
            .Produces<WorldsAnalytics>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/instances", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] int? days,
                [FromQuery] bool? all,
                [FromQuery] DateOnly? from,
                [FromQuery] DateOnly? to,
                CancellationToken ct) =>
            {
                var window = await WindowAsync(db, clock, days, all, from, to, ct);
                if (window.Error is not null) return window.Error;

                return Results.Ok(await new InstancesAnalyticsQuery(db).RunAsync(window.From, window.To, clock.UtcNow, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetInstancesAnalytics")
            .WithSummary("Instances: when is the community actually active?")
            .WithDescription(
                "Instances opened and closed per day (daily totals), most open at once and the most "
                + "people seen in one instance per day, how long instances typically stay open, and "
                + "an hour-of-week heatmap of arrivals and openings. Hours are UTC; the page shifts "
                + "them to the viewer's time zone.")
            .Produces<InstancesAnalytics>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>
    /// The UTC day window a request asks for.
    /// </summary>
    /// <remarks>
    /// Never the machine clock (spec 4.4): "today" is a value this deployment agrees on, not
    /// whatever the container thinks. <c>all=true</c> starts at the first day either source
    /// knows about, so "all time" means all recorded time and not an arbitrary cap.
    /// </remarks>
    private static async Task<(DateOnly From, DateOnly To, IResult? Error)> WindowAsync(
        ModbotContext db,
        IModbotClock clock,
        int? days,
        bool? all,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        var today = AnalyticsSql.DayOf(clock.UtcNow);
        var last = to ?? today;

        DateOnly first;
        if (from is not null)
            first = from.Value;
        else if (all == true)
            first = await AnalyticsCoverageQuery.FirstDayAsync(db, ct) ?? last;
        else
            first = last.AddDays(-(Math.Clamp(days ?? DefaultDays, 1, MaxDays) - 1));

        if (first > last)
            return (first, last, Results.BadRequest(new { error = "`from` is after `to`." }));

        return (first, last, null);
    }
}
