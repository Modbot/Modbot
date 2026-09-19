using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.AI.Usage;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>One model the picker offers.</summary>
/// <param name="Maker">The part of the id before the <c>/</c>, which the picker groups by.</param>
/// <param name="PriceSource"><c>entered</c>, <c>openrouter</c>, or null when there is no price.</param>
/// <param name="PriceVaries">A router whose price depends on the model it picks.</param>
/// <param name="Missing">What the feature needs that this model cannot do: <c>tools</c>, <c>structuredOutput</c>.</param>
/// <param name="CostPerThousandCalls">What a thousand calls would cost at this deployment's own average tokens.</param>
public sealed record AiCatalogModelView(
    string Id,
    string? Name,
    string Maker,
    int? ContextLength,
    int? MaxOutputTokens,
    IReadOnlyList<string> InputModalities,
    IReadOnlyList<string> OutputModalities,
    DateTimeOffset? AddedAt,
    decimal? InputPerMillion,
    decimal? CachedInputPerMillion,
    decimal? OutputPerMillion,
    string? PriceSource,
    bool PriceVaries,
    bool Free,
    bool Tools,
    bool StructuredOutput,
    bool ImagesIn,
    bool Recommended,
    IReadOnlyList<string> Missing,
    decimal? CostPerThousandCalls);

/// <summary>What one call of the feature has used on average, per call.</summary>
public sealed record AiTokenAverageView(long Calls, decimal InputTokens, decimal CachedInputTokens, decimal OutputTokens);

/// <summary>
/// OpenRouter's model list as last fetched, in the order the picker shows it.
/// </summary>
/// <param name="Feature">Which model box the picker was opened from: <c>base</c>, <c>moderation</c>, <c>insights</c> or <c>chat</c>.</param>
/// <param name="Needs">What that feature needs a model to do.</param>
/// <param name="Now">The server's clock, which "fetched an hour ago" is counted from.</param>
/// <param name="FetchedAt">When the list was last fetched. Null when it never was.</param>
/// <param name="Average">The feature's own average tokens per call. Null when there is no usage to work from.</param>
/// <param name="Prices">The prices the operator entered, for models this list does not carry.</param>
public sealed record AiCatalogResponse(
    string Feature,
    IReadOnlyList<string> Needs,
    DateTimeOffset Now,
    DateTimeOffset? FetchedAt,
    AiTokenAverageView? Average,
    IReadOnlyList<AiCatalogModelView> Models,
    IReadOnlyList<AiPriceView> Prices);

/// <summary>
/// The model picker's list: what OpenRouter offers, what each model costs, and what it would cost
/// this deployment (AI chat design §10.9).
/// </summary>
/// <remarks>
/// Served from what the daily price fetch stored, so opening the picker calls nobody. Refresh on
/// the picker is the existing <c>POST /api/settings/ai/prices/fetch</c>.
/// </remarks>
public static class AiCatalogEndpoints
{
    public static IEndpointRouteBuilder MapAiCatalog(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/ai").WithTags("AI settings");

        group.MapGet("/catalog", async (
                [FromQuery] string? feature,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var which = feature?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(which))
                    which = AiModelCatalog.BaseFeature;
                if (!AiModelCatalog.Features.Contains(which))
                    return Results.BadRequest(new { error = "Choose base, moderation, insights or chat." });

                var now = clock.UtcNow;
                var models = await AiModelCatalog.ReadAsync(db, ct);
                var average = await AiModelCatalog.AverageAsync(db, which, now, ct);
                var fetchedAt = await db.AiCatalogModels.AsNoTracking().MaxAsync(m => (DateTimeOffset?)m.FetchedAt, ct);

                var entered = await db.AiModelPrices.AsNoTracking().OrderBy(p => p.Model).ToListAsync(ct);

                var ranked = AiModelCatalog.Order(models, which, now, average);

                return Results.Ok(new AiCatalogResponse(
                    which,
                    AiModelCatalog.NeedsOf(which),
                    now,
                    fetchedAt,
                    average.HasHistory
                        ? new AiTokenAverageView(average.Calls, average.InputPerCall, average.CachedInputPerCall, average.OutputPerCall)
                        : null,
                    [.. ranked.Select(View)],
                    [.. entered.Select(p => new AiPriceView(p.Model, p.InputPerMillion, p.CachedInputPerMillion, p.OutputPerMillion))]));
            })
            .WithName("GetAiCatalog")
            .WithSummary("Get AI model catalogue")
            .WithDescription("OpenRouter's models, their prices, and what a thousand calls would cost.")
            .Produces<AiCatalogResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static AiCatalogModelView View(AiRankedModel ranked)
    {
        var model = ranked.Model;

        return new AiCatalogModelView(
            model.Id,
            model.Name,
            model.Maker,
            model.ContextLength,
            model.MaxOutputTokens,
            model.InputModalities,
            model.OutputModalities,
            model.AddedAt,
            model.Price?.InputPerMillion,
            model.Price?.CachedInputPerMillion,
            model.Price?.OutputPerMillion,
            model.Price?.Source,
            model.PriceVaries,
            model.Free,
            model.Tools,
            model.StructuredOutput,
            model.ImagesIn,
            ranked.Recommended,
            ranked.Missing,
            ranked.CostPerThousandCalls);
    }
}
