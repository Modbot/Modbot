using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Reviews;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <param name="Value">The fact type, as sent back in <c>types</c>.</param>
/// <param name="Label">The kind of action in plain words.</param>
/// <param name="Counts">Whether it counts towards the threshold now.</param>
public sealed record RepeatOffenderTypeOption(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("counts")] bool Counts);

/// <param name="Threshold">Actions in the last 30 days that make somebody a repeat offender.</param>
/// <param name="Types">Every kind of action that can count, and whether it does.</param>
/// <param name="LastRunAt">When the standings were last rebuilt.</param>
public sealed record RepeatOffenderRulesView(
    [property: JsonPropertyName("threshold")] int Threshold,
    [property: JsonPropertyName("types")] IReadOnlyList<RepeatOffenderTypeOption> Types,
    [property: JsonPropertyName("lastRunAt")] DateTimeOffset? LastRunAt);

public sealed record SetRepeatOffenderRulesRequest(
    [property: JsonPropertyName("threshold")] int Threshold,
    [property: JsonPropertyName("types")] IReadOnlyList<string>? Types);

/// <summary>
/// The two numbers behind the repeat-offender status: how many actions in thirty days it takes,
/// and which kinds of action count.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.8.5. Both were fixed until now — the threshold lived in a settings document nothing on a
/// screen could reach, and the kinds were compiled into the counting query. Groups disagree about
/// what a strike is: a group that clears an instance to break up an argument does not think a kick
/// is a mark against anybody, and counting it beside bans made their regulars look like their
/// worst people.
/// </para>
/// <para>
/// <strong>Changing either rebuilds the standings before the answer comes back.</strong> Every row
/// in <c>modbot_repeat_offender</c> is derived from facts under the old rule, so leaving them
/// until the next scheduled run would show an operator the rule they just replaced. The rebuild is
/// the one the detection run already uses.
/// </para>
/// </remarks>
public static class RepeatOffenderSettingsEndpoints
{
    public static IEndpointRouteBuilder MapRepeatOffenderSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/repeat-offenders")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/", async ([FromServices] ModbotContext db, CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);
                return Results.Ok(await ViewAsync(db, ReviewThresholds.Read(settings.ReviewThresholds), ct));
            })
            .WithName("GetRepeatOffenderRules")
            .WithSummary("The repeat offender threshold and the kinds of action that count")
            .Produces<RepeatOffenderRulesView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/", async (
                [FromBody] SetRepeatOffenderRulesRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var chosen = body.Types?
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.Ordinal)
                    .ToList() ?? [];

                if (chosen.Count == 0)
                    return Results.BadRequest(new { error = "Pick at least one kind of action." });

                var unknown = chosen.FirstOrDefault(t => !ActionsOnPeople.Types.Contains(t, StringComparer.Ordinal));
                if (unknown is not null)
                    return Results.BadRequest(new { error = $"'{unknown}' is not a kind of action that can count." });

                var settings = await db.GetSettingsAsync(ct);
                var before = ReviewThresholds.Read(settings.ReviewThresholds);

                // Every kind chosen is stored as "no choice", so the list stays the default rather
                // than a copy of it -- a later release that adds a kind of action adds it for this
                // deployment too, which is what the sparse settings document is for.
                var everything = chosen.Count == ActionsOnPeople.Types.Length;

                var after = (before with
                {
                    RepeatOffenderActionsIn30Days = body.Threshold,
                    RepeatOffenderTypes = everything ? null : chosen,
                }).Clamped();

                var changed = after.RepeatOffenderActionsIn30Days != before.RepeatOffenderActionsIn30Days
                    || !after.CountedTypes.SequenceEqual(before.CountedTypes, StringComparer.Ordinal);

                if (!changed)
                    return Results.Ok(await ViewAsync(db, before, ct));

                settings.ReviewThresholds = after.ToJson();
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.SettingsChanged,
                    "settings",
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["setting"] = "repeatOffenderRules",
                        ["before"] = new JsonObject
                        {
                            ["threshold"] = before.RepeatOffenderActionsIn30Days,
                            ["types"] = TypesJson(before),
                        },
                        ["after"] = new JsonObject
                        {
                            ["threshold"] = after.RepeatOffenderActionsIn30Days,
                            ["types"] = TypesJson(after),
                        },
                    },
                    ct);

                // The standings are a cache of a rule that has just changed, so they are rebuilt
                // now rather than at the next daily run (spec 5.2: every derived table is
                // recomputable, and this is the moment to prove it).
                if (http.RequestServices.GetService<ReviewJob>() is { } reviews)
                    await reviews.RebuildAsync(ct);

                return Results.Ok(await ViewAsync(db, after, ct));
            })
            .WithName("SetRepeatOffenderRules")
            .WithSummary("Change the threshold or the kinds of action that count")
            .WithDescription(
                "Both are rules the standings are computed under, so every person's counts are "
                + "rebuilt from the fact log before this answers.")
            .Produces<RepeatOffenderRulesView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static JsonArray TypesJson(ReviewThresholds thresholds)
    {
        var array = new JsonArray();

        foreach (var type in thresholds.CountedTypes)
            array.Add(JsonValue.Create(type));

        return array;
    }

    private static async Task<RepeatOffenderRulesView> ViewAsync(
        ModbotContext db,
        ReviewThresholds thresholds,
        CancellationToken ct)
    {
        var counted = thresholds.CountedTypes;

        var options = ActionsOnPeople.Types
            .Select(t => new RepeatOffenderTypeOption(t, FactLabels.For(t), counted.Contains(t, StringComparer.Ordinal)))
            .ToList();

        var lastRun = await db.ReviewRunState.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        return new RepeatOffenderRulesView(thresholds.RepeatOffenderActionsIn30Days, options, lastRun);
    }
}
