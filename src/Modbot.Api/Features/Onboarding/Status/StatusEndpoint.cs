using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modbot.Api.Features.Onboarding.Status;

public static class StatusEndpoint
{
    public static IEndpointRouteBuilder MapOnboardingStatus(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/onboarding/status", StatusHandler.HandleAsync)
            .WithTags("Onboarding")
            .WithName("GetOnboardingStatus")
            .WithSummary("What is configured, and what the wizard should show next")
            .WithDescription(
                "The SPA calls this before rendering anything. While hasAdministrator is false "
                + "every route leads to /setup (spec 7.1); afterwards /setup is a normal "
                + "authenticated page.\n\n"
                + "Returns no secret: never the VRChat password, the TOTP secret, the proxy "
                + "password, the Discord token or the SMTP password — only whether each is "
                + "stored (spec 5.9.3).\n\n"
                + "Unauthenticated deliberately and permanently. What it discloses to a stranger "
                + "is whether this deployment has finished being set up, which is already "
                + "obvious from whether the login page works.")
            .Produces<OnboardingStatusResponse>()
            .AllowAnonymous();

        return app;
    }
}
