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
            // The setup wizard's own steps, for the web app only: left out of the public API reference.
            .ExcludeFromDescription()
            .WithName("GetOnboardingStatus")
            .WithSummary("Get setup status")
            .WithDescription(
                "What is configured, and what the wizard should show next. "
                + "The SPA calls this before rendering anything. While hasAdministrator is false "
                + "every route leads to /setup (spec 7.1); afterwards /setup is a normal "
                + "authenticated page.\n\n"
                + "Returns no secret: never the VRChat password, the TOTP secret, the proxy "
                + "password, the Discord token or the SMTP password — only whether each is "
                + "stored (spec 5.9.3).\n\n"
                + "Open to anyone, and what it answers depends on who asks. The short answer is "
                + "whether an administrator exists, whether the caller is signed in, whether setup "
                + "is finished, the next step, the managed group's id, name and pictures (which "
                + "GET /api/server already gives to anyone), the selector's address, whether pictures "
                + "load through this server, and whether the updates checkbox is offered. The "
                + "account details — the VRChat username and display name and when it last signed "
                + "in, the proxy address and username, the Discord server, channel and instance "
                + "message, the mail server's host and the public address — come back only while "
                + "no administrator exists yet, when the wizard has nobody to sign in as, and "
                + "afterwards only to a signed-in account holding ManageSettings. Everyone else "
                + "gets those fields empty.")
            .Produces<OnboardingStatusResponse>()
            .AllowAnonymous();

        return app;
    }
}
