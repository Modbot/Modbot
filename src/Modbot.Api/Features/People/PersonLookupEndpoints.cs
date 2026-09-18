using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data;

namespace Modbot.Api.Features.People;

/// <summary>
/// One person, whichever of their accounts a link named (one view per person design §3).
/// </summary>
/// <remarks>
/// <para>
/// A moderator following a link has one of three ids: a VRChat user id, a Discord user id, or a
/// Modbot account id. All three are the same human being and the screen is one screen, so the
/// browser asks here once and gets back every account Modbot can honestly tie to the one it was
/// given.
/// </para>
/// <para>
/// Ids go in the query string, never in the path: a VRChat id is arbitrary text and a legacy one
/// can hold anything (foundation §3.1.1).
/// </para>
/// </remarks>
public static class PersonLookupEndpoints
{
    public static IEndpointRouteBuilder MapPersonLookup(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/people/lookup").WithTags("People").RequireAuthorization();

        group.MapGet("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromQuery] string? vrchatUserId,
                [FromQuery] string? discordUserId,
                [FromQuery] Guid? accountId,
                CancellationToken ct) =>
            {
                var given = new[]
                {
                    !string.IsNullOrWhiteSpace(vrchatUserId),
                    !string.IsNullOrWhiteSpace(discordUserId),
                    accountId is not null,
                }.Count(set => set);

                if (given != 1)
                    return Results.BadRequest(new { error = "Give one of vrchatUserId, discordUserId or accountId." });

                var ask = new PersonAsk(vrchatUserId?.Trim(), discordUserId?.Trim(), accountId);
                var sight = PersonSight.Of(ModbotAuth.PermissionsOf(http.User));

                return Results.Ok(await new PersonLookup(db).ResolveAsync(ask, sight, ct));
            })
            .WithName("GetPerson")
            .WithSummary("One person's VRChat, Discord and Modbot accounts, from any one of them")
            .WithDescription(
                "Give exactly one of `vrchatUserId`, `discordUserId` or `accountId`. The answer "
                + "carries every account Modbot can tie to it, each with `foundBy` saying what "
                + "tied it: `asked` for the one you gave, `link` for the proved Discord account "
                + "link, `account` for an id recorded on a Modbot account.\n\n"
                + "Nothing is guessed. Accounts are tied only by the proved link and by the ids "
                + "written on a Modbot account; display names are never compared and an id's "
                + "shape is never read. An account that ties to nothing answers with itself and "
                + "nulls, which is the honest answer rather than an error.\n\n"
                + "Each part needs the permission that already shows it elsewhere: the Discord "
                + "side and the VRChat display name need ViewProfile, the Discord display name "
                + "needs ViewMembers, and the Modbot account needs ViewOperationalLog or "
                + "ManageUsers. A part you may not see is left null; `canSeeAccount` says whether "
                + "a null Modbot account means there is none or that you may not be told.")
            .Produces<PersonView>()
            .Produces(StatusCodes.Status400BadRequest);

        return app;
    }
}
