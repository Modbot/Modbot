using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modbot.Api.Features.Onboarding.Integrations;

public static class IntegrationsEndpoint
{
    public static IEndpointRouteBuilder MapIntegrations(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/onboarding/integrations", IntegrationsHandler.HandleAsync)
            .WithTags("Onboarding")
            // The setup wizard's own steps, for the web app only: left out of the public API reference.
            .ExcludeFromDescription()
            .WithName("ConfigureIntegrations")
            .WithSummary("Discord bot and SMTP, both optional (spec 7.1, step 5)")
            .WithDescription(
                "Every field is optional and the whole step is skippable. Omitting a field leaves "
                + "the stored value alone; sending it empty clears it, which is how an "
                + "integration is switched off.\n\n"
                + "Secrets are encrypted at rest and never read back (spec 8.3, 5.9.3).\n\n"
                + OnboardingAccess.Rule)
            .Produces<IntegrationsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .RequiresOnboardingAccess();

        return app;
    }
}
