using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Reviews;
using Modbot.Api.Auth;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Reviews;

/// <summary>
/// People acted on more than once (spec 5.8.4): the list, and one person's history block for the
/// subject pane.
/// </summary>
/// <remarks>
/// Both read <c>modbot_repeat_offender</c>, which the detection run rebuilds from facts. Every
/// response carries when that last happened, because a count is only as fresh as its last run
/// (spec 4.2.5). Gated on <see cref="ModbotPermissions.ViewProfile"/>: this is a person's history.
/// </remarks>
public static class RepeatOffenderEndpoints
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    public static IEndpointRouteBuilder MapRepeatOffenders(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/repeat-offenders").WithTags("Repeat offenders").RequireAuthorization();

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] int? offset,
                [FromQuery] int? limit,
                [FromQuery] string? status,
                CancellationToken ct) =>
            {
                var wanted = (status ?? "all").ToLowerInvariant();
                if (wanted is not ("all" or RepeatOffenderStatus.Repeat or RepeatOffenderStatus.MoreThanOnce))
                    return Results.BadRequest(new { error = "status must be all, repeat or more-than-once." });

                var skip = Math.Max(0, offset ?? 0);
                var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

                // "More than once" is the list's definition: somebody acted on once is history on
                // their own pane, not a pattern worth a list.
                var query = db.RepeatOffenders.AsNoTracking().Where(r => r.Actions >= 2);
                if (wanted != "all")
                    query = query.Where(r => r.Status == wanted);

                var total = await query.CountAsync(ct);
                var rows = await query
                    .OrderByDescending(r => r.LastActionAt)
                    .ThenBy(r => r.SubjectId)
                    .Skip(skip)
                    .Take(take)
                    .ToListAsync(ct);

                var thresholds = await ReviewEndpoints.ThresholdsAsync(db, ct);
                var names = await PeopleNames.LookupAsync(
                    db,
                    rows.Select(r => r.SubjectId).Concat(rows.Select(r => r.LastActorId).OfType<string>()).ToList(),
                    ct);

                return Results.Ok(new RepeatOffenderListResponse(
                    rows.Select(r => View(r, names)).ToList(),
                    total,
                    skip,
                    Rule(thresholds),
                    await LastRunAsync(db, ct),
                    clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("ListRepeatOffenders")
            .WithSummary("People acted on more than once, most recent action first")
            .WithDescription(
                "Per person: instance kicks, warns, bans, removals from the group and join requests "
                + "turned away, all time and over the last 30 and 90 days; how many different "
                + "moderators acted; the first and last action; and a status decided by the rule in "
                + "`rule`. Rebuilt from the fact log on the daily totals schedule -- `lastRunAt` says when.")
            .Produces<RepeatOffenderListResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/one", async (
                [FromQuery] string id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(id))
                    return Results.BadRequest(new { error = "id is required." });

                var row = await db.RepeatOffenders.AsNoTracking().FirstOrDefaultAsync(r => r.SubjectId == id, ct);
                var thresholds = await ReviewEndpoints.ThresholdsAsync(db, ct);
                var lastRun = await LastRunAsync(db, ct);

                if (row is null)
                    return Results.Ok(new SubjectHistory(id, false, null, Rule(thresholds), lastRun, clock.UtcNow));

                var names = await PeopleNames.LookupAsync(db, [row.SubjectId, row.LastActorId ?? string.Empty], ct);

                return Results.Ok(new SubjectHistory(id, true, View(row, names), Rule(thresholds), lastRun, clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("GetSubjectHistory")
            .WithSummary("One person's count of being acted on -- the History block on their pane")
            .WithDescription(
                "The id goes in the query string, never the path: VRChat ids are opaque and a legacy "
                + "one can contain anything. `known` is false when nobody has ever acted on them.")
            .Produces<SubjectHistory>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>The status rule in words, so a screen can show it beside the word (spec 5.10.3).</summary>
    public static string Rule(ReviewThresholds thresholds)
        => $"Repeat: {thresholds.RepeatOffenderActionsIn30Days} or more actions in the last 30 days.";

    private static async Task<DateTimeOffset?> LastRunAsync(ModbotContext db, CancellationToken ct)
        => await db.ReviewRunState.AsNoTracking().Where(s => s.Id == 1).Select(s => s.UpdatedAt).FirstOrDefaultAsync(ct);

    private static RepeatOffenderView View(RepeatOffender row, IReadOnlyDictionary<string, string> names)
    {
        var platform = row.SubjectPlatform.ToString().ToLowerInvariant();

        return new RepeatOffenderView(
            new Person(platform, row.SubjectId, names.GetValueOrDefault(row.SubjectId)),
            row.InstanceKicks,
            row.Warns,
            row.Bans,
            row.Unbans,
            row.Removals,
            row.Rejections,
            row.Actions,
            row.ActionsLast30Days,
            row.ActionsLast90Days,
            row.Moderators,
            row.ModeratorsLast90Days,
            row.FirstActionAt,
            row.LastActionAt,
            row.LastActionType,
            FactLabels.For(row.LastActionType),
            row.LastActorId is null ? null : new Person(platform, row.LastActorId, names.GetValueOrDefault(row.LastActorId)),
            row.Status,
            row.ComputedAt);
    }
}
