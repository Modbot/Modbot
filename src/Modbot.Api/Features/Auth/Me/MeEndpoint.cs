using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
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
                CancellationToken ct) =>
            {
                var id = ModbotAuth.UserIdOf(http.User);
                if (id is null)
                    return Results.Unauthorized();

                // One read, for the role names and the linked VRChat account, neither of which
                // is in the cookie. The session check already proved the account exists.
                var user = await accounts.FindAsync(id.Value, ct);

                return user is null
                    ? Results.Unauthorized()
                    : Results.Ok(SessionUser.From(user));
            })
            .WithTags("Auth")
            .WithName("GetCurrentUser")
            .WithSummary("Who the session belongs to")
            .WithDescription(
                "The SPA calls this on load to decide what to show: the sign-in form, the "
                + "link-your-VRChat-account page, or the app. Reachable before the VRChat link "
                + "is done, because it is how the SPA finds out the link is not done.")
            .Produces<SessionUser>()
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization(ModbotAuth.SignedInPolicy);

        return app;
    }
}
