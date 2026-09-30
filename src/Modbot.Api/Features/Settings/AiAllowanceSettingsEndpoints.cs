using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.AI.Usage;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>What a team member may use in a month. Null means no limit of that kind.</summary>
/// <param name="Tokens">Input plus output tokens, whatever the model.</param>
/// <param name="Money">US dollars. Counted only where the model has a price.</param>
public sealed record AiAllowanceAmount(long? Tokens, decimal? Money);

/// <summary>One team member: their allowance and what they have used this month.</summary>
/// <param name="Own">Their allowance when they have one of their own, in place of the default. Null when they use the default.</param>
/// <param name="Allowance">What applies to them: their own, or the default.</param>
/// <param name="Month">
/// Everything used under this account this month, every feature together. Use with no account behind
/// it (insights on a schedule, AutoMod's own checks) is not in anybody's figure.
/// </param>
/// <param name="PastLimits">
/// The account holds Administrator or "Use AI in excess of usage limits", so the allowance does not
/// stop it. The use is still counted and shown.
/// </param>
public sealed record AiMemberAllowanceView(
    Guid UserId,
    string Name,
    AiAllowanceAmount? Own,
    AiAllowanceAmount Allowance,
    AiSpentView Month,
    bool PastLimits);

/// <param name="Default">What every member gets unless they have one of their own.</param>
/// <param name="ResetsAt">When the month being counted ends and every allowance starts again (UTC).</param>
public sealed record AiAllowancesView(
    AiAllowanceAmount Default,
    IReadOnlyList<AiMemberAllowanceView> Members,
    DateTimeOffset ResetsAt);

public sealed record AiMemberAllowanceInput(Guid? UserId, long? Tokens, decimal? Money);

/// <param name="Default">The default for every member. Null leaves it as it is.</param>
/// <param name="Members">Every allowance of a member's own. What is left out goes back to the default. Null leaves them as they are.</param>
public sealed record AiAllowancesUpdate(AiAllowanceAmount? Default, IReadOnlyList<AiMemberAllowanceInput>? Members);

/// <summary>
/// Settings → AI → Limits → Team allowances: how much AI each team member may use in a month, and
/// what each has used so far.
/// </summary>
/// <remarks>
/// The enforcement is in <see cref="AiSpendLimits"/> and the counting is the spend report's; this is
/// the setting and its view. A change is recorded as a settings change in the audit log, naming what
/// it was and what it became.
/// </remarks>
public static class AiAllowanceSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAiAllowanceSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/ai").WithTags("AI settings");

        group.MapPut("/allowances", async (
                HttpContext http,
                [FromBody] AiAllowancesUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] AiSpendReport report,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.Default is { } given && Problem(given) is { } defaultProblem)
                    return Results.BadRequest(new { error = defaultProblem });

                var users = await db.Users.AsNoTracking()
                    .Where(u => u.DeletedAt == null)
                    .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

                var seen = new HashSet<Guid>();
                var wanted = new List<AiMemberAllowance>();

                foreach (var input in body.Members ?? [])
                {
                    if (input.UserId is not { } userId || !users.ContainsKey(userId))
                        return Results.BadRequest(new { error = "Choose a team member for each allowance." });
                    if (Problem(new AiAllowanceAmount(input.Tokens, input.Money)) is { } memberProblem)
                        return Results.BadRequest(new { error = memberProblem });
                    if (!seen.Add(userId))
                        return Results.BadRequest(new { error = "Each team member can only have one allowance." });

                    wanted.Add(new AiMemberAllowance
                    {
                        UserId = userId,
                        MonthlyTokens = input.Tokens,
                        MonthlyMoney = input.Money,
                        UpdatedAt = clock.UtcNow,
                    });
                }

                var settings = await db.GetSettingsAsync(ct);
                var current = await db.AiMemberAllowances.ToListAsync(ct);

                var before = Snapshot(
                    new AiAllowanceAmount(settings.AiMemberMonthlyTokens, settings.AiMemberMonthlyMoney), current, users);

                if (body.Default is { } next)
                {
                    settings.AiMemberMonthlyTokens = next.Tokens;
                    settings.AiMemberMonthlyMoney = next.Money;
                }

                if (body.Members is not null)
                {
                    // Kept as they are when nothing about them changed, so the time they were set stays true.
                    foreach (var row in current.Where(c => wanted.All(w => w.UserId != c.UserId)))
                        db.AiMemberAllowances.Remove(row);

                    foreach (var row in wanted)
                    {
                        var existing = current.FirstOrDefault(c => c.UserId == row.UserId);
                        if (existing is null)
                        {
                            db.AiMemberAllowances.Add(row);
                        }
                        else if (existing.MonthlyTokens != row.MonthlyTokens || existing.MonthlyMoney != row.MonthlyMoney)
                        {
                            existing.MonthlyTokens = row.MonthlyTokens;
                            existing.MonthlyMoney = row.MonthlyMoney;
                            existing.UpdatedAt = row.UpdatedAt;
                        }
                    }
                }

                var after = Snapshot(
                    new AiAllowanceAmount(settings.AiMemberMonthlyTokens, settings.AiMemberMonthlyMoney),
                    body.Members is null ? current : wanted,
                    users);

                var changed = !string.Equals(before.ToJsonString(), after.ToJsonString(), StringComparison.Ordinal);

                await db.SaveChangesAsync(ct);

                if (changed)
                {
                    await facts.RecordAsync(
                        FactType.SettingsChanged,
                        "settings",
                        Actor.Of(http),
                        new JsonObject
                        {
                            ["setting"] = "aiAllowances",
                            ["before"] = before,
                            ["after"] = after,
                        },
                        ct);
                }

                return Results.Ok(await AiLimitsSettingsEndpoints.ViewAsync(db, report, ct));
            })
            .WithName("SetAiAllowances")
            .WithSummary("Update team AI allowances")
            .WithDescription(
                "Set how many tokens and how many US dollars each team member may use in a month: one default for every member "
                + "and, where wanted, an allowance of a member's own. A change is recorded in the audit log.")
            .Produces<AiLimitsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    /// <summary>What is wrong with an allowance, or null.</summary>
    private static string? Problem(AiAllowanceAmount amount)
    {
        if (amount.Tokens is < 0)
            return "An allowance in tokens must be zero or more.";
        if (amount.Tokens is > AiMemberAllowances.MaxTokens)
            return $"An allowance in tokens can be at most {AiMemberAllowances.MaxTokens:N0}.";
        if (amount.Money is < 0)
            return "An allowance in US dollars must be zero or more.";
        if (amount.Money is > AiPriceRules.MaxAmount)
            return $"An allowance in US dollars can be at most {AiPriceRules.MaxAmount:N0}.";

        return null;
    }

    /// <summary>The default and every allowance of a member's own, as the audit log shows them.</summary>
    private static JsonObject Snapshot(
        AiAllowanceAmount defaultAllowance, IEnumerable<AiMemberAllowance> members, IReadOnlyDictionary<Guid, string> names)
        => new()
        {
            ["default"] = Amount(defaultAllowance.Tokens, defaultAllowance.Money),
            ["members"] = new JsonArray([.. members
                .OrderBy(m => names.GetValueOrDefault(m.UserId), StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.UserId)
                .Select(m =>
                {
                    var one = Amount(m.MonthlyTokens, m.MonthlyMoney);
                    one["userId"] = m.UserId.ToString();
                    one["username"] = names.GetValueOrDefault(m.UserId);
                    return (JsonNode)one;
                })]),
        };

    /// <remarks>
    /// The dollars are written without trailing zeros: the database hands back 5.000000 for what was
    /// sent as 5, and "was it changed?" is decided by comparing these, so 5 and 5.000000 must read alike.
    /// </remarks>
    private static JsonObject Amount(long? tokens, decimal? money) => new()
    {
        ["tokens"] = tokens,
        ["money"] = money is { } dollars ? dollars / 1.000000000000000000000000000000000m : null,
    };

    /// <summary>The team, each with an allowance and this month's use, for the Limits screen.</summary>
    public static async Task<AiAllowancesView> ViewAsync(
        ModbotContext db, AiSpendReport report, DateTimeOffset now, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.AiMemberMonthlyTokens, s.AiMemberMonthlyMoney })
            .FirstOrDefaultAsync(ct);

        var fallback = new AiAllowanceAmount(settings?.AiMemberMonthlyTokens, settings?.AiMemberMonthlyMoney);

        var own = await db.AiMemberAllowances.AsNoTracking().ToDictionaryAsync(a => a.UserId, ct);
        var used = await report.MonthByMemberAsync(ct);

        var users = await db.Users.AsNoTracking()
            .Where(u => u.DeletedAt == null)
            .OrderBy(u => u.Username)
            .Select(u => new { u.Id, u.Username })
            .ToListAsync(ct);

        var roleSets = await db.UserRoles.AsNoTracking()
            .Select(r => new { r.UserId, r.Role.Permissions })
            .ToListAsync(ct);

        var members = users
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u =>
            {
                AiAllowanceAmount? mine = own.TryGetValue(u.Id, out var row) ? new AiAllowanceAmount(row.MonthlyTokens, row.MonthlyMoney) : null;
                var spent = used.GetValueOrDefault(u.Id) ?? AiSpent.None;
                var held = ModbotRole.Union(roleSets.Where(r => r.UserId == u.Id).Select(r => r.Permissions));

                return new AiMemberAllowanceView(
                    u.Id,
                    u.Username,
                    mine,
                    mine ?? fallback,
                    new AiSpentView(spent.Cost, spent.InputTokens, spent.CachedInputTokens, spent.OutputTokens, spent.UnpricedTokens),
                    AiMemberAllowances.PastLimits(held));
            })
            .ToList();

        return new AiAllowancesView(fallback, members, AiPeriods.MonthOf(now).AddMonths(1));
    }
}
