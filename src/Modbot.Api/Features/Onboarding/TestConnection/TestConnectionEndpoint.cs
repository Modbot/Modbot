using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modbot.Api.Features.Onboarding.TestConnection;

public static class TestConnectionEndpoint
{
    public static IEndpointRouteBuilder MapTestConnection(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/onboarding/connection-test", TestConnectionHandler.HandleAsync)
            .WithTags("Onboarding")
            // The setup wizard's own steps, for the web app only: left out of the public API reference.
            .ExcludeFromDescription()
            .WithName("TestVRChatConnection")
            .WithSummary("Test this host's egress to VRChat, with or without a proxy (spec 7.1.1)")
            .WithDescription(
                "Answers 200 whether or not the connection worked: a failed check is still a "
                + "successful diagnosis, and the operator asked for the diagnosis.\n\n"
                + "The outcome distinguishes a Cloudflare WAF block from a DNS failure, a "
                + "timeout, a network error and rejected credentials, and only the first sets "
                + "proxyWouldHelp. That distinction is the reason this endpoint exists — "
                + "suggesting a proxy for the other four sends an operator somewhere that cannot "
                + "help them.\n\n"
                + "Re-runnable in place, and re-runnable later from settings, which is where the "
                + "gate's WafBlocked health state links to when a host that used to work stops.\n\n"
                + OnboardingAccess.Rule)
            .Produces<ConnectionDiagnosis>()
            .Produces(StatusCodes.Status400BadRequest)
            .RequiresOnboardingAccess(requireVRChatLink: false);

        return app;
    }
}
