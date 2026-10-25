using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.AutomaticRoles;
using Modbot.Core.Data;

namespace Modbot.Api.Features.AutomaticRoles;

/// <summary>A role a rule gives, and whether the person holds it now.</summary>
public sealed record MyRoleStanding(string Id, string Name, bool Held);

/// <summary>One rule that applies to the signed-in account.</summary>
/// <param name="Met">Every condition is met.</param>
/// <param name="Conditions">One line per condition the rule switches on, in the order the role giver checks them.</param>
public sealed record MyAutomaticRole(
    Guid Id,
    string? Name,
    MyRoleStanding? VRChatRole,
    MyRoleStanding? DiscordRole,
    bool Met,
    IReadOnlyList<RuleCondition> Conditions);

public sealed record MyAutomaticRoleList(IReadOnlyList<MyAutomaticRole> Rules);

/// <summary>
/// Account → Automatic roles: the rules that apply to the signed-in account and where it stands on
/// each condition. The facts come from the same <see cref="ModeratorFactsGatherer"/> the role giver reads.
/// </summary>
public static class MyAutomaticRolesEndpoint
{
    public static IEndpointRouteBuilder MapMyAutomaticRoles(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/me/automatic-roles", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] ModeratorFactsGatherer gatherer,
                CancellationToken ct) =>
            {
                var id = ModbotAuth.UserIdOf(http.User);
                if (id is null)
                    return Results.Unauthorized();

                var rules = await db.AutomaticRoles.AsNoTracking().OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
                if (rules.Count == 0)
                    return Results.Ok(new MyAutomaticRoleList([]));

                var facts = (await gatherer.GatherAsync(rules, [id.Value], ct)).FirstOrDefault();
                if (facts is null)
                    return Results.Ok(new MyAutomaticRoleList([]));

                var applicable = rules.Where(r => AutomaticRoleRule.AppliesTo(r, facts)).ToList();
                var names = await AutomaticRoleNames.ReadAsync(db, applicable, ct);

                return Results.Ok(new MyAutomaticRoleList([.. applicable.Select(rule =>
                {
                    var conditions = AutomaticRoleRule.Explain(rule, facts);

                    return new MyAutomaticRole(
                        rule.Id,
                        rule.Name,
                        names.VRChat(rule.VRChatRoleId) is { } vrchat
                            ? new MyRoleStanding(vrchat.Id, vrchat.Name, facts.VRChatRoleIds.Contains(vrchat.Id))
                            : null,
                        names.Discord(rule.DiscordRoleId) is { } discord
                            ? new MyRoleStanding(discord.Id, discord.Name, facts.DiscordRoleIds.Contains(discord.Id))
                            : null,
                        conditions.All(c => c.Met),
                        conditions);
                })]));
            })
            .WithTags("Auth")
            .WithName("GetMyAutomaticRoles")
            .WithSummary("Get my automatic roles")
            .WithDescription(
                "The automatic role rules that apply to the signed-in account: the whole moderator team, "
                + "or the Discord or VRChat roles the rule names. Each lists whether the account holds the "
                + "role now and, for every condition the rule switches on, whether it is met and the numbers "
                + "behind it. Empty when no rule applies.")
            .Produces<MyAutomaticRoleList>()
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        return app;
    }
}
