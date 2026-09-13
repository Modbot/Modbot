using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modbot.Core.Data;

namespace Modbot.Api.Features.Onboarding.Complete;

public sealed record CompleteResponse(bool OnboardingComplete);

/// <summary>
/// Spec 7.1: the wizard is finished.
/// </summary>
/// <remarks>
/// <para>
/// This is a flag, not a gate. It records that an operator has been through the wizard once, so
/// the SPA stops opening on it — every individual step stays re-runnable from settings, and
/// nothing else in Modbot branches on this value.
/// </para>
/// <para>
/// It refuses while the parts Modbot cannot work without are missing, because a deployment marked
/// "set up" with no VRChat account and no group is one that will look broken with no explanation
/// of what is missing.
/// </para>
/// </remarks>
public static class CompleteEndpoint
{
    public static IEndpointRouteBuilder MapCompleteOnboarding(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/onboarding/complete", async (ModbotContext db, CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                if (settings.VRChatVerifiedAt is null)
                {
                    return Results.BadRequest(new
                    {
                        error = "The VRChat account step has not been completed successfully yet.",
                    });
                }

                if (string.IsNullOrWhiteSpace(settings.ManagedGroupId))
                    return Results.BadRequest(new { error = "No group has been selected yet." });

                settings.OnboardingComplete = true;
                await db.SaveChangesAsync(ct);

                return Results.Ok(new CompleteResponse(true));
            })
            .WithTags("Onboarding")
            .WithName("CompleteOnboarding")
            .WithSummary("Mark setup as finished (spec 7.1)")
            .WithDescription(
                "Refuses while the VRChat account is unverified or no group is chosen — the two "
                + "things Modbot cannot do anything without. The optional step is not among "
                + "them; skipping it is a supported way to finish.\n\n"
                + OnboardingAccess.Rule)
            .Produces<CompleteResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .RequiresOnboardingAccess();

        return app;
    }
}
