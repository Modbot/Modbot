using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Auth.Me;

/// <param name="Id">The signed-in account's id.</param>
/// <param name="Username">Display name for the account menu.</param>
/// <param name="Permissions">What the SPA should offer. Enforcement is always server-side.</param>
public sealed record MeResponse(Guid Id, string Username, ModbotPermissions Permissions);

public static class MeEndpoint
{
    public static IEndpointRouteBuilder MapMe(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/auth/me", (HttpContext http) =>
            {
                var id = ModbotAuth.UserIdOf(http.User);
                if (id is null)
                    return Results.Unauthorized();

                return Results.Ok(new MeResponse(
                    id.Value,
                    http.User.Identity?.Name ?? string.Empty,
                    ModbotAuth.PermissionsOf(http.User)));
            })
            .WithTags("Auth")
            .WithName("GetCurrentUser")
            .WithSummary("Who the session belongs to")
            .WithDescription(
                "The SPA calls this on load to decide whether to show the sign-in form. It is "
                + "read from the session cookie, so it costs no database query.")
            .Produces<MeResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        return app;
    }
}
