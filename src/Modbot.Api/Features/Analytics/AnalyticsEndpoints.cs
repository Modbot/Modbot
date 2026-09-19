using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.Analytics.Group;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Api.Features.Analytics.Server;
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
            .WithSummary("Get group analytics")
            .WithDescription(
                "My Group: is the community growing or shrinking, and what changed? "
                + "Member count over time, joins and leaves per day, net change, roles, how long "
                + "current members have been members, and whether invites turn into joins. Daily "
                + "series come from modbot_daily_total, which is never aged out; the headcount, "
                + "role changes, tenure and invite follow-up come from the fact log, which a "
                + "retention window can shorten. `coverage` reports both ranges.")
            .Produces<GroupAnalytics>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        // The one chart with its own endpoint. Its window is hours to years of five-minute
        // readings, not the whole-day window the page shares, and folding it into the page would
        // either send every reading with every page load or make the page's range mean two things.
        group.MapGet("/group/member-count", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] string? range,
                CancellationToken ct) =>
            {
                var series = await new GroupMemberCountQuery(db).RunAsync(range ?? GroupMemberCountQuery.Week, clock.UtcNow, ct);

                return series is null
                    ? Results.BadRequest(new { error = "`range` must be day, week, month or all." })
                    : Results.Ok(series);
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetGroupMemberCount")
            .WithSummary("Get member count")
            .WithDescription(
                "Every reading the group-info sync took of VRChat's memberCount and "
                + "onlineMemberCount, about one every five minutes, over the last `day`, `week` "
                + "(the default), `month` or `all` recorded time. Long ranges are thinned to at "
                + "most about 500 points: the window is cut into equal steps and the last reading "
                + "in each is kept, so every point is a number VRChat reported at the time given. "
                + "Time from before the first stored reading comes from group-info facts, one "
                + "point per day. Readings older than the presence retention window are deleted "
                + "when one is set.")
            .Produces<GroupMemberCountSeries>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/server", async (
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

                return Results.Ok(await new ServerAnalyticsQuery(db).RunAsync(window.From, window.To, clock.UtcNow, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetServerAnalytics")
            .WithSummary("Get server analytics")
            .WithDescription(
                "My Server: is the Discord server healthy, and who keeps it going? "
                + "Discord's member count, joins and leaves, messages and voice minutes per day, people "
                + "active each day and over the week and thirty days before it, the busiest channels and "
                + "hours (UTC), moderation actions, the people who sent the most, new members who "
                + "stayed after 7 and 30 days, and member health now: how many members were active in "
                + "the last thirty days and who went quiet. Active means sent a message or spent time "
                + "in voice; bots are left out. Series come from daily totals.")
            .Produces<ServerAnalytics>()
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
            .WithSummary("Get team analytics")
            .WithDescription(
                "My Team: who is doing the moderation work, and when is nobody covering? "
                + "Actions per moderator broken down by kind and over time, from daily totals; and "
                + "coverage gaps -- stretches when people were in a group instance and no moderator "
                + "was, computed from the companion's presence reports. A moderator is present "
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
            .WithSummary("Get world analytics")
            .WithDescription(
                "Worlds: which of our worlds actually get used? "
                + "Time people were seen in each world, distinct visitors and instances opened, plus "
                + "visitors per day per world from daily totals. Time and visitors come from presence "
                + "reports, which exist only while a moderator's companion is in the instance.")
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
            .WithSummary("Get instance analytics")
            .WithDescription(
                "Instances: when is the community actually active? "
                + "Instances opened and closed per day (daily totals), most open at once and the most "
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
