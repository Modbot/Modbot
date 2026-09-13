using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Metrics;

/// <summary>
/// Group health over time (spec 5.6, 10.1).
/// </summary>
/// <remarks>
/// <para>
/// One endpoint rather than one per chart. The charts on this screen are read together and they
/// share a date window; splitting them would mean four round trips and four chances for the
/// windows to disagree, which is how a dashboard ends up showing two panels that cannot both be
/// true.
/// </para>
/// <para>
/// The window is expressed in whole UTC days because the rollups are, and translating between two
/// day boundaries in one system is spec 5.4's stated bug farm.
/// </para>
/// </remarks>
public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetrics(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/metrics").WithTags("Metrics").RequireAuthorization();

        group.MapGet("/", async (
                // Explicit on every parameter: an unattributed concrete type is bound as the
                // request body, and on a GET that throws during route mapping and takes the whole
                // host's endpoint table with it.
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] int? days,
                [FromQuery] DateOnly? from,
                [FromQuery] DateOnly? to,
                CancellationToken ct) =>
            {
                // Never the machine clock (spec 4.4). "Today" is a value this deployment agrees
                // on, not whatever the container thinks.
                var now = clock.UtcNow;
                var today = DateOnly.FromDateTime(now.UtcDateTime);

                var last = to ?? today;
                var span = Math.Clamp(days ?? MetricsQuery.DefaultDays, 1, MetricsQuery.MaxDays);
                var first = from ?? last.AddDays(-(span - 1));

                if (first > last)
                    return Results.BadRequest(new { error = "`from` is after `to`." });

                return Results.Ok(await new MetricsQuery(db).RunAsync(first, last, now, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetMetrics")
            .WithSummary("Daily series, per-moderator totals, and the observed member count")
            .WithDescription(
                "Daily series come from modbot_rollup_daily, which is never aged out; the "
                + "per-type action breakdown and the observed member counts come from the fact "
                + "log, which a retention window can shorten. `coverage` reports both ranges "
                + "separately, because a chart may legitimately cover a longer period than the "
                + "audit log does and a single date picker over both would imply otherwise.\n\n"
                + "members.total is the running net of recorded joins and leaves from zero — not "
                + "the group's headcount. The headcount is `memberCount`, observed by the "
                + "group-info sync.")
            .Produces<MetricsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}
