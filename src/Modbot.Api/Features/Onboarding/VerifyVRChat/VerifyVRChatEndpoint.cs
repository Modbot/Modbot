using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modbot.Api.Features.Onboarding.VerifyVRChat;

public static class VerifyVRChatEndpoint
{
    public static IEndpointRouteBuilder MapVerifyVRChat(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/onboarding/vrchat", VerifyVRChatHandler.HandleAsync)
            .WithTags("Onboarding")
            // The setup wizard's own steps, for the web app only: left out of the public API reference.
            .ExcludeFromDescription()
            .WithName("VerifyVRChatAccount")
            .WithSummary("Verify VRChat account")
            .WithDescription(
                "Store the VRChat account and log in with it (spec 7.1, step 2). "
                + "Validated live against the VRChat API, so a wrong credential fails here rather "
                + "than silently at the first sync.\n\n"
                + "A rejected attempt answers 422 with a ConnectionDiagnosis explaining which of "
                + "the failure modes it was — a Cloudflare block, DNS, a timeout or the "
                + "credentials themselves — because only the first of those is fixed by a proxy "
                + "(spec 7.1.1). The credentials stay stored so the proxy step can retry without "
                + "retyping them; only the verified timestamp is withheld.\n\n"
                + OnboardingAccess.Rule)
            .Produces<VerifyVRChatResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces<ConnectionDiagnosis>(StatusCodes.Status422UnprocessableEntity)
            .RequiresOnboardingAccess(requireVRChatLink: false);

        return app;
    }
}
