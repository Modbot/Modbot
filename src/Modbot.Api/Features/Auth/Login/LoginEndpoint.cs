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
                "Returns 401 for any failed attempt without saying why. Distinguishing "
                + "\"no such user\" from \"wrong password\" would turn the login form into a way "
                + "to enumerate staff accounts.")
            .Produces<LoginResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .AllowAnonymous();

        return app;
    }
}
