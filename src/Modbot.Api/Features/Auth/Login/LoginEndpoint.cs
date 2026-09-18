using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modbot.Api.Features.Auth.Login;

public static class LoginEndpoint
{
    public static IEndpointRouteBuilder MapLogin(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/auth/login", LoginHandler.HandleAsync)
            .WithTags("Auth")
            .WithName("Login")
            .WithSummary("Start a session")
            .WithDescription(
                "The username field takes an email address or a username; both are matched "
                + "case-insensitively.\n\n"
                + "Returns 401 for any failed attempt without saying why. Distinguishing "
                + "\"no such user\" from \"wrong password\" would turn the login form into a way "
                + "to enumerate staff accounts, and answering differently for an address would "
                + "turn it into a way to find out who has an account here.")
            .Produces<SessionUser>()
            .Produces(StatusCodes.Status401Unauthorized)
            .AllowAnonymous();

        return app;
    }
}
