using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modbot.Api.Features.Onboarding.CreateAdmin;

public static class CreateAdminEndpoint
{
    public static IEndpointRouteBuilder MapCreateAdmin(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/onboarding/administrator", CreateAdminHandler.HandleAsync)
            .WithTags("Onboarding")
            // The setup wizard's own steps, for the web app only: left out of the public API reference.
            .ExcludeFromDescription()
            .WithName("CreateAdministrator")
            .WithSummary("Create the first staff account (spec 7.1, step 1)")
            .WithDescription(
                "The first account created gets Administrator and is signed in immediately, so "
                + "the wizard can continue into the steps that require authentication.\n\n"
                + OnboardingAccess.Rule)
            .Produces<Modbot.Api.Features.Auth.SessionUser>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status409Conflict)
            .RequiresOnboardingAccess(requireVRChatLink: false);

        return app;
    }
}
