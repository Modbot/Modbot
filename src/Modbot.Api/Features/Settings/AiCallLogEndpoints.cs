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

/// <summary>One call in the log, counts only.</summary>
/// <param name="Cost">
/// What the call cost: the provider's own figure where it sent one, otherwise the tokens priced
/// from the price list. Null when the model has no price.
/// </param>
/// <param name="HasText">The prompt and the answer were kept, so the call can be opened.</param>
public sealed record AiCallView(
    Guid Id,
    DateTimeOffset At,
    string Feature,
    string FeatureLabel,
    string ModelAsked,
    string? ModelAnswered,
    string? Provider,
    bool Fallback,
    string Outcome,
    string OutcomeLabel,
    string? Error,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    decimal? Cost,
    int DurationMs,
    Guid? UserId,
    string? Username,
    bool Flagged,
    bool HasText);

/// <param name="Next">Pass as <c>skip</c> to read the next page. Null on the last page.</param>
/// <param name="Models">Every model in the log, for the model filter.</param>
/// <param name="KeepDays">How long a row is kept. 0 keeps them forever.</param>
public sealed record AiCallLogPage(
    IReadOnlyList<AiCallView> Calls,
    int? Next,
    DateTimeOffset Now,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> Outcomes,
    int KeepDays);

/// <summary>One call with what the model was sent and what it answered.</summary>
public sealed record AiCallDetail(AiCallView Call, string? Prompt, string? Answer);

/// <summary>
/// Settings → AI → Call log: every AI call Modbot has made, and what the model was sent and
/// answered for the ones that flagged something or that somebody started from a button.
/// </summary>
/// <remarks>
/// <para>
/// Two permissions, because they are two different things to be shown. The counts -- which feature,
/// which model, how long, what went wrong -- are Modbot's own operational record, so
/// <see cref="ModbotPermissions.ViewOperationalLog"/> is enough. The prompt and the answer are a
/// member's own profile text or Discord message, and reading those needs
/// <see cref="ModbotPermissions.ManageSettings"/>, the same permission that decides where that text
/// is sent in the first place.
/// </para>
/// </remarks>
public static class AiCallLogEndpoints
{
    /// <summary>Rows in one page.</summary>
    public const int PageSize = 100;

    /// <summary>The most models the filter offers. Beyond that the search box is the way.</summary>
    public const int MaxModelsListed = 200;

    public static IEndpointRouteBuilder MapAiCallLog(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/ai/calls").WithTags("AI settings");

        group.MapGet("", async (
                [FromQuery] string? feature,
                [FromQuery] string? outcome,
                [FromQuery] string? model,
                [FromQuery] DateTimeOffset? from,
                [FromQuery] DateTimeOffset? to,
                [FromQuery] bool? flagged,
                [FromQuery] int? skip,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var rows = db.AiCalls.AsNoTracking();

                if (!string.IsNullOrWhiteSpace(feature))
                    rows = rows.Where(c => c.Feature == feature);
                if (!string.IsNullOrWhiteSpace(outcome))
                    rows = rows.Where(c => c.Outcome == outcome);
                if (!string.IsNullOrWhiteSpace(model))
                    rows = rows.Where(c => c.ModelAsked == model || c.ModelAnswered == model);
                if (from is { } start)
                    rows = rows.Where(c => c.At >= start);
                if (to is { } end)
                    rows = rows.Where(c => c.At <= end);
                if (flagged == true)
                    rows = rows.Where(c => c.Flagged);

                // Version 7 ids sort by when the call was made, so ordering by the id is ordering
                // by time, with no ties to lose a row to.
                var offset = Math.Max(0, skip ?? 0);

                var page = await rows
                    .OrderByDescending(c => c.Id)
                    .Skip(offset)
                    .Take(PageSize + 1)
                    .ToListAsync(ct);

                var more = page.Count > PageSize;
                if (more)
                    page.RemoveAt(page.Count - 1);

                var prices = await AiPrices.ForAsync(
                    db,
                    [.. page.Where(c => c.ReportedCost is null).Select(c => c.ModelAnswered ?? c.ModelAsked).Distinct(StringComparer.Ordinal)],
                    ct);

                var models = await db.AiCalls.AsNoTracking()
                    .Select(c => c.ModelAnswered ?? c.ModelAsked)
                    .Distinct()
                    .OrderBy(m => m)
                    .Take(MaxModelsListed)
                    .ToListAsync(ct);

                var keepDays = await db.Settings.AsNoTracking().Where(s => s.Id == 1)
                    .Select(s => s.AiCallLogKeepDays)
                    .FirstOrDefaultAsync(ct);

                return Results.Ok(new AiCallLogPage(
                    [.. page.Select(c => View(c, prices))],
                    more ? offset + page.Count : null,
                    clock.UtcNow,
                    models,
                    AiFeatures.All,
                    AiCallOutcomes.All,
                    keepDays));
            })
            .WithName("GetAiCallLog")
            .WithSummary("AI calls, newest first, with counts and what went wrong")
            .Produces<AiCallLogPage>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ViewOperationalLog);

        group.MapGet("/{id:guid}", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var call = await db.AiCalls.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
                if (call is null)
                    return Results.NotFound();

                var prices = await AiPrices.ForAsync(
                    db, call.ReportedCost is null ? [call.ModelAnswered ?? call.ModelAsked] : [], ct);

                return Results.Ok(new AiCallDetail(View(call, prices), call.Prompt, call.Answer));
            })
            .WithName("GetAiCall")
            .WithSummary("One AI call, with what the model was sent and what it answered")
            .Produces<AiCallDetail>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static AiCallView View(AiCall call, IReadOnlyDictionary<string, AiPrice> prices)
    {
        var model = call.ModelAnswered ?? call.ModelAsked;

        var cost = call.ReportedCost
                   ?? AiPrices.CostOf(prices.GetValueOrDefault(model), call.InputTokens, call.CachedInputTokens, call.OutputTokens);

        return new AiCallView(
            call.Id,
            call.At,
            call.Feature,
            AiFeatures.LabelOf(call.Feature),
            call.ModelAsked,
            call.ModelAnswered,
            call.Provider,
            call.Fallback,
            call.Outcome,
            AiCallOutcomes.LabelOf(call.Outcome),
            call.Error,
            call.InputTokens,
            call.CachedInputTokens,
            call.OutputTokens,
            cost,
            call.DurationMs,
            call.UserId,
            call.Username,
            call.Flagged,
            call.Prompt is not null || call.Answer is not null);
    }
}
