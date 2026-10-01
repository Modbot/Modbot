using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Chat;
using Modbot.Core.Data;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Auth.Me;

public static class MeEndpoint
{
    public static IEndpointRouteBuilder MapMe(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/auth/me", async (
                HttpContext http,
                [FromServices] UserAccountService accounts,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var id = ModbotAuth.UserIdOf(http.User);
                if (id is null)
                    return Results.Unauthorized();

                // One read, for the role names and the linked VRChat account, neither of which
                // is in the cookie. The session check already proved the account exists.
                var user = await accounts.FindAsync(id.Value, ct);

                if (user is null)
                    return Results.Unauthorized();

                var me = SessionUser.From(user, await ChatSwitch.ReadAsync(db, ct));

                // Called with a key: which key, and what it may do now. The permissions on the
                // principal are already the key's, capped by the account (API keys design §3.2).
                if (ApiKeyAuthentication.KeyIdOf(http.User) is { } keyId)
                {
                    var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == keyId, ct);
                    if (key is null)
                        return Results.Unauthorized();

                    me = me with
                    {
                        ApiKey = new KeyInUse(
                            key.Id,
                            key.Name,
                            key.Start,
                            PermissionCatalog.NamesOf(ModbotAuth.PermissionsOf(http.User)),
                            key.ExpiresAt),
                    };
                }

                return Results.Ok(me);
            })
            .WithTags("Auth")
            .WithName("GetCurrentUser")
            .WithSummary("Get current user")
            .WithDescription(
                "Who the session belongs to. "
                + "The SPA calls this on load to decide what to show: the sign-in form, the "
                + "link-your-VRChat-account page, or the app. Reachable before the VRChat link "
                + "is done, because it is how the SPA finds out the link is not done.\n\n"
                + "Called with an API key, `apiKey` names the key and lists what it may do right "
                + "now, which is what every request with it is checked against: its own "
                + "permissions, never more than the account holds. `permissionNames` beside it "
                + "are the account's. Null for a session.")
            .Produces<SessionUser>()
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization(ModbotAuth.SignedInPolicy);

        return app;
    }
}
