using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.AI;
using Modbot.AI.Usage;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <param name="UnpricedTokens">Tokens of models with no price, whose cost is unknown and not in <paramref name="Cost"/>.</param>
public sealed record AiSpentView(decimal Cost, long InputTokens, long CachedInputTokens, long OutputTokens, long UnpricedTokens);

/// <summary>A price the operator entered.</summary>
/// <param name="CachedInputPerMillion">Null means cached input costs the same as other input.</param>
public sealed record AiPriceView(string Model, decimal InputPerMillion, decimal? CachedInputPerMillion, decimal OutputPerMillion);

/// <summary>A price fetched from OpenRouter.</summary>
public sealed record AiFetchedPriceView(decimal InputPerMillion, decimal? CachedInputPerMillion, decimal OutputPerMillion, DateTimeOffset FetchedAt);

/// <summary>One model in use, set in settings or priced by the operator, with both of its prices.</summary>
/// <param name="Entered">The operator's price, which wins. Null when none.</param>
/// <param name="Fetched">OpenRouter's price. Null when OpenRouter does not list the model or nothing was fetched.</param>
public sealed record AiModelPricesView(string Model, AiPriceView? Entered, AiFetchedPriceView? Fetched);

/// <param name="AppliesTo"><c>everyone</c>, <c>feature</c>, <c>role</c> or <c>user</c>.</param>
/// <param name="Name">The role's name or the account's username. Null otherwise.</param>
/// <param name="Today">
/// What this limit is compared with today: everyone's spend, the feature's, or the account's own
/// Chat spend. Null for a role limit, which is compared with each member's own spend separately.
/// </param>
/// <param name="Estimate">The month-end estimate, for a limit for everyone or a feature.</param>
public sealed record AiLimitView(
    string AppliesTo,
    string? Feature,
    Guid? RoleId,
    Guid? UserId,
    string? Name,
    decimal? PerDay,
    decimal? PerMonth,
    AiSpentView? Today,
    AiSpentView? Month,
    AiSpentView? Estimate);

/// <summary>A monthly token limit kept from before prices (AI chat design §10.5).</summary>
public sealed record AiTokenLimitView(string Feature, long MonthlyTokens, AiSpentView Month, AiSpentView Estimate);

public sealed record AiNamedOption(Guid Id, string Name);

public sealed record AiFeatureOption(string Id, string Label);

public sealed record AiFeatureSpendView(
    string Feature,
    string Label,
    AiSpentView Today,
    AiSpentView Week,
    AiSpentView Month,
    AiSpentView LastMonth,
    AiSpentView Estimate);

public sealed record AiDaySpendView(DateOnly Day, string Feature, decimal Cost, long Tokens, long UnpricedTokens);

public sealed record AiUserSpendView(Guid UserId, string? Username, AiSpentView Month);

/// <param name="Now">The server's clock, which days and months are counted from.</param>
/// <param name="Days">Spend per feature per day from <paramref name="FirstDay"/> to <paramref name="LastDay"/>; days with none are left out.</param>
/// <param name="Prices">The prices the operator entered.</param>
/// <param name="Models">Every model in use, set in settings or priced, with its entered and fetched price.</param>
/// <param name="ModelsUsed">Models asked for so far or set in settings, for the price list to offer.</param>
public sealed record AiLimitsResponse(
    DateTimeOffset Now,
    AiSpentView Today,
    AiSpentView Month,
    IReadOnlyList<AiFeatureSpendView> Spend,
    AiFeatureSpendView Total,
    DateOnly FirstDay,
    DateOnly LastDay,
    IReadOnlyList<AiDaySpendView> Days,
    IReadOnlyList<AiLimitView> Limits,
    IReadOnlyList<AiTokenLimitView> TokenLimits,
    IReadOnlyList<AiUserSpendView> TopChatUsers,
    IReadOnlyList<AiPriceView> Prices,
    IReadOnlyList<AiModelPricesView> Models,
    DateTimeOffset? PricesFetchedAt,
    IReadOnlyList<string> ModelsUsed,
    IReadOnlyList<AiFeatureOption> Features,
    IReadOnlyList<AiNamedOption> Roles,
    IReadOnlyList<AiNamedOption> Users);

public sealed record AiLimitInput(string? AppliesTo, string? Feature, Guid? RoleId, Guid? UserId, decimal? PerDay, decimal? PerMonth);

public sealed record AiTokenLimitInput(string? Feature, long? MonthlyTokens);

/// <param name="Limits">Every money limit. What is left out is removed.</param>
/// <param name="TokenLimits">Every token limit kept from before prices. Null leaves them as they are.</param>
public sealed record AiLimitsUpdate(IReadOnlyList<AiLimitInput>? Limits, IReadOnlyList<AiTokenLimitInput>? TokenLimits = null);

/// <param name="Prices">Every price. What is left out is removed.</param>
public sealed record AiPricesUpdate(IReadOnlyList<AiPriceView>? Prices);

/// <summary>
/// Settings → AI → Limits: what AI has cost by feature, estimates, the spend limits, and the price
/// list (AI chat design §10).
/// </summary>
public static class AiLimitsSettingsEndpoints
{
    public const decimal MaxAmount = AiPriceRules.MaxAmount;
    public const int MaxModelsListed = 100;
    public const int TopChatUsersListed = 10;

    public static IEndpointRouteBuilder MapAiLimitsSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/ai").WithTags("AI settings");

        group.MapGet("/limits", async (
                [FromServices] ModbotContext db,
                [FromServices] AiSpendReport report,
                CancellationToken ct) =>
                Results.Ok(await ViewAsync(db, report, ct)))
            .WithName("GetAiLimits")
            .WithSummary("AI spend by feature, estimates, spend limits, and model prices")
            .Produces<AiLimitsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("/limits", async (
                [FromBody] AiLimitsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] AiSpendReport report,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var roleIds = await db.Roles.AsNoTracking().Select(r => r.Id).ToListAsync(ct);
                var userIds = await db.Users.AsNoTracking().Select(u => u.Id).ToListAsync(ct);
                var seen = new HashSet<(string, string?)>();
                var rows = new List<AiSpendLimit>();

                foreach (var limit in body.Limits ?? [])
                {
                    var appliesTo = limit.AppliesTo?.Trim().ToLowerInvariant();
                    var feature = limit.Feature?.Trim().ToLowerInvariant();

                    string? target = appliesTo switch
                    {
                        AiSpendLimit.Everyone => null,
                        AiSpendLimit.ForFeature => feature,
                        AiSpendLimit.Role => limit.RoleId?.ToString(),
                        AiSpendLimit.User => limit.UserId?.ToString(),
                        _ => string.Empty,
                    };

                    if (target == string.Empty)
                        return Results.BadRequest(new { error = "A limit applies to everyone, a feature, a role or a user." });
                    if (appliesTo == AiSpendLimit.ForFeature && (feature is null || !AiFeatures.All.Contains(feature)))
                        return Results.BadRequest(new { error = "Choose a feature for each feature limit." });
                    if (appliesTo == AiSpendLimit.Role && (limit.RoleId is null || !roleIds.Contains(limit.RoleId.Value)))
                        return Results.BadRequest(new { error = "Choose a role for each role limit." });
                    if (appliesTo == AiSpendLimit.User && (limit.UserId is null || !userIds.Contains(limit.UserId.Value)))
                        return Results.BadRequest(new { error = "Choose a user for each user limit." });
                    if (limit.PerDay is < 0 or > MaxAmount || limit.PerMonth is < 0 or > MaxAmount)
                        return Results.BadRequest(new { error = "A limit must be zero or more." });
                    if (limit.PerDay is null && limit.PerMonth is null)
                        return Results.BadRequest(new { error = "Give each limit a daily or a monthly amount." });
                    if (!seen.Add((appliesTo!, target)))
                        return Results.BadRequest(new { error = "Each limit can only be set once." });

                    rows.Add(new AiSpendLimit
                    {
                        AppliesTo = appliesTo!,
                        Feature = appliesTo == AiSpendLimit.ForFeature ? feature : null,
                        RoleId = appliesTo == AiSpendLimit.Role ? limit.RoleId : null,
                        UserId = appliesTo == AiSpendLimit.User ? limit.UserId : null,
                        PerDay = limit.PerDay,
                        PerMonth = limit.PerMonth,
                        UpdatedAt = clock.UtcNow,
                    });
                }

                List<AiFeatureLimit>? tokenRows = null;
                if (body.TokenLimits is { } tokenLimits)
                {
                    tokenRows = [];
                    foreach (var limit in tokenLimits)
                    {
                        var feature = limit.Feature?.Trim().ToLowerInvariant();
                        if (feature is null || !AiFeatures.All.Contains(feature))
                            return Results.BadRequest(new { error = "Choose a feature for each token limit." });
                        if (limit.MonthlyTokens is null or < 0)
                            return Results.BadRequest(new { error = "A token limit must be zero or more." });
                        if (tokenRows.Any(r => r.Feature == feature))
                            return Results.BadRequest(new { error = "Each limit can only be set once." });

                        tokenRows.Add(new AiFeatureLimit { Feature = feature, MonthlyTokenLimit = limit.MonthlyTokens });
                    }
                }

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.AiSpendLimits.ExecuteDeleteAsync(ct);
                db.AiSpendLimits.AddRange(rows);

                if (tokenRows is not null)
                {
                    await db.AiFeatureLimits.ExecuteDeleteAsync(ct);
                    db.AiFeatureLimits.AddRange(tokenRows);
                }

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, report, ct));
            })
            .WithName("SetAiLimits")
            .WithSummary("Replace the AI spend limits")
            .Produces<AiLimitsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("/prices", async (
                [FromBody] AiPricesUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] AiSpendReport report,
                [FromServices] AiTokenLimits tokenLimits,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var rows = new List<AiModelPrice>();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var price in body.Prices ?? [])
                {
                    var model = price.Model?.Trim();
                    if (string.IsNullOrEmpty(model) || model.Length > AiSettingsRules.MaxModelLength)
                        return Results.BadRequest(new { error = "Enter a model for each price." });
                    if (price.InputPerMillion is < 0 or > MaxAmount
                        || price.OutputPerMillion is < 0 or > MaxAmount
                        || price.CachedInputPerMillion is < 0 or > MaxAmount)
                        return Results.BadRequest(new { error = "A price must be zero or more." });
                    if (!seen.Add(model))
                        return Results.BadRequest(new { error = $"{model} has two prices." });

                    rows.Add(new AiModelPrice
                    {
                        Model = model,
                        InputPerMillion = price.InputPerMillion,
                        CachedInputPerMillion = price.CachedInputPerMillion,
                        OutputPerMillion = price.OutputPerMillion,
                        UpdatedAt = clock.UtcNow,
                    });
                }

                // Usage is stored as tokens and priced when it is read, so a price entered now also
                // prices what was used earlier.
                await using (var transaction = await db.Database.BeginTransactionAsync(ct))
                {
                    await db.AiModelPrices.ExecuteDeleteAsync(ct);
                    db.AiModelPrices.AddRange(rows);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }

                await tokenLimits.ChangeToMoneyAsync(ct);

                return Results.Ok(await ViewAsync(db, report, ct));
            })
            .WithName("SetAiPrices")
            .WithSummary("Replace the prices the operator entered")
            .Produces<AiLimitsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/prices/fetch", async (
                [FromServices] ModbotContext db,
                [FromServices] AiSpendReport report,
                [FromServices] OpenRouterPrices openRouter,
                [FromServices] AiTokenLimits tokenLimits,
                CancellationToken ct) =>
            {
                var result = await openRouter.FetchAsync(ct);
                if (result.Error is not null)
                    return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway);

                await tokenLimits.ChangeToMoneyAsync(ct);

                return Results.Ok(await ViewAsync(db, report, ct));
            })
            .WithName("FetchAiPrices")
            .WithSummary("Fetch model prices from OpenRouter now")
            .WithDescription("Answers 502 with the reason when OpenRouter could not be reached or refused. Never retried.")
            .Produces<AiLimitsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status502BadGateway)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static async Task<AiLimitsResponse> ViewAsync(ModbotContext db, AiSpendReport report, CancellationToken ct)
    {
        var summary = await report.ReadAsync(ct);
        var now = summary.Now;

        var settings = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.AiModel, s.AiChatModel })
            .FirstOrDefaultAsync(ct);

        var insightModel = await db.InsightSettings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.Model)
            .FirstOrDefaultAsync(ct);

        var used = await db.AiUsage.AsNoTracking()
            .Select(u => u.Model)
            .Distinct()
            .Take(MaxModelsListed)
            .ToListAsync(ct);

        var entered = await db.AiModelPrices.AsNoTracking().OrderBy(p => p.Model).ToListAsync(ct);

        var modelsUsed = new[] { settings?.AiModel, settings?.AiChatModel, insightModel }
            .Concat(used)
            .OfType<string>()
            .Select(m => m.Trim())
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allModels = modelsUsed.Concat(entered.Select(p => p.Model)).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToList();

        var fetched = await db.AiFetchedPrices.AsNoTracking()
            .Where(p => allModels.Contains(p.Model))
            .ToDictionaryAsync(p => p.Model, StringComparer.Ordinal, ct);

        var fetchedAt = await db.AiFetchedPrices.AsNoTracking().MaxAsync(p => (DateTimeOffset?)p.FetchedAt, ct);

        var models = allModels.Select(m =>
        {
            var e = entered.FirstOrDefault(p => p.Model == m);
            var f = fetched.GetValueOrDefault(m);
            return new AiModelPricesView(
                m,
                e is null ? null : new AiPriceView(e.Model, e.InputPerMillion, e.CachedInputPerMillion, e.OutputPerMillion),
                f is null ? null : new AiFetchedPriceView(f.InputPerMillion, f.CachedInputPerMillion, f.OutputPerMillion, f.FetchedAt));
        }).ToList();

        var roles = await db.Roles.AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new AiNamedOption(r.Id, r.Name))
            .ToListAsync(ct);

        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new AiNamedOption(u.Id, u.Username))
            .ToListAsync(ct);

        var stored = await db.AiSpendLimits.AsNoTracking().ToListAsync(ct);
        var limits = new List<AiLimitView>();

        foreach (var l in stored)
        {
            AiSpent? today = null, month = null, estimate = null;
            string? name = null;

            switch (l.AppliesTo)
            {
                case AiSpendLimit.Everyone:
                    (today, month, estimate) = (summary.Total.Today, summary.Total.Month, summary.Total.Estimate);
                    break;
                case AiSpendLimit.ForFeature when l.Feature is not null:
                    var spend = summary.For(l.Feature);
                    (today, month, estimate) = (spend.Today, spend.Month, spend.Estimate);
                    name = AiFeatures.LabelOf(l.Feature);
                    break;
                case AiSpendLimit.User:
                    name = users.FirstOrDefault(u => u.Id == l.UserId)?.Name;
                    (today, month) = await AiSpending.TodayAndMonthAsync(db, now, AiFeatures.Chat, l.UserId, ct);
                    break;
                case AiSpendLimit.Role:
                    name = roles.FirstOrDefault(r => r.Id == l.RoleId)?.Name;
                    break;
            }

            limits.Add(new AiLimitView(
                l.AppliesTo, l.Feature, l.RoleId, l.UserId, name, l.PerDay, l.PerMonth, View(today), View(month), View(estimate)));
        }

        var tokenLimits = await db.AiFeatureLimits.AsNoTracking()
            .Where(l => l.MonthlyTokenLimit != null)
            .OrderBy(l => l.Feature)
            .ToListAsync(ct);

        var topUsers = await report.TopChatUsersAsync(TopChatUsersListed, ct);

        return new AiLimitsResponse(
            now,
            View(summary.Total.Today)!,
            View(summary.Total.Month)!,
            [.. summary.Features.Select(FeatureView)],
            FeatureView(summary.Total),
            summary.FirstDay,
            summary.LastDay,
            [.. summary.Days.Select(d => new AiDaySpendView(d.Day, d.Feature, d.Spent.Cost, d.Spent.Tokens, d.Spent.UnpricedTokens))],
            [.. limits
                .OrderBy(l => l.AppliesTo switch { AiSpendLimit.Everyone => 0, AiSpendLimit.ForFeature => 1, AiSpendLimit.Role => 2, _ => 3 })
                .ThenBy(l => l.Feature is null ? -1 : IndexOf(l.Feature))
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)],
            [.. tokenLimits.Select(l =>
            {
                var spend = summary.For(l.Feature);
                return new AiTokenLimitView(l.Feature, l.MonthlyTokenLimit!.Value, View(spend.Month)!, View(spend.Estimate)!);
            })],
            [.. topUsers.Select(u => new AiUserSpendView(u.UserId, u.Username, View(u.Spent)!))],
            [.. entered.Select(p => new AiPriceView(p.Model, p.InputPerMillion, p.CachedInputPerMillion, p.OutputPerMillion))],
            models,
            fetchedAt,
            modelsUsed,
            [.. AiFeatures.All.Select(f => new AiFeatureOption(f, AiFeatures.LabelOf(f)))],
            roles,
            users);
    }

    private static int IndexOf(string feature)
    {
        var index = AiFeatures.All.ToList().IndexOf(feature);
        return index < 0 ? int.MaxValue : index;
    }

    private static AiFeatureSpendView FeatureView(AiFeatureSpend spend) => new(
        spend.Feature,
        spend.Feature == "total" ? "Total" : AiFeatures.LabelOf(spend.Feature),
        View(spend.Today)!,
        View(spend.Week)!,
        View(spend.Month)!,
        View(spend.LastMonth)!,
        View(spend.Estimate)!);

    private static AiSpentView? View(AiSpent? spent) =>
        spent is null ? null : new AiSpentView(spent.Cost, spent.InputTokens, spent.CachedInputTokens, spent.OutputTokens, spent.UnpricedTokens);
}
