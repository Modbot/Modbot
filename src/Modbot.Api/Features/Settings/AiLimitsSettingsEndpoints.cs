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

public sealed record AiSpentView(decimal Cost, long InputTokens, long CachedInputTokens, long OutputTokens);

/// <param name="CachedInputPerMillion">Null means cached input costs the same as other input.</param>
public sealed record AiPriceView(string Model, decimal InputPerMillion, decimal? CachedInputPerMillion, decimal OutputPerMillion);

/// <param name="AppliesTo"><c>everyone</c>, <c>role</c> or <c>user</c>.</param>
/// <param name="Name">The role's name or the account's username. Null for everyone.</param>
/// <param name="Today">
/// What this limit is compared with today: everyone's spend, or the account's own. Null for a role
/// limit, which is compared with each member's own spend separately.
/// </param>
public sealed record AiLimitView(
    string AppliesTo,
    Guid? RoleId,
    Guid? UserId,
    string? Name,
    decimal? PerDay,
    decimal? PerMonth,
    AiSpentView? Today,
    AiSpentView? Month);

public sealed record AiNamedOption(Guid Id, string Name);

/// <param name="ModelsUsed">Models asked for so far or set in settings, for the price list to offer.</param>
public sealed record AiLimitsResponse(
    AiSpentView Today,
    AiSpentView Month,
    IReadOnlyList<AiPriceView> Prices,
    IReadOnlyList<string> ModelsUsed,
    IReadOnlyList<AiLimitView> Limits,
    IReadOnlyList<AiNamedOption> Roles,
    IReadOnlyList<AiNamedOption> Users);

public sealed record AiLimitInput(string? AppliesTo, Guid? RoleId, Guid? UserId, decimal? PerDay, decimal? PerMonth);

/// <param name="Limits">Every limit. What is left out is removed.</param>
public sealed record AiLimitsUpdate(IReadOnlyList<AiLimitInput>? Limits);

/// <param name="Prices">Every price. What is left out is removed.</param>
public sealed record AiPricesUpdate(IReadOnlyList<AiPriceView>? Prices);

/// <summary>
/// Settings → AI → Limits: what AI has cost, what each model costs, and the daily and monthly spend
/// limits for everyone, a role or one account (AI chat design §10).
/// </summary>
public static class AiLimitsSettingsEndpoints
{
    public const decimal MaxAmount = 1_000_000_000m;
    public const int MaxModelsListed = 100;

    public static IEndpointRouteBuilder MapAiLimitsSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/ai").WithTags("Settings");

        group.MapGet("/limits", async (
                [FromServices] ModbotContext db,
                [FromServices] AiSpendLimits spend,
                CancellationToken ct) =>
                Results.Ok(await ViewAsync(db, spend, ct)))
            .WithName("GetAiLimits")
            .WithSummary("AI spend today and this month, model prices, and spend limits")
            .Produces<AiLimitsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("/limits", async (
                [FromBody] AiLimitsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] AiSpendLimits spend,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var wanted = body.Limits ?? [];
                var roleIds = await db.Roles.AsNoTracking().Select(r => r.Id).ToListAsync(ct);
                var userIds = await db.Users.AsNoTracking().Select(u => u.Id).ToListAsync(ct);
                var seen = new HashSet<(string, Guid?)>();
                var rows = new List<AiSpendLimit>();

                foreach (var limit in wanted)
                {
                    var appliesTo = limit.AppliesTo?.Trim().ToLowerInvariant();
                    Guid? target = appliesTo switch
                    {
                        AiSpendLimit.Everyone => null,
                        AiSpendLimit.Role => limit.RoleId,
                        AiSpendLimit.User => limit.UserId,
                        _ => Guid.Empty,
                    };

                    if (target == Guid.Empty)
                        return Results.BadRequest(new { error = "A limit applies to everyone, a role or a user." });
                    if (appliesTo == AiSpendLimit.Role && (target is null || !roleIds.Contains(target.Value)))
                        return Results.BadRequest(new { error = "Choose a role for each role limit." });
                    if (appliesTo == AiSpendLimit.User && (target is null || !userIds.Contains(target.Value)))
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
                        RoleId = appliesTo == AiSpendLimit.Role ? target : null,
                        UserId = appliesTo == AiSpendLimit.User ? target : null,
                        PerDay = limit.PerDay,
                        PerMonth = limit.PerMonth,
                        UpdatedAt = clock.UtcNow,
                    });
                }

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.AiSpendLimits.ExecuteDeleteAsync(ct);
                db.AiSpendLimits.AddRange(rows);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, spend, ct));
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
                [FromServices] AiSpendLimits spend,
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
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.AiModelPrices.ExecuteDeleteAsync(ct);
                db.AiModelPrices.AddRange(rows);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, spend, ct));
            })
            .WithName("SetAiPrices")
            .WithSummary("Replace the model prices")
            .Produces<AiLimitsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static async Task<AiLimitsResponse> ViewAsync(ModbotContext db, AiSpendLimits spend, CancellationToken ct)
    {
        var (today, month) = await spend.SpentAsync(null, ct);

        var prices = await db.AiModelPrices.AsNoTracking()
            .OrderBy(p => p.Model)
            .Select(p => new AiPriceView(p.Model, p.InputPerMillion, p.CachedInputPerMillion, p.OutputPerMillion))
            .ToListAsync(ct);

        var settings = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.AiModel, s.AiChatModel })
            .FirstOrDefaultAsync(ct);

        var used = await db.AiUsage.AsNoTracking()
            .Select(u => u.Model)
            .Distinct()
            .Take(MaxModelsListed)
            .ToListAsync(ct);

        var models = new[] { settings?.AiModel, settings?.AiChatModel }
            .Concat(used)
            .OfType<string>()
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

        // Everyone first, then roles, then accounts, each by name.
        foreach (var l in stored.OrderBy(l => l.AppliesTo switch { AiSpendLimit.Everyone => 0, AiSpendLimit.Role => 1, _ => 2 }))
        {
            AiSpent? limitToday = null, limitMonth = null;
            string? name = null;

            switch (l.AppliesTo)
            {
                case AiSpendLimit.Everyone:
                    (limitToday, limitMonth) = (today, month);
                    break;
                case AiSpendLimit.User:
                    name = users.FirstOrDefault(u => u.Id == l.UserId)?.Name;
                    (limitToday, limitMonth) = await spend.SpentAsync(l.UserId, ct);
                    break;
                case AiSpendLimit.Role:
                    name = roles.FirstOrDefault(r => r.Id == l.RoleId)?.Name;
                    break;
            }

            limits.Add(new AiLimitView(l.AppliesTo, l.RoleId, l.UserId, name, l.PerDay, l.PerMonth, View(limitToday), View(limitMonth)));
        }

        return new AiLimitsResponse(
            View(today)!,
            View(month)!,
            prices,
            models,
            [.. limits.OrderBy(l => l.AppliesTo switch { AiSpendLimit.Everyone => 0, AiSpendLimit.Role => 1, _ => 2 }).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)],
            roles,
            users);
    }

    private static AiSpentView? View(AiSpent? spent) =>
        spent is null ? null : new AiSpentView(spent.Cost, spent.InputTokens, spent.CachedInputTokens, spent.OutputTokens);
}
